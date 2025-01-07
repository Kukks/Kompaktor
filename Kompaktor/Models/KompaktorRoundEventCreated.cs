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
        Dictionary<CredentialType, CredentialConfiguration> Credentials) : base(KompaktorStatus.InputRegistration)
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