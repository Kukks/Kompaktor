using System.Text.Json.Serialization;

namespace Kompaktor.Models;


public record MessageRequest
{
    public MessageRequest(byte[] message)
    {
        Message = message;
    }

    [JsonPropertyName("message")]
    public byte[] Message { get; init; }

}