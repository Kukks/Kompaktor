using System.Text.Json;
using System.Text.Json.Serialization;
using NBitcoin.Secp256k1;
using WabiSabi.Crypto;

namespace Kompaktor.JsonConverters;

public class ScalarVectorJsonConverter : JsonConverter<ScalarVector>
{
    public override ScalarVector? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.StartArray:
            {
                var elements = reader.Deserialize<Scalar[]>(options)
                               ?? throw new JsonException("Array was expected. Null was given.");
                return ScalarVector.FromScalars(elements);
            }
            default:
                throw new JsonException($"Invalid serialized {nameof(ScalarVector)}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, ScalarVector value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var element in value)
        {
            writer.Serialize(element, options);
        }

        writer.WriteEndArray();
    }
}
