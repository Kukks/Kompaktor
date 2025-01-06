using System.Text.Json.Serialization;

namespace Kompaktor.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KompaktorStatus
{
    InputRegistration,
    OutputRegistration,
    Signing,
    Broadcasting,
    Completed,
    Failed
}