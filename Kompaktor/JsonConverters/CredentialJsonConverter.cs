using System.Text.Json;
using Kompaktor.Mapper;
using Kompaktor.Utils;
using Newtonsoft.Json;
using WabiSabi.Crypto.Groups;
using WabiSabi.Crypto.ZeroKnowledge;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace Kompaktor.JsonConverters;

public class CredentialJsonConverter : GenericStringJsonConverter<BlindedCredential>
{
    public override BlindedCredential Create(string str)
    {
        return CredentialHelper.CredFromBytes(Convert.FromHexString(str));
    }
}

public class CredentialPresentationJsonConverter : System.Text.Json.Serialization.JsonConverter<CredentialPresentation>
{
    public override CredentialPresentation? ReadJson(JsonReader reader, Type objectType, CredentialPresentation? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        reader.Expect(JsonToken.StartObject);
        var ca = reader.ReadProperty<GroupElement>(serializer, "Ca");
        var cx0 = reader.ReadProperty<GroupElement>(serializer, "Cx0");
        var cx1 = reader.ReadProperty<GroupElement>(serializer, "Cx1");
        var cV = reader.ReadProperty<GroupElement>(serializer, "CV");
        var s = reader.ReadProperty<GroupElement>(serializer, "S");
        reader.Read();
        reader.Expect(JsonToken.EndObject);

        return ReflectionUtils.CreateInstance<CredentialPresentation>(new object[] { ca, cx0, cx1, cV, s });
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, CredentialPresentation? value, JsonSerializer serializer)
    {
        
    }

    public override CredentialPresentation? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    public override void Write(Utf8JsonWriter writer, CredentialPresentation value, JsonSerializerOptions options)
    {if (value is null)
        {
            throw new ArgumentException($"No valid {nameof(CredentialPresentation)}.", nameof(value));
        }
        writer.WriteStartObject();
        writer.WrVa
        writer.Serialize("Ca", value.Ca, options);
        writer.WriteProperty("Cx0", value.Cx0, options);
        writer.WriteProperty("Cx1", value.Cx1, options);
        writer.WriteProperty("CV", value.CV, options);
        writer.WriteProperty("S", value.S, options);
        writer.WriteEndObject();
    }
}
