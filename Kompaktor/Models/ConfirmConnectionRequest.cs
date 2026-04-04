using System.Text.Json.Serialization;

namespace Kompaktor.Contracts;

public record ConfirmConnectionRequest
{
    public ConfirmConnectionRequest(string Secret)
    {
        this.Secret = Secret;
    }

    [JsonPropertyName("secret")]
    public string Secret { get; init; }
}
