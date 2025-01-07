using System.Text.Json;
using System.Text.Json.Serialization;
using NBitcoin;

namespace Kompaktor.JsonConverters;

public class CoinJsonConverter:JsonConverter<Coin>
{
    public override Coin? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if(reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        if(reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected StartObject");
        }

        TxOut? txout = null;
        OutPoint? outpoint = null;
        Script? redeem = null;

        while (reader.Read())
        {
            if(reader.TokenType == JsonTokenType.EndObject)
            {
                if(txout is null || outpoint is null)
                {
                    throw new JsonException("Missing required fields");
                }
                if(redeem is null)
                {
                    return new Coin(outpoint, txout);
                }
                return new ScriptCoin(outpoint, txout, redeem);
            }
            if(reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected PropertyName");
            }
            var propertyName = reader.GetString();
            reader.Read();
            switch(propertyName)
            {
                case nameof(Coin.TxOut):
                    txout = JsonSerializer.Deserialize<TxOut>(ref reader, options);
                    break;
                case nameof(Coin.Outpoint):
                    outpoint = JsonSerializer.Deserialize<OutPoint>(ref reader, options);
                    break;
                case nameof(ScriptCoin.Redeem):
                    redeem = JsonSerializer.Deserialize<Script>(ref reader, options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        
        throw new JsonException("Unexpected end of JSON");

    }

    public override void Write(Utf8JsonWriter writer, Coin value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteProperty(nameof(Coin.TxOut), value.TxOut, options);
        writer.WriteProperty(nameof(Coin.Outpoint), value.Outpoint, options);
        if(value is ScriptCoin scriptCoin)
        {
            writer.WriteProperty(nameof(ScriptCoin.Redeem), scriptCoin.Redeem, options);
        }
        writer.WriteEndObject();
    }
}