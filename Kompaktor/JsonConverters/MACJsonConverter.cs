using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using NBitcoin.Secp256k1;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Groups;

namespace Kompaktor.JsonConverters;

public class MACJsonConverter : JsonConverter<MAC>
{
    public override MAC? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected StartObject");
        }

        Scalar? t = null;
        GroupElement? v = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return (MAC)Activator.CreateInstance(typeof(MAC),
                    BindingFlags.NonPublic | BindingFlags.Instance, null,
                    [t, v], null)!;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected PropertyName");
            }

            var propertyName = reader.GetString();
            reader.Read();
            switch (propertyName)
            {
                case nameof(MAC.T):
                    t = reader.Deserialize<Scalar>(options);
                    break;
                case nameof(MAC.V):
                    v = reader.Deserialize<GroupElement>(options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
            
        }
        throw new JsonException("Could not read MAC.");
    }

    public override void Write(Utf8JsonWriter writer, MAC value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteProperty(nameof(value.T), value.T, options);
        writer.WriteProperty(nameof(value.V), value.V, options);
        writer.WriteEndObject();
    }
}