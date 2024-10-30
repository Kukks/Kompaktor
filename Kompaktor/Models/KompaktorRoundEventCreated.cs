using Kompaktor.Credentials;
using NBitcoin;
using WabiSabi.Crypto;

namespace Kompaktor.Models;

public record Range
{
    public Range(int Min, int Max)
    {
        if (Min > Max)
            throw new ArgumentException("Min must be less than or equal to Max.");
        this.Min = Min;
        this.Max = Max;
    }

    public bool Contains(int value) => Min <= value && value <= Max;
    public int Min { get; init; }
    public int Max { get; init; }
}
public record LongRange
{
    public LongRange(long Min, long Max)
    {
        if (Min > Max)
            throw new ArgumentException("Min must be less than or equal to Max.");
        this.Min = Min;
        this.Max = Max;
    }

    public bool Contains(long value) => Min <= value && value <= Max;
    public long Min { get; init; }
    public long Max { get; init; }
}

public record MoneyRange
{
    public MoneyRange(Money Min, Money Max)
    {
        if (Min > Max)
            throw new ArgumentException("Min must be less than or equal to Max.");
        this.Min = Min;
        this.Max = Max;
    }

    public bool Contains(Money value) => Min <= value && value <= Max;
    public Money Min { get; init; }
    public Money Max { get; init; }
}

public record KompaktorRoundEventCreated(
    string RoundId,
    FeeRate FeeRate,
    TimeSpan InputTimeout,
    TimeSpan OutputTimeout,
    TimeSpan SigningTimeout,
    Range InputCount,
    MoneyRange InputAmount,
    Range OutputCount,
    MoneyRange OutputAmount,
    Dictionary<CredentialType, long> CredentialAmount,
    Dictionary<CredentialType, CredentialIssuerParameters> CredentialIssuerParameters)
    : KompaktorRoundEventStatusUpdate(KompaktorStatus.InputRegistration)
{

}