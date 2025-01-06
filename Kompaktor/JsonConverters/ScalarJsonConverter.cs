using NBitcoin.Secp256k1;

namespace Kompaktor.JsonConverters;

public class ScalarJsonConverter : GenericStringJsonConverter<Scalar>
{
    public override Scalar Create(string str)
    {
        return new Scalar(Convert.FromHexString(str));
    }
}