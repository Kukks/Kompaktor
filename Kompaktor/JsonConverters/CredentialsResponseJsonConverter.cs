using System.Text.Json;
using System.Text.Json.Serialization;
using WabiSabi.CredentialRequesting;

namespace Kompaktor.JsonConverters;

public class CredentialsResponseJsonConverter : JsonConverter<CredentialsResponse>
{
    public override CredentialsResponse? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    public override void Write(Utf8JsonWriter writer, CredentialsResponse value, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }
}