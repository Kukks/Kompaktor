using Kompaktor.Credentials;
using NBitcoin;

namespace Kompaktor.Models;

public record KompaktorRoundEventCreated(
    string RoundId,
    FeeRate FeeRate,
    TimeSpan InputTimeout,
    TimeSpan OutputTimeout,
    TimeSpan SigningTimeout,
    IntRange InputCount,
    MoneyRange InputAmount,
    IntRange OutputCount,
    MoneyRange OutputAmount,
    Dictionary<CredentialType, CredentialConfiguation> Credentials)
    : KompaktorRoundEventStatusUpdate(KompaktorStatus.InputRegistration)
{
}