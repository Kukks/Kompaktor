using System.Text.Json;
using System.Text.Json.Serialization;
using NBitcoin;

namespace Kompaktor.JsonConverters;

public class TxOutJsonConverter : JsonConverter<TxOut>
{
    public override TxOut? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if(reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        if(reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected StartObject");
        }

        Money? value = null;
        Script? scriptPubKey = null;

        while (reader.Read())
        {
            if(reader.TokenType == JsonTokenType.EndObject)
            {
                if(value is null || scriptPubKey is null)
                {
                    throw new JsonException("Missing required fields");
                }
                return new TxOut(value, scriptPubKey);
            }
            if(reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected PropertyName");
            }
            var propertyName = reader.GetString();
            reader.Read();
            switch(propertyName)
            {
                case nameof(TxOut.Value):
                    value = JsonSerializer.Deserialize<Money>(ref reader, options);
                    break;
                case nameof(TxOut.ScriptPubKey):
                    scriptPubKey = JsonSerializer.Deserialize<Script>(ref reader, options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        throw new JsonException("Unexpected end of JSON");
    }

    public override void Write(Utf8JsonWriter writer, TxOut value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteProperty(nameof(TxOut.Value), value.Value, options);
        writer.WriteProperty(nameof(TxOut.ScriptPubKey), value.ScriptPubKey, options);
        writer.WriteEndObject();
    }
}