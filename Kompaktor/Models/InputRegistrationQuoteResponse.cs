using System.Text.Json.Serialization;
using Kompaktor.JsonConverters;
using WabiSabi.CredentialRequesting;

namespace Kompaktor.Contracts;

public record InputRegistrationQuoteResponse
{
    public InputRegistrationQuoteResponse(string Secret, CredentialsResponse CredentialsResponse, long CredentialAmount)
    {
        this.Secret = Secret;
        this.CredentialsResponse = CredentialsResponse;
        this.CredentialAmount = CredentialAmount;
    }

    [JsonPropertyName("secret")]
    public string Secret { get; init; }
    
    [JsonPropertyName("credentialsResponse")]
    [JsonConverter(typeof(CredentialsResponseJsonConverter))]
    public CredentialsResponse CredentialsResponse { get; init; }
    [JsonPropertyName("credentialAmount")]
    public long CredentialAmount { get; init; }

    public void Deconstruct(out string Secret, out CredentialsResponse CredentialsResponse, out long CredentialAmount)
    {
        Secret = this.Secret;
        CredentialsResponse = this.CredentialsResponse;
        CredentialAmount = this.CredentialAmount;
    }
}