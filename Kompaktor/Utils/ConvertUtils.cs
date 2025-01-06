using Kompaktor.Mapper;
using NBitcoin.Secp256k1;

namespace Kompaktor.Utils;

public static class ConvertUtils
{

    public static XPubKey ToXPubKey(this string hex)
    {
        return ECXOnlyPubKey.Create(global::System.Convert.FromHexString(hex));
    }

    public static ECPrivKey ToPrivKey(this string hex)
    {
        return ECPrivKey.Create(global::System.Convert.FromHexString(hex));
    }

    public static string ToHex(this byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static byte[] ToBytes(this ECPrivKey key)
    {
        Span<byte> output = stackalloc byte[32];
        key.WriteToSpan(output);
        return output.ToArray();
    }


    public static byte[] ToBytes(this long val)
    {
        var bytes = BitConverter.GetBytes(val);
        return BitConverter.IsLittleEndian ? bytes : bytes.Reverse().ToArray();
    }

    public static long ToLong(this byte[] bytes)
    {
        return BitConverter.ToInt64(BitConverter.IsLittleEndian ? bytes : bytes.Reverse().ToArray());
    }
    public static XPubKey ToXPubKey(this byte[] bytes)
    {
        return ECXOnlyPubKey.Create(bytes);
    }
    public static PrivKey ToPrivKey(this byte[] bytes)
    {
        return ECPrivKey.Create(bytes);
    }
}