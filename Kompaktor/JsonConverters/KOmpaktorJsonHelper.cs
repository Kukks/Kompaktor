using System.Text.Json;

namespace Kompaktor.JsonConverters;

public static class KompaktorJsonHelper
{
    public static void Serialize<T>(this Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        var o = new JsonSerializerOptions(options)
        {
            TypeInfoResolver = SourceGenerationContext.Default
        };
        foreach (var converter in SourceGenerationContext.DefaultConverters)
        {
            o.Converters.Add(converter);
        }
#pragma warning disable IL2026,IL3050
        JsonSerializer.Serialize(writer, value, o);
#pragma warning restore IL2026,IL3050
    }

    public static T? Deserialize<T>(this Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var o = new JsonSerializerOptions(options)
        {
            TypeInfoResolver = SourceGenerationContext.Default
        };
        foreach (var converter in SourceGenerationContext.DefaultConverters)
        {
            o.Converters.Add(converter);
        }
#pragma warning disable IL2026,IL3050
        return JsonSerializer.Deserialize<T>(ref reader, o);
#pragma warning restore IL2026,IL3050
    }
}