// using WabiSabi.CredentialRequesting;
// using WalletWasabi.WabiSabi.Client.CredentialDependencies;
//
// namespace WalletWasabi.Crypto;
//

using System.Text.Json.Serialization;
using Kompaktor.Credentials;
using Kompaktor.JsonConverters;
using WabiSabi.CredentialRequesting;

namespace Kompaktor.Models;

public record KompaktorRoundCredentialReissuanceResponse
{
    public KompaktorRoundCredentialReissuanceResponse(Dictionary<CredentialType, CredentialsResponse> Credentials)
    {
        this.Credentials = Credentials;
    }
    [JsonPropertyName("credentials")]   
    [JsonConverter(typeof(ItemConverterDecorator<CredentialsResponseJsonConverter>))]


    public Dictionary<CredentialType, CredentialsResponse> Credentials { get; init; }

    public void Deconstruct(out Dictionary<CredentialType, CredentialsResponse> Credentials)
    {
        Credentials = this.Credentials;
    }
}