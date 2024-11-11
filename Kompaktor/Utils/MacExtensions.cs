using WabiSabi.Crypto;

namespace Kompaktor.Utils;

public static class MacExtensions
{
    //32 + 33 bytes
    public static byte[] ToBytes(this MAC mac)
    {
        return mac.T.ToBytes().Concat(mac.V.ToBytes()).ToArray();
    }

    public static string Serial(this MAC mac)
    {
        return Convert.ToHexString(mac.ToBytes()).ToLowerInvariant();
    }
}