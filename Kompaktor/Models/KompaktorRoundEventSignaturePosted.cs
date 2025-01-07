using System.Text.Json.Serialization;

namespace Kompaktor.Models;

public record KompaktorRoundEventSignaturePosted : KompaktorRoundEvent
{
    public KompaktorRoundEventSignaturePosted(SignRequest Request)
    {
        this.Request = Request;
    }

    [JsonPropertyName("request")]
    public SignRequest Request { get; init; }

    public void Deconstruct(out SignRequest Request)
    {
        Request = this.Request;
    }
}