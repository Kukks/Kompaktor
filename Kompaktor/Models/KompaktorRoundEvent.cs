using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kompaktor.JsonConverters;
using Kompaktor.Utils;

namespace Kompaktor.Models;

public abstract record KompaktorRoundEvent
{
    [JsonIgnore]
    public virtual string Id => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)).ToHex();
    [JsonPropertyName("parentEventId")]
    public string? ParentEventId { get; }
    [JsonConverter(typeof(UnixToNullableDateTimOffsetConverter))]
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public override string ToString()
    {
        return GetType().Name;
    }
}


