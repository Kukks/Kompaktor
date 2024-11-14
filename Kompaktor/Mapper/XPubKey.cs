using System.Text.Json.Serialization;
using Kompaktor.JsonConverters;
using Kompaktor.Utils;
using NBitcoin.Secp256k1;

namespace Kompaktor.Mapper;

[JsonConverter(typeof(XPubKeyJsonConverter))]
public class XPubKey : IEquatable<XPubKey>
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Always)] public readonly ECXOnlyPubKey Key;

    public XPubKey(string hex)
    {
        Key = hex.ToXPubKey();
    } public XPubKey(byte[] bytes)
    {
        Key = bytes.ToXPubKey();
    }

    public XPubKey(ECXOnlyPubKey ecPubKey)
    {
        Key = ecPubKey;
    }

    public override string ToString()
    {
        return Convert.ToHexString(Key.ToBytes()).ToLower();
    }
    
    public static implicit operator XPubKey(ECXOnlyPubKey ecPubKey)
    {
        return new XPubKey(ecPubKey);
    }

    public static implicit operator ECXOnlyPubKey(XPubKey pubKey)
    {
        return pubKey.Key;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as XPubKey);
    }

    public bool Equals(XPubKey? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Key.CompareTo(other.Key) == 0;
    }

    public override int GetHashCode()
    {
        return Key.GetHashCode();
    }

    public static bool operator ==(XPubKey? left, XPubKey? right)
    {
        return Equals(left, right);
    }

    public static bool operator !=(XPubKey? left, XPubKey? right)
    {
        return !Equals(left, right);
    }
    
    public byte[] ToBytes()
    {
        return Key.ToBytes();
    }
}