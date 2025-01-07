using NBitcoin;

namespace Kompaktor.JsonConverters;

public class OutPointJsonConverter : GenericStringJsonConverter<OutPoint>
{
    public override OutPoint Create(string str)
    {
        return OutPoint.Parse(str);
    }
    public override string ToString(OutPoint value)
    {
        return value.ToString();
    }
}