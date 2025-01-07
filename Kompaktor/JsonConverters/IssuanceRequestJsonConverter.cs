using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using WabiSabi;
using WabiSabi.Crypto.Groups;

namespace Kompaktor.JsonConverters;

public class IssuanceRequestJsonConverter : JsonConverter<IssuanceRequest>
{
    public override IssuanceRequest? Read(ref Utf8JsonReader reader, Type typeToConvert,
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

        GroupElement? ma = null;
        GroupElement[]? bitCommitments = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return (IssuanceRequest)Activator.CreateInstance(typeof(IssuanceRequest),
                    BindingFlags.NonPublic | BindingFlags.Instance, null,
                    [ma, bitCommitments], null)!;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected PropertyName");
            }

            var propertyName = reader.GetString();
            reader.Read();
            switch (propertyName)
            {
                case nameof(IssuanceRequest.Ma):
                    ma = reader.Deserialize<GroupElement>(options);
                    break;
                case nameof(IssuanceRequest.BitCommitments):
                    bitCommitments = reader.Deserialize<GroupElement[]>(options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("Could not read IssuanceRequest.");
    }

    public override void Write(Utf8JsonWriter writer, IssuanceRequest? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();

            writer.WriteProperty(nameof(IssuanceRequest.Ma), value.Ma, options);
            writer.WriteProperty(nameof(IssuanceRequest.BitCommitments), value.BitCommitments, options);

            writer.WriteEndObject();
        }
    }
}