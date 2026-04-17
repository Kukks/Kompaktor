using NBitcoin;

namespace Kompaktor.Blockchain;

/// <summary>
/// Abstraction over blockchain data access, replacing direct RPCClient usage
/// in both coordinator and wallet code.
/// </summary>
public interface IBlockchainBackend : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct = default);
    bool IsConnected { get; }

    // UTXO queries
    Task<UtxoInfo?> GetUtxoAsync(OutPoint outpoint, CancellationToken ct = default);
    Task<IList<UtxoInfo>> GetUtxosForScriptAsync(Script scriptPubKey, CancellationToken ct = default);
    Task<Transaction?> GetTransactionAsync(uint256 txId, CancellationToken ct = default);

    // Broadcasting
    Task<uint256> BroadcastAsync(Transaction tx, CancellationToken ct = default);

    // Address monitoring
    Task SubscribeAddressAsync(Script scriptPubKey, CancellationToken ct = default);
    Task UnsubscribeAddressAsync(Script scriptPubKey, CancellationToken ct = default);
    event EventHandler<AddressNotification> AddressNotified;

    // Fee estimation
    Task<FeeRate> EstimateFeeAsync(int confirmationTarget = 6, CancellationToken ct = default);

    // Block height
    Task<int> GetBlockHeightAsync(CancellationToken ct = default);
}

/// <summary>
/// Wire DTO for blockchain UTXO queries — distinct from UtxoEntity (persisted wallet state).
/// </summary>
public record UtxoInfo(OutPoint OutPoint, TxOut TxOut, int Confirmations, bool IsCoinBase);

/// <summary>
/// Notification fired when a subscribed address receives or sends a transaction.
/// </summary>
public record AddressNotification(Script ScriptPubKey, uint256 TxId, int? Height);
