using NBitcoin;

namespace Kompaktor.JsonConverters;

public class MoneyJsonConverter : GenericStringJsonConverter<Money>
{
    public override Money Create(string str)
    {
        if (!long.TryParse(str, out var satoshis))
            throw new System.Text.Json.JsonException($"Invalid Money value: '{str}'");
        return Money.Satoshis(satoshis);
    }
    public override string ToString(Money value)
    {
        return value.Satoshi.ToString();
    }
}