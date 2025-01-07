using System.Text.Json;
using System.Text.Json.Serialization;
using WabiSabi;
using WabiSabi.CredentialRequesting;
using WabiSabi.Crypto.ZeroKnowledge;

namespace Kompaktor.JsonConverters;

public class CredentialsRequestJsonConverter : JsonConverter<ICredentialsRequest>
{
    public override ICredentialsRequest? Read(ref Utf8JsonReader reader, Type typeToConvert,
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

        long delta = 0;
        CredentialPresentation[]? presentations = null;
        IssuanceRequest[]? requests = null;
        Proof[]? proofs = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (delta == 0 && presentations is null)
                {
                    return new ZeroCredentialsRequest(requests!, proofs!);
                }

                return new RealCredentialsRequest(delta, presentations!, requests!, proofs!);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected PropertyName");
            }

            var propertyName = reader.GetString();
            reader.Read();
            switch (propertyName)
            {
                case nameof(ZeroCredentialsRequest.Delta):
                    delta = reader.GetInt64();
                    break;
                case nameof(ZeroCredentialsRequest.Presented):
                    presentations = reader.Deserialize<CredentialPresentation[]>(options);
                    break;
                case nameof(ZeroCredentialsRequest.Requested):
                    requests = reader.Deserialize<IssuanceRequest[]>(options);
                    break;
                case nameof(ZeroCredentialsRequest.Proofs):
                    proofs = reader.Deserialize<Proof[]>(options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("Could not read ICredentialsRequest.");
    }

    public override void Write(Utf8JsonWriter writer, ICredentialsRequest? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();

            writer.WriteProperty(nameof(ICredentialsRequest.Requested), value.Requested, options);
            writer.WriteProperty(nameof(ICredentialsRequest.Proofs), value.Proofs, options);
            if (value is RealCredentialsRequest real)
            {
                writer.WriteNumber(nameof(RealCredentialsRequest.Delta), real.Delta);
                writer.WriteProperty(nameof(RealCredentialsRequest.Presented), value.Presented, options);
            }

            writer.WriteEndObject();
        }
    }
}