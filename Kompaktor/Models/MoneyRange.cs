using System.Text.Json.Serialization;
using Kompaktor.JsonConverters;
using NBitcoin;

namespace Kompaktor.Models;

public record MoneyRange
{
    public MoneyRange(Money Min, Money Max)
    {
        if (Min > Max)
            throw new ArgumentException("Min must be less than or equal to Max.");
        this.Min = Min;
        this.Max = Max;
    }

    public bool Contains(Money value) => Min <= value && value <= Max;
    [JsonConverter(typeof(MoneyJsonConverter))]
    [JsonPropertyName("min")]
    public Money Min { get; init; }    
    [JsonConverter(typeof(MoneyJsonConverter))]
    [JsonPropertyName("max")]
    public Money Max { get; init; }
}