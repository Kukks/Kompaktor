using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Groups;
using WabiSabi.Crypto.ZeroKnowledge;

namespace Kompaktor.JsonConverters;

public class ProofJsonConverter : JsonConverter<Proof>
{
    public override Proof? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected StartObject");
        }

        GroupElementVector? publicNonces = null;
        ScalarVector? responses = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return (Proof)Activator.CreateInstance(typeof(Proof),
                    BindingFlags.NonPublic | BindingFlags.Instance, null,
                    [publicNonces, responses], null)!;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected PropertyName");
            }

            var propertyName = reader.GetString();
            reader.Read();
            switch (propertyName)
            {
                case nameof(Proof.PublicNonces):
                    publicNonces = reader.Deserialize<GroupElementVector>(options);
                    break;
                case nameof(Proof.Responses):
                    responses = reader.Deserialize<ScalarVector>(options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("Could not read Proof.");
    }

    public override void Write(Utf8JsonWriter writer, Proof value, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }
}