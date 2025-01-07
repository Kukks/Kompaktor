using NBitcoin;

namespace Kompaktor.JsonConverters;

public class MoneyJsonConverter : GenericStringJsonConverter<Money>
{
    public override Money Create(string str)
    {
        return Money.Satoshis(long.Parse(str));
    }
    public override string ToString(Money value)
    {
        return value.Satoshi.ToString();
    }
}