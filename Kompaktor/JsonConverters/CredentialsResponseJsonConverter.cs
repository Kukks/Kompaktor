using System.Text.Json;
using System.Text.Json.Serialization;
using WabiSabi.CredentialRequesting;
using WabiSabi.Crypto;
using WabiSabi.Crypto.ZeroKnowledge;

namespace Kompaktor.JsonConverters;

public class CredentialsResponseJsonConverter : JsonConverter<CredentialsResponse>
{
    public override CredentialsResponse? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected StartObject");
        }
        IEnumerable<MAC>? issuedCredentials = null;
        IEnumerable<Proof>? proofs = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new CredentialsResponse(issuedCredentials!, proofs!);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected PropertyName");
            }

            var propertyName = reader.GetString();
            reader.Read();
            switch (propertyName)
            {
                case nameof(CredentialsResponse.IssuedCredentials):
                    issuedCredentials = reader.Deserialize<IEnumerable<MAC>>(options);
                    break;
                case nameof(CredentialsResponse.Proofs):
                    proofs = reader.Deserialize<IEnumerable<Proof>>(options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        
        throw new JsonException("Could not read CredentialsResponse.");
    }

    public override void Write(Utf8JsonWriter writer, CredentialsResponse value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteProperty(nameof(CredentialsResponse.IssuedCredentials), value.IssuedCredentials, options);
        writer.WriteProperty(nameof(CredentialsResponse.Proofs), value.Proofs, options);
        writer.WriteEndObject();
    }
}