using NBitcoin;

namespace Kompaktor.JsonConverters;

public class OutPointJsonConverter : GenericStringJsonConverter<OutPoint>
{
    public override OutPoint Create(string str)
    {
        try
        {
            return OutPoint.Parse(str);
        }
        catch (FormatException ex)
        {
            throw new System.Text.Json.JsonException($"Invalid OutPoint value: '{str}'", ex);
        }
    }
    public override string ToString(OutPoint value)
    {
        return value.ToString();
    }
}