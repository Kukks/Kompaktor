using System.Text.Json.Serialization;
using Kompaktor.JsonConverters;
using NBitcoin.BIP322;
using WabiSabi.CredentialRequesting;

namespace Kompaktor.Models;

public record RegisterInputQuoteRequest
{
    [JsonConverter(typeof(SignatureJsonConverter))]
    [JsonPropertyName("signature")]
    
    public required BIP322Signature.Full Signature { get; init; }
    
    [JsonConverter(typeof(CredentialsRequestJsonConverter))]
    [JsonPropertyName("credentialsRequest")]
    public required ICredentialsRequest CredentialsRequest { get; init; }
}