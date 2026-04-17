using Kompaktor.Credentials;
using NBitcoin;

namespace Kompaktor.Models;

public record KompaktorRoundEventCreated : KompaktorRoundEventStatusUpdate
{
    public KompaktorRoundEventCreated(string RoundId,
        FeeRate FeeRate,
        TimeSpan InputTimeout,
        TimeSpan OutputTimeout,
        TimeSpan SigningTimeout,
        IntRange InputCount,
        MoneyRange InputAmount,
        IntRange OutputCount,
        MoneyRange OutputAmount,
        Dictionary<CredentialType, CredentialConfiguration> Credentials,
        TimeSpan? InputRegistrationSoftTimeout = null) : base(KompaktorStatus.InputRegistration)
    {
        this.RoundId = RoundId;
        this.FeeRate = FeeRate;
        this.InputTimeout = InputTimeout;
        this.OutputTimeout = OutputTimeout;
        this.SigningTimeout = SigningTimeout;
        this.InputCount = InputCount;
        this.InputAmount = InputAmount;
        this.OutputCount = OutputCount;
        this.OutputAmount = OutputAmount;
        this.Credentials = Credentials;
        this.InputRegistrationSoftTimeout = InputRegistrationSoftTimeout;
    }

    public string RoundId { get; init; }
    public FeeRate FeeRate { get; init; }
    public TimeSpan InputTimeout { get; init; }
    public TimeSpan OutputTimeout { get; init; }
    public TimeSpan SigningTimeout { get; init; }
    public IntRange InputCount { get; init; }
    public MoneyRange InputAmount { get; init; }
    public IntRange OutputCount { get; init; }
    public MoneyRange OutputAmount { get; init; }
    public Dictionary<CredentialType, CredentialConfiguration> Credentials { get; init; }
    /// <summary>
    /// Optional soft timeout for input registration. After this period, the round will
    /// transition to output registration if the minimum input count is met.
    /// If null, the round waits for the full InputTimeout.
    /// </summary>
    public TimeSpan? InputRegistrationSoftTimeout { get; init; }

    /// <summary>
    /// Script types allowed for input registration. If empty, all supported types are allowed.
    /// </summary>
    public HashSet<ScriptType> AllowedInputTypes { get; init; } = [ScriptType.Taproot, ScriptType.P2WPKH];

    /// <summary>
    /// If set, this round is a blame round created from a failed parent round.
    /// Only inputs whose outpoints are in the whitelist may register.
    /// </summary>
    public string? BlameOf { get; init; }

    /// <summary>
    /// Outpoints allowed to register in a blame round. Null for normal rounds.
    /// </summary>
    public HashSet<OutPoint>? BlameWhitelist { get; init; }

    public bool IsBlameRound => BlameOf is not null;

    public void Deconstruct(out string RoundId, out FeeRate FeeRate, out TimeSpan InputTimeout, out TimeSpan OutputTimeout, out TimeSpan SigningTimeout, out IntRange InputCount, out MoneyRange InputAmount, out IntRange OutputCount, out MoneyRange OutputAmount, out Dictionary<CredentialType, CredentialConfiguration> Credentials)
    {
        RoundId = this.RoundId;
        FeeRate = this.FeeRate;
        InputTimeout = this.InputTimeout;
        OutputTimeout = this.OutputTimeout;
        SigningTimeout = this.SigningTimeout;
        InputCount = this.InputCount;
        InputAmount = this.InputAmount;
        OutputCount = this.OutputCount;
        OutputAmount = this.OutputAmount;
        Credentials = this.Credentials;
    }
}
