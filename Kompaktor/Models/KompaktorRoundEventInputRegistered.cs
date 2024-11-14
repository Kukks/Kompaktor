using NBitcoin;
using WabiSabi.CredentialRequesting;

namespace Kompaktor.Models;

public record KompaktorRoundEventInputRegistered(
    RegisterInputQuoteRequest QuoteRequest,
    CredentialsResponse CredentialsResponse,
    Coin Coin)
    : KompaktorRoundEvent
{

    public override string ToString() => $"Input Registered: {Coin.Outpoint} -> {Coin.Amount}";
}