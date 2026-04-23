using System.Collections.Concurrent;
using Kompaktor.Blockchain;
using NBitcoin;

namespace Kompaktor.Tests.E2E;

/// <summary>
/// Minimal in-memory IBlockchainBackend for E2E tests.
/// Does not talk to any external service. Records broadcast transactions
/// and allows the test to stage UTXOs / transactions / fee rate responses.
/// </summary>
public class FakeBlockchainBackend : IBlockchainBackend
{
    private readonly ConcurrentDictionary<OutPoint, UtxoInfo> _utxos = new();
    private readonly ConcurrentDictionary<uint256, Transaction> _transactions = new();
    private readonly ConcurrentBag<Script> _subscribedScripts = [];

    public readonly List<Transaction> BroadcastedTransactions = [];

    public int BlockHeight { get; set; } = 800_000;
    public FeeRate FeeRate { get; set; } = new(Money.Satoshis(1000));
    public bool IsConnected { get; private set; } = true;

    public event EventHandler<AddressNotification>? AddressNotified;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public void StageUtxo(UtxoInfo utxo) => _utxos[utxo.OutPoint] = utxo;
    public void StageTransaction(Transaction tx) => _transactions[tx.GetHash()] = tx;

    public void RaiseAddressNotification(Script script, uint256 txId, int? height)
        => AddressNotified?.Invoke(this, new AddressNotification(script, txId, height));

    public Task<UtxoInfo?> GetUtxoAsync(OutPoint outpoint, CancellationToken ct = default)
        => Task.FromResult(_utxos.TryGetValue(outpoint, out var u) ? u : null);

    public Task<IList<UtxoInfo>> GetUtxosForScriptAsync(Script scriptPubKey, CancellationToken ct = default)
        => Task.FromResult<IList<UtxoInfo>>(
            _utxos.Values.Where(u => u.TxOut.ScriptPubKey == scriptPubKey).ToList());

    public Task<Transaction?> GetTransactionAsync(uint256 txId, CancellationToken ct = default)
        => Task.FromResult(_transactions.TryGetValue(txId, out var tx) ? tx : null);

    public Task<uint256> BroadcastAsync(Transaction tx, CancellationToken ct = default)
    {
        BroadcastedTransactions.Add(tx);
        _transactions[tx.GetHash()] = tx;
        return Task.FromResult(tx.GetHash());
    }

    public Task SubscribeAddressAsync(Script scriptPubKey, CancellationToken ct = default)
    {
        _subscribedScripts.Add(scriptPubKey);
        return Task.CompletedTask;
    }

    public Task UnsubscribeAddressAsync(Script scriptPubKey, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<FeeRate> EstimateFeeAsync(int confirmationTarget = 6, CancellationToken ct = default)
        => Task.FromResult(FeeRate);

    public Task<int> GetBlockHeightAsync(CancellationToken ct = default)
        => Task.FromResult(BlockHeight);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
