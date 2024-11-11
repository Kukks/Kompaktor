using System.Reflection;
using Kompaktor.Mapper;
using NBitcoin.Secp256k1;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Groups;
using WabiSabi.Crypto.ZeroKnowledge;

namespace Kompaktor.Utils;

public static class CredentialHelper
{

    public static byte[] ToBytes(this Credential credential)
    {
        //
        //33 bytes + 32 bytes + 32 bytes + 8 bytes = 105 bytes
        return credential.Mac.ToBytes().Concat(credential.Randomness.ToBytes()).Concat(credential.Value.ToBytes()).ToArray();
    }

    public static MAC MacFromBytes(byte[] bytes)
    {
        if (bytes.Length != 65)
        {
            throw new ArgumentException("Invalid byte array length");
        }
        var t = new Scalar(bytes[..32]);
        var k =  GroupElement.FromBytes(bytes[32..]);
        return (MAC)Activator.CreateInstance(typeof(MAC), BindingFlags.NonPublic | BindingFlags.Instance, null,
            [t, k], null)!;
    }
    public static BlindedCredential CredFromBytes(byte[] bytes)
    {
        if (bytes.Length != 105)
        {
            throw new ArgumentException("Invalid byte array length");
        }
        var mac = MacFromBytes(bytes[..65]);    
        var randomness = new Scalar(bytes[65..97]);
        var value = bytes[97..].ToLong();
        
        return new BlindedCredential(new Credential(value, randomness, mac));
    }
}