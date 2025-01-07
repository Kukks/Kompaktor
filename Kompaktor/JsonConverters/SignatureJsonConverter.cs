using Kompaktor.Utils;
using NBitcoin.BIP322;

namespace Kompaktor.JsonConverters;

public class SignatureJsonConverter : GenericStringJsonConverter<BIP322Signature.Full>
{
    public override BIP322Signature.Full Create(string str)
    {
        return (BIP322Signature.Full) NetworkHelper.Try(network => BIP322Signature.Parse(str, network));
    }
    public override string ToString(BIP322Signature.Full value)
    {
        return value.ToBase64();
    }
    
}