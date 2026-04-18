using NBitcoin;

namespace Kompaktor.Models;

/// <summary>
/// Configuration for a Kompaktor round coordinator.
/// </summary>
public class KompaktorCoordinatorOptions
{
    /// <summary>Fee rate for the coinjoin transaction (satoshis per byte).</summary>
    public FeeRate FeeRate { get; set; } = new(2m);

    /// <summary>Duration of the input registration phase.</summary>
    public TimeSpan InputTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Duration of the output registration phase.</summary>
    public TimeSpan OutputTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Duration of the signing phase.</summary>
    public TimeSpan SigningTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Minimum number of inputs required for a round to proceed.</summary>
    public int MinInputCount { get; set; } = 1;

    /// <summary>Maximum number of inputs allowed in a round.</summary>
    public int MaxInputCount { get; set; } = 1000;

    /// <summary>Minimum input amount (satoshis).</summary>
    public Money MinInputAmount { get; set; } = Money.Satoshis(10000);

    /// <summary>Maximum input amount (satoshis).</summary>
    public Money MaxInputAmount { get; set; } = Money.Coins(100);

    /// <summary>Minimum output amount (satoshis).</summary>
    public Money MinOutputAmount { get; set; } = Money.Satoshis(10000);

    /// <summary>Maximum output amount (satoshis).</summary>
    public Money MaxOutputAmount { get; set; } = Money.Coins(100);

    /// <summary>Minimum number of outputs required.</summary>
    public int MinOutputCount { get; set; } = 1;

    /// <summary>Maximum number of outputs allowed.</summary>
    public int MaxOutputCount { get; set; } = 10000;

    /// <summary>Maximum credential value for WabiSabi issuance.</summary>
    public long MaxCredentialValue { get; set; } = 4300000000000L; // ~43 BTC

    /// <summary>
    /// Number of credentials per issuance step (k in the WabiSabi paper).
    /// Higher values reduce reissuance tree depth (fewer round-trips) at the cost of
    /// larger proofs per step. Default 2 matches the original WabiSabi protocol.
    /// </summary>
    public int CredentialCount { get; set; } = 2;

    /// <summary>
    /// Use Bulletproofs++ (BP++) for range proofs instead of classical sigma-protocol bit decomposition.
    /// BP++ has O(log n) proof size vs O(n), making it much faster at higher credential arity (k).
    /// Enabled by default — benchmarks show 39% faster at scale (n=100) vs classical proofs.
    /// </summary>
    public bool UseBulletproofs { get; set; } = true;

    /// <summary>
    /// Optional soft timeout for input registration. After this period, the round will
    /// transition to output registration early if the minimum input count is already met.
    /// If null, the round waits for the full InputTimeout before transitioning.
    /// </summary>
    public TimeSpan? InputRegistrationSoftTimeout { get; set; }

    /// <summary>Allow P2WPKH (SegWit v0) inputs and outputs.</summary>
    public bool AllowP2wpkh { get; set; } = true;

    /// <summary>Allow P2TR (Taproot) inputs and outputs.</summary>
    public bool AllowP2tr { get; set; } = true;

    /// <summary>Allow P2PKH (legacy) outputs only.</summary>
    public bool AllowP2pkhOutputs { get; set; } = false;

    /// <summary>Allow P2SH outputs only.</summary>
    public bool AllowP2shOutputs { get; set; } = false;

    /// <summary>Allow P2WSH outputs only.</summary>
    public bool AllowP2wshOutputs { get; set; } = false;

    /// <summary>
    /// Base amount for the round tiering system. Tiers are powers of 10 from this base.
    /// For example, 0.1 BTC base creates tiers: 0.1, 1.0, 10.0 BTC.
    /// If null, all rounds use the full input range (no tiering).
    /// </summary>
    public Money? MaxSuggestedAmountBase { get; set; }

    /// <summary>Maximum number of concurrent rounds.</summary>
    public int MaxConcurrentRounds { get; set; } = 10;

    /// <summary>Time between automatic round creation.</summary>
    public TimeSpan RoundInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Hex-encoded coordinator signing private key for transcript signatures.
    /// If null, an ephemeral key is generated at startup by the round manager.
    /// Ephemeral keys do not survive restarts, so clients cannot verify transcript
    /// continuity across coordinator restarts. Configure a persistent key for production.
    /// </summary>
    public string? CoordinatorSigningKeyHex { get; set; }
}

/// <summary>
/// Configuration for a Kompaktor round client (wallet participant).
/// </summary>
public class KompaktorClientOptions
{
    /// <summary>Maximum number of coins to register per round.</summary>
    public int MaxCoinsPerRound { get; set; } = 10;

    /// <summary>Whether to automatically consolidate UTXOs when count is low.</summary>
    public bool AutoConsolidate { get; set; } = true;

    /// <summary>Minimum number of coins to trigger consolidation.</summary>
    public int ConsolidationThreshold { get; set; } = 3;

    /// <summary>Maximum number of concurrent interactive payment flows.</summary>
    public int MaxConcurrentInteractiveFlows { get; set; } = 50;

    /// <summary>Timeout for individual API calls to the coordinator.</summary>
    public TimeSpan ApiCallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Number of retries for transient API failures.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay for exponential backoff on retries.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}
