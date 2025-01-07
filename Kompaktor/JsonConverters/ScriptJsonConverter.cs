using NBitcoin;

namespace Kompaktor.JsonConverters;

public class ScriptJsonConverter : GenericStringJsonConverter<Script>
{
    public override Script Create(string str)
    {
        return new Script(str);
    }
    public override string ToString(Script value)
    {
        return value.ToString();
    }
}