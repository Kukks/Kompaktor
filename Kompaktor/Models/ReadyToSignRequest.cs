using System.Text.Json.Serialization;

namespace Kompaktor.Contracts;

public record ReadyToSignRequest
{
    public ReadyToSignRequest(string Secret)
    {
        this.Secret = Secret;
    }
[JsonPropertyName("secret")]
    public string Secret { get; init; }

    public void Deconstruct(out string Secret)
    {
        Secret = this.Secret;
    }
}