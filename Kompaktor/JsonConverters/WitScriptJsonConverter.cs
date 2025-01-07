using NBitcoin;

namespace Kompaktor.JsonConverters;

public class WitScriptJsonConverter : GenericStringJsonConverter<WitScript>
{
    public override WitScript Create(string str)
    {
        return new WitScript(str);
    }
    public override string ToString(WitScript value)
    {
        return value.ToString();
    }
}