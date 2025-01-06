using System.Text.Json.Serialization;
using Kompaktor.JsonConverters;
using NBitcoin.BIP322;
using WabiSabi.CredentialRequesting;

namespace Kompaktor.Models;

public record RegisterInputQuoteRequest
{
    [JsonConverter(typeof(SignatureJsonConverter))]
    public required BIP322Signature.Full Signature { get; init; }
    
    [JsonConverter(typeof(CredentialsRequestJsonConverter))]
    public required ICredentialsRequest CredentialsRequest { get; init; }
}