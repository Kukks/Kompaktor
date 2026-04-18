namespace Kompaktor.Wallet.Data;

public class WalletEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public byte[] EncryptedMnemonic { get; set; } = [];
    public byte[] MnemonicSalt { get; set; } = [];
    public string Network { get; set; } = "RegTest";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<AccountEntity> Accounts { get; set; } = [];
}

public class AccountEntity
{
    public int Id { get; set; }
    public string WalletId { get; set; } = "";
    public int Purpose { get; set; } // 84 = P2WPKH, 86 = P2TR
    public int AccountIndex { get; set; }
    /// <summary>
    /// BIP-32 account-level extended public key (base58 string, e.g. xpub/tpub).
    /// Enables watch-only address derivation for gap limit extension
    /// without requiring the master private key.
    /// </summary>
    public string? AccountXPub { get; set; }

    public WalletEntity Wallet { get; set; } = null!;
    public List<AddressEntity> Addresses { get; set; } = [];
}

public class AddressEntity
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string KeyPath { get; set; } = ""; // e.g. "0/5" (chain/index)
    public byte[] ScriptPubKey { get; set; } = [];
    public bool IsChange { get; set; }
    public bool IsUsed { get; set; }
    public bool IsExposed { get; set; }
    public int? RoutingGroup { get; set; }

    public AccountEntity Account { get; set; } = null!;
    public List<UtxoEntity> Utxos { get; set; } = [];
}

public class UtxoEntity
{
    public int Id { get; set; }
    public string TxId { get; set; } = "";
    public int OutputIndex { get; set; }
    public int AddressId { get; set; }
    public long AmountSat { get; set; }
    public byte[] ScriptPubKey { get; set; } = [];
    public int? ConfirmedHeight { get; set; }
    public string? SpentByTxId { get; set; }
    public bool IsCoinBase { get; set; }
    public bool IsFrozen { get; set; }
    /// <summary>
    /// True if this UTXO was created as an output of a completed coinjoin round.
    /// Used to allow unconfirmed coinjoin output reuse for faster re-mixing.
    /// </summary>
    public bool IsCoinJoinOutput { get; set; }

    public AddressEntity Address { get; set; } = null!;
}

public class TransactionEntity
{
    public string Id { get; set; } = ""; // TX hash
    public string RawHex { get; set; } = "";
    public int? BlockHeight { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public class CoinJoinRecordEntity
{
    public int Id { get; set; }
    public string? TransactionId { get; set; }
    public string RoundId { get; set; } = "";
    public string Status { get; set; } = ""; // "Completed" or "Failed"
    public int OurInputCount { get; set; }
    public int TotalInputCount { get; set; }
    public int OurOutputCount { get; set; }
    public int TotalOutputCount { get; set; }
    public int ParticipantCount { get; set; }
    public long[] OutputValuesSat { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public TransactionEntity? Transaction { get; set; }
    public List<CoinJoinParticipationEntity> Participations { get; set; } = [];
}

public class CoinJoinParticipationEntity
{
    public int Id { get; set; }
    public int CoinJoinRecordId { get; set; }
    public int UtxoId { get; set; }
    public string Role { get; set; } = ""; // "Input" or "Output"

    public CoinJoinRecordEntity CoinJoinRecord { get; set; } = null!;
    public UtxoEntity Utxo { get; set; } = null!;
}

public class CredentialEventEntity
{
    public int Id { get; set; }
    public int CoinJoinRecordId { get; set; }
    public string CredentialSerial { get; set; } = "";
    public string EventType { get; set; } = ""; // "Acquired", "Reissued", "Spent"
    public long AmountSat { get; set; }
    public int? ParentEventId { get; set; }
    public int? OutputUtxoId { get; set; }
    public int GraphDepth { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public CoinJoinRecordEntity CoinJoinRecord { get; set; } = null!;
    public CredentialEventEntity? ParentEvent { get; set; }
    public UtxoEntity? OutputUtxo { get; set; }
}

public class AddressBookEntry
{
    public int Id { get; set; }
    public string WalletId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Address { get; set; } = ""; // Bitcoin address string (e.g. bc1q...)
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public WalletEntity Wallet { get; set; } = null!;
}

public class LabelEntity
{
    public int Id { get; set; }
    public string EntityType { get; set; } = ""; // "Address", "Transaction", "Utxo", "CoinJoin"
    public string EntityId { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A pending payment waiting to be fulfilled in a CoinJoin round.
/// Supports both outbound (sending) and inbound (receiving) interactive payments.
/// </summary>
public class PendingPaymentEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string WalletId { get; set; } = "";
    /// <summary>"Outbound" or "Inbound"</summary>
    public string Direction { get; set; } = "";
    public long AmountSat { get; set; }
    /// <summary>Bitcoin address string (e.g. bc1q...)</summary>
    public string Destination { get; set; } = "";
    /// <summary>"Pending", "Reserved", "Committed", "Completed", "Failed"</summary>
    public string Status { get; set; } = "Pending";
    /// <summary>
    /// For interactive payments: the Kompaktor protocol key (hex-encoded).
    /// Outbound: sender's public key. Inbound: receiver's private key.
    /// Null for non-interactive payments.
    /// </summary>
    public string? KompaktorKeyHex { get; set; }
    /// <summary>Whether this is an interactive (peer-to-peer credential transfer) payment</summary>
    public bool IsInteractive { get; set; }
    /// <summary>Urgent payments fall back to non-interactive if peer is offline</summary>
    public bool IsUrgent { get; set; }
    /// <summary>Optional label for display</summary>
    public string? Label { get; set; }
    /// <summary>Serialized proof once the payment completes</summary>
    public string? ProofJson { get; set; }
    /// <summary>Transaction ID if the payment was included in a broadcast CoinJoin</summary>
    public string? CompletedTxId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Number of times this payment has been retried after round failures</summary>
    public int RetryCount { get; set; }
    /// <summary>Optional expiration time — expired pending payments are auto-cancelled</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// Point-in-time snapshot of wallet privacy metrics.
/// Recorded after each completed coinjoin round to track anonymity improvement over time.
/// </summary>
public class PrivacySnapshotEntity
{
    public int Id { get; set; }
    public string WalletId { get; set; } = "";
    public int TotalUtxos { get; set; }
    public long TotalAmountSat { get; set; }
    public double AverageAnonScore { get; set; }
    public double MinAnonScore { get; set; }
    public double MaxAnonScore { get; set; }
    public int MixedUtxoCount { get; set; }
    public int UnmixedUtxoCount { get; set; }
    public int CoinJoinRoundNumber { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Records which inputs were co-registered in a failed round.
/// Used by the intersection attack tracker to prevent the same wallet from
/// re-disclosing coin pairings across rounds after service restarts.
/// </summary>
public class FailedRoundInputEntity
{
    public int Id { get; set; }
    public string WalletId { get; set; } = "";
    public string RoundGroupId { get; set; } = ""; // Groups inputs from the same failed round
    public string TxId { get; set; } = "";
    public int OutputIndex { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
