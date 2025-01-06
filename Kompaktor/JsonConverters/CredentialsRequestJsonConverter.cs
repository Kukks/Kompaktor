using System.Text.Json;
using System.Text.Json.Serialization;
using WabiSabi.CredentialRequesting;

namespace Kompaktor.JsonConverters;

public class CredentialsRequestJsonConverter : JsonConverter<ICredentialsRequest>
{
    public override ICredentialsRequest? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
    }

    public override void Write(Utf8JsonWriter writer, ICredentialsRequest value, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }
}