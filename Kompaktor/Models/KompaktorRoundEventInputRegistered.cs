using System.Text.Json.Serialization;
using Kompaktor.JsonConverters;
using NBitcoin;
using WabiSabi.CredentialRequesting;

namespace Kompaktor.Models;

public record KompaktorRoundEventInputRegistered : KompaktorRoundEvent
{
    public KompaktorRoundEventInputRegistered(RegisterInputQuoteRequest QuoteRequest,
        CredentialsResponse CredentialsResponse,
        Coin Coin)
    {
        this.QuoteRequest = QuoteRequest;
        this.CredentialsResponse = CredentialsResponse;
        this.Coin = Coin;
    }

    public override string ToString() => $"Input Registered: {Coin.Outpoint} -> {Coin.Amount}";
    [JsonPropertyName("quoteRequest")]
    public RegisterInputQuoteRequest QuoteRequest { get; init; }
    [JsonPropertyName("credentialsResponse")]
    [JsonConverter(typeof(CredentialsResponseJsonConverter))]
    public CredentialsResponse CredentialsResponse { get; init; }
    [JsonPropertyName("coin")]
    [JsonConverter(typeof(CoinJsonConverter))]
    public Coin Coin { get; init; }

    public void Deconstruct(out RegisterInputQuoteRequest QuoteRequest, out CredentialsResponse CredentialsResponse, out Coin Coin)
    {
        QuoteRequest = this.QuoteRequest;
        CredentialsResponse = this.CredentialsResponse;
        Coin = this.Coin;
    }
}