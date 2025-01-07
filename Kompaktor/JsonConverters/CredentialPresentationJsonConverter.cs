using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using WabiSabi.Crypto.Groups;
using WabiSabi.Crypto.ZeroKnowledge;

namespace Kompaktor.JsonConverters;

public class CredentialPresentationJsonConverter : JsonConverter<CredentialPresentation>
{
    public override CredentialPresentation? Read(ref Utf8JsonReader reader, Type typeToConvert,
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

        GroupElement? ca = null;
        GroupElement? cx0 = null;
        GroupElement? cx1 = null;
        GroupElement? cV = null;
        GroupElement? s = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return (CredentialPresentation)Activator.CreateInstance(typeof(CredentialPresentation),
                    BindingFlags.NonPublic | BindingFlags.Instance, null,
                    [ca, cx0, cx1, cV, s], null)!;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected PropertyName");
            }

            var propertyName = reader.GetString();
            reader.Read();
            switch (propertyName)
            {
                case nameof(CredentialPresentation.Ca):
                    ca = reader.Deserialize<GroupElement>(options);
                    break;
                case nameof(CredentialPresentation.Cx0):
                    cx0 = reader.Deserialize<GroupElement>(options);
                    break;
                case nameof(CredentialPresentation.Cx1):
                    cx1 = reader.Deserialize<GroupElement>(options);
                    break;
                case nameof(CredentialPresentation.CV):
                    cV = reader.Deserialize<GroupElement>(options);
                    break;
                case nameof(CredentialPresentation.S):
                    s = reader.Deserialize<GroupElement>(options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("Could not read CredentialPresentation.");
    }

    public override void Write(Utf8JsonWriter writer, CredentialPresentation? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();

            writer.WriteProperty(nameof(CredentialPresentation.Ca), value.Ca, options);
            writer.WriteProperty(nameof(CredentialPresentation.Cx0), value.Cx0, options);
            writer.WriteProperty(nameof(CredentialPresentation.Cx1), value.Cx1, options);
            writer.WriteProperty(nameof(CredentialPresentation.CV), value.CV, options);
            writer.WriteProperty(nameof(CredentialPresentation.S), value.S, options);

            writer.WriteEndObject();
        }
    }
}