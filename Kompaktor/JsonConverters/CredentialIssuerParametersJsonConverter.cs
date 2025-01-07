using System.Text.Json;
using System.Text.Json.Serialization;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Groups;

namespace Kompaktor.JsonConverters;

public class CredentialIssuerParametersJsonConverter : JsonConverter<CredentialIssuerParameters>
{
    public override CredentialIssuerParameters? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected StartObject");
        }

        GroupElement? cw = null;
        GroupElement? i = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new CredentialIssuerParameters(cw!, i!);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected PropertyName");
            }

            var propertyName = reader.GetString();
            reader.Read();
            switch (propertyName)
            {
                case nameof(CredentialIssuerParameters.Cw):
                    cw = reader.Deserialize<GroupElement>(options);
                    break;
                case nameof(CredentialIssuerParameters.I):
                    i = reader.Deserialize<GroupElement>(options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("Could not read CredentialIssuerParameters.");
    }

    public override void Write(Utf8JsonWriter writer, CredentialIssuerParameters? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteProperty(nameof(value.Cw), value.Cw, options);
            writer.WriteProperty(nameof(value.I), value.I, options);
            writer.WriteEndObject();
        }
    }
}