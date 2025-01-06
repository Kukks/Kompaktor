using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kompaktor.JsonConverters;

/// <summary>
/// Converts Unix time to nullable DateTimeOffset.
/// </summary>
public class UnixToNullableDateTimOffsetConverter : JsonConverter<DateTimeOffset>
{
    /// <summary>
    /// Minimum Unix time in seconds.
    /// </summary>
    private static readonly long s_unixMinSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();

    /// <summary>
    /// Maximum Unix time in seconds.
    /// </summary>
    private static readonly long s_unixMaxSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <summary>
    /// Determines if the time should be formatted as seconds. False if resolved as milliseconds.
    /// </summary>
    public bool FormatAsSeconds { get; init; } = true;

    /// <summary>
    /// Reads and converts the JSON to type T.
    /// </summary>
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        long time;

        if ((reader.TokenType == JsonTokenType.String && long.TryParse(reader.GetString(), out time)) ||
            reader.TryGetInt64(out time))
        {
            // If FormatAsSeconds is not specified, the correct type is derived depending on whether
            //    the value can be represented as seconds within the .NET DateTimeOffset min/max range 0001-1-1 to 9999-12-31.

            // Since this is a 64-bit value, the Unixtime in seconds may exceed
            //    the 32-bit min/max restrictions 1/1/1970-1-1 to 1/19/2038-1-19.
            if (FormatAsSeconds || !FormatAsSeconds && time > s_unixMinSeconds && time < s_unixMaxSeconds)
            {
                return DateTimeOffset.FromUnixTimeSeconds(time);
            }

            return DateTimeOffset.FromUnixTimeMilliseconds(time);
        }

        throw new JsonException("Expected integer value.");
    }

    /// <summary>
    /// Writes the converted value to JSON.
    /// </summary>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(FormatAsSeconds ? value.ToUnixTimeSeconds() : value.ToUnixTimeMilliseconds());
    }
}