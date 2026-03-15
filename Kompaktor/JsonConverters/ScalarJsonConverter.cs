using NBitcoin.Secp256k1;

namespace Kompaktor.JsonConverters;

public class ScalarJsonConverter : GenericStringJsonConverter<Scalar>
{
    public override Scalar Create(string str)
    {
        try
        {
            return new Scalar(Convert.FromHexString(str));
        }
        catch (FormatException ex)
        {
            throw new System.Text.Json.JsonException($"Invalid Scalar hex value: '{str}'", ex);
        }
    }
}