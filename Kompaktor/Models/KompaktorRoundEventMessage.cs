using System.Text.Json.Serialization;

namespace Kompaktor.Models;

public record KompaktorRoundEventMessage : KompaktorRoundEvent
{
    public KompaktorRoundEventMessage(MessageRequest Request)
    {
        this.Request = Request;
    }

    public override string ToString()
    {
        return null;
    }
    
    [JsonPropertyName("request")]
    public MessageRequest Request { get; init; }

    public void Deconstruct(out MessageRequest Request)
    {
        Request = this.Request;
    }
}