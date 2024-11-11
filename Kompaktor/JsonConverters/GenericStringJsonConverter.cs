using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kompaktor.JsonConverters;

public abstract class GenericStringJsonConverter<T> : JsonConverter<T> where T : class
{
    public abstract T Create(string str);
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String ||
            reader.GetString() is not { } str ||
            string.IsNullOrEmpty(str))
        {
            throw new JsonException("Expected string");
        }

        return Create(str);
    }

    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.ToString());
    }
}