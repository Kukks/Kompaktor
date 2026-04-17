using System.Collections.Concurrent;
using NBitcoin;

namespace Kompaktor.Blockchain;

/// <summary>
/// IBlockchainBackend that distributes address-linked operations across multiple Electrum
/// servers. Each script is assigned to a routing group (server index), ensuring no single
/// server sees all wallet addresses. Non-address operations (tx lookup, fee estimation)
/// are load-balanced across all servers. Broadcasts go to ALL servers for redundancy.
/// </summary>
public class MultiServerBackend : IBlockchainBackend
{
    private readonly ElectrumBackend[] _backends;
    private readonly MultiServerOptions _options;
    private readonly ConcurrentDictionary<string, int> _scriptToServer = new();
    private int _roundRobinIndex;
    private int _loadBalanceIndex;

    public MultiServerBackend(MultiServerOptions options)
    {
        _options = options;
        _backends = options.Servers.Select(s => new ElectrumBackend(new ElectrumOptions
        {
            Host = s.Host,
            Port = s.Port,
            UseSsl = s.UseSsl
        })).ToArray();
    }

    /// <summary>
    /// Constructor for testing — accepts pre-built backends.
    /// </summary>
    internal MultiServerBackend(MultiServerOptions options, ElectrumBackend[] backends)
    {
        _options = options;
        _backends = backends;
    }

    public bool IsConnected => _backends.All(b => b.IsConnected);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await Task.WhenAll(_backends.Select(b => b.ConnectAsync(ct)));
    }

    /// <summary>
    /// Assign a script to a specific routing group (server index).
    /// </summary>
    public void AssignRouting(Script script, int routingGroup)
    {
        var hash = ElectrumBackend.ToElectrumScriptHash(script);
        _scriptToServer[hash] = routingGroup % _backends.Length;
    }

    /// <summary>
    /// Auto-assign a script using the configured strategy.
    /// Returns the assigned routing group.
    /// </summary>
    public int AssignRoutingAuto(Script script)
    {
        var hash = ElectrumBackend.ToElectrumScriptHash(script);
        if (_scriptToServer.TryGetValue(hash, out var existing))
            return existing;

        var group = _options.Strategy switch
        {
            RoutingStrategy.RoundRobin => Interlocked.Increment(ref _roundRobinIndex) % _backends.Length,
            RoutingStrategy.Random => Random.Shared.Next(_backends.Length),
            _ => 0
        };

        _scriptToServer[hash] = group;
        return group;
    }

    public async Task SubscribeAddressAsync(Script scriptPubKey, CancellationToken ct = default)
    {
        var backend = GetBackendForScript(scriptPubKey);
        await backend.SubscribeAddressAsync(scriptPubKey, ct);
    }

    public Task UnsubscribeAddressAsync(Script scriptPubKey, CancellationToken ct = default)
    {
        var backend = GetBackendForScript(scriptPubKey);
        return backend.UnsubscribeAddressAsync(scriptPubKey, ct);
    }

    public async Task<IList<UtxoInfo>> GetUtxosForScriptAsync(Script scriptPubKey, CancellationToken ct = default)
    {
        var backend = GetBackendForScript(scriptPubKey);
        return await backend.GetUtxosForScriptAsync(scriptPubKey, ct);
    }

    // Non-address operations — load balanced across servers
    public async Task<UtxoInfo?> GetUtxoAsync(OutPoint outpoint, CancellationToken ct = default)
    {
        var backend = GetNextLoadBalanced();
        return await backend.GetUtxoAsync(outpoint, ct);
    }

    public async Task<Transaction?> GetTransactionAsync(uint256 txId, CancellationToken ct = default)
    {
        var backend = GetNextLoadBalanced();
        return await backend.GetTransactionAsync(txId, ct);
    }

    // Broadcast to ALL servers for redundancy and to prevent timing analysis
    public async Task<uint256> BroadcastAsync(Transaction tx, CancellationToken ct = default)
    {
        var tasks = _backends.Select(b => b.BroadcastAsync(tx, ct)).ToArray();
        var results = await Task.WhenAll(tasks);
        return results[0]; // All should return the same txid
    }

    public async Task<FeeRate> EstimateFeeAsync(int confirmationTarget = 6, CancellationToken ct = default)
    {
        var backend = GetNextLoadBalanced();
        return await backend.EstimateFeeAsync(confirmationTarget, ct);
    }

    public async Task<int> GetBlockHeightAsync(CancellationToken ct = default)
    {
        var heights = await Task.WhenAll(_backends.Select(b => b.GetBlockHeightAsync(ct)));
        return heights.Max();
    }

    public event EventHandler<AddressNotification>? AddressNotified
    {
        add
        {
            foreach (var backend in _backends)
                backend.AddressNotified += value;
        }
        remove
        {
            foreach (var backend in _backends)
                backend.AddressNotified -= value;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var backend in _backends)
            await backend.DisposeAsync();
    }

    private ElectrumBackend GetBackendForScript(Script script)
    {
        var hash = ElectrumBackend.ToElectrumScriptHash(script);
        if (_scriptToServer.TryGetValue(hash, out var serverIndex))
            return _backends[serverIndex];

        // Auto-assign if not yet routed
        var group = AssignRoutingAuto(script);
        return _backends[group];
    }

    private ElectrumBackend GetNextLoadBalanced()
    {
        var idx = Interlocked.Increment(ref _loadBalanceIndex);
        return _backends[(idx & 0x7FFFFFFF) % _backends.Length];
    }
}
