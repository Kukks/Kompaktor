using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kompaktor.JsonConverters;
using Kompaktor.Utils;

namespace Kompaktor.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(KompaktorRoundEventCreated), "created")]
[JsonDerivedType(typeof(KompaktorRoundEventStatusUpdate), "status")]
[JsonDerivedType(typeof(KompaktorRoundEventInputRegistered), "inputRegistered")]
[JsonDerivedType(typeof(KompaktorRoundEventOutputRegistered), "outputRegistered")]
[JsonDerivedType(typeof(KompaktorRoundEventSignaturePosted), "signaturePosted")]
[JsonDerivedType(typeof(KompaktorRoundEventMessage), "message")]
[JsonDerivedType(typeof(KompaktorRoundEventTranscriptSigned), "transcriptSigned")]
public abstract record KompaktorRoundEvent
{
    [JsonIgnore]
    public virtual string Id => SHA256.HashData(KompaktorJsonHelper.SerializeToUtf8Bytes(this)).ToHex();
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


