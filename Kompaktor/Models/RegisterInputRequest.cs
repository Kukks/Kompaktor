using System.Text.Json.Serialization;
using Kompaktor.JsonConverters;
using WabiSabi.CredentialRequesting;

namespace Kompaktor.Contracts;

public record RegisterInputRequest
{
    public RegisterInputRequest(string Secret, ICredentialsRequest CredentialsRequest)
    {
        this.Secret = Secret;
        this.CredentialsRequest = CredentialsRequest;
    }

    [JsonPropertyName("secret")]
    public string Secret { get; init; }
    [JsonPropertyName("credentialsRequest")]
    [JsonConverter(typeof(CredentialsRequestJsonConverter))]
    public ICredentialsRequest CredentialsRequest { get; init; }

    public void Deconstruct(out string Secret, out ICredentialsRequest CredentialsRequest)
    {
        Secret = this.Secret;
        CredentialsRequest = this.CredentialsRequest;
    }
}