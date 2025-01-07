using System.Text.Json.Serialization;
using Kompaktor.JsonConverters;
using NBitcoin;

namespace Kompaktor.Models;

public record SignRequest
{
    [JsonPropertyName("outpoint")]
    [JsonConverter(typeof(OutPointJsonConverter))]
    public OutPoint OutPoint { get; set; }
    [JsonPropertyName("witness")]
    [JsonConverter(typeof(WitScriptJsonConverter))]
    public WitScript Witness { get; set; }
}