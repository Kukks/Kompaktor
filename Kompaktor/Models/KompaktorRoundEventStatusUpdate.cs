using System.Text.Json.Serialization;

namespace Kompaktor.Models;

public record KompaktorRoundEventStatusUpdate : KompaktorRoundEvent
{
    public KompaktorRoundEventStatusUpdate(KompaktorStatus Status)
    {
        this.Status = Status;
    }

    public override string ToString() => $"Status Update: {Status}";
    
    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter<KompaktorStatus>))]
    public KompaktorStatus Status { get; init; }

    public void Deconstruct(out KompaktorStatus Status)
    {
        Status = this.Status;
    }
}