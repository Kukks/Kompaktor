using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using WabiSabi.Crypto.Groups;

namespace Kompaktor.JsonConverters;

public class GroupElementVectorJsonConverter : JsonConverter<GroupElementVector>
{
    public override GroupElementVector? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.StartArray:
            {
                var elements = reader.Deserialize<GroupElement[]>(options)
                               ?? throw new JsonException("Array was expected. Null was given.");
                return (GroupElementVector) Activator.CreateInstance(typeof(GroupElementVector),
                    BindingFlags.NonPublic | BindingFlags.Instance, null,
                    [elements], null)!;
            }
            default:
                throw new JsonException($"Invalid serialized {nameof(GroupElementVector)}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, GroupElementVector value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var element in value)
        {
            writer.Serialize(element, options);
        }

        writer.WriteEndArray();
    }
}