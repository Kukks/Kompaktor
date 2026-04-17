using System.Collections.Concurrent;
using NBitcoin;
using NBitcoin.RPC;

namespace Kompaktor.Blockchain;

/// <summary>
/// IBlockchainBackend adapter wrapping NBitcoin's RPCClient.
/// Address subscriptions are tracked but not polled — Bitcoin Core has no push notifications.
/// </summary>
public class BitcoinCoreBackend : IBlockchainBackend
{
    private readonly RPCClient _rpc;
    private readonly ConcurrentDictionary<Script, CancellationTokenSource> _subscriptions = new();
    private bool _connected;

    public BitcoinCoreBackend(RPCClient rpc)
    {
        _rpc = rpc;
    }

    public bool IsConnected => _connected;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _rpc.GetBlockCountAsync();
        _connected = true;
    }

    public async Task<UtxoInfo?> GetUtxoAsync(OutPoint outpoint, CancellationToken ct = default)
    {
        var result = await _rpc.GetTxOutAsync(outpoint.Hash, (int)outpoint.N);
        if (result is null) return null;
        return new UtxoInfo(
            outpoint,
            new TxOut(result.TxOut.Value, result.TxOut.ScriptPubKey),
            result.Confirmations,
            result.IsCoinBase);
    }

    public Task<IList<UtxoInfo>> GetUtxosForScriptAsync(Script scriptPubKey, CancellationToken ct = default)
    {
        // Bitcoin Core RPC does not support querying UTXOs by script directly
        // without wallet import. Return empty for coordinator use.
        return Task.FromResult<IList<UtxoInfo>>(Array.Empty<UtxoInfo>());
    }

    public async Task<Transaction?> GetTransactionAsync(uint256 txId, CancellationToken ct = default)
    {
        try
        {
            return await _rpc.GetRawTransactionAsync(txId);
        }
        catch (RPCException ex) when (ex.RPCCode == RPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY)
        {
            return null;
        }
    }

    public async Task<uint256> BroadcastAsync(Transaction tx, CancellationToken ct = default)
    {
        // Pass maxfeerate=0 to skip the per-kVB fee rate check;
        // the node's -maxtxfee still caps the absolute fee.
        var resp = await _rpc.SendCommandAsync("sendrawtransaction", tx.ToHex(), 0);
        resp.ThrowIfError();
        return uint256.Parse(resp.Result.ToString()!);
    }

    public Task SubscribeAddressAsync(Script scriptPubKey, CancellationToken ct = default)
    {
        _subscriptions.TryAdd(scriptPubKey, new CancellationTokenSource());
        return Task.CompletedTask;
    }

    public Task UnsubscribeAddressAsync(Script scriptPubKey, CancellationToken ct = default)
    {
        if (_subscriptions.TryRemove(scriptPubKey, out var cts))
            cts.Cancel();
        return Task.CompletedTask;
    }

    public event EventHandler<AddressNotification>? AddressNotified;

    public async Task<FeeRate> EstimateFeeAsync(int confirmationTarget = 6, CancellationToken ct = default)
    {
        var result = await _rpc.EstimateSmartFeeAsync(confirmationTarget);
        return result.FeeRate;
    }

    public async Task<int> GetBlockHeightAsync(CancellationToken ct = default)
    {
        return await _rpc.GetBlockCountAsync();
    }

    public ValueTask DisposeAsync()
    {
        foreach (var kvp in _subscriptions)
        {
            kvp.Value.Cancel();
            kvp.Value.Dispose();
        }
        _subscriptions.Clear();
        return ValueTask.CompletedTask;
    }
}
