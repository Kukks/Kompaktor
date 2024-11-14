using System.Text.Json.Serialization;
using Kompaktor.JsonConverters;
using Kompaktor.Utils;
using NBitcoin.Secp256k1;

namespace Kompaktor.Mapper;

[JsonConverter(typeof(PrivKeyJsonConverter))]
public class PrivKey: IEquatable<PrivKey>
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Always)] public readonly ECPrivKey Key;

    public PrivKey(string hex)
    {
        Key = hex.ToPrivKey();
    }

    private PrivKey(ECPrivKey ecPrivKey)
    {
        Key = ecPrivKey;
    }

    public override string ToString()
    {
        return Convert.ToHexString(Key.ToBytes()).ToLower();
    }
    
    public static implicit operator PrivKey(ECPrivKey ecPubKey)
    {
        return new PrivKey(ecPubKey);
    }

    public static implicit operator ECPrivKey(PrivKey privKey)
    {
        return privKey.Key;
    } 
    public XPubKey ToXPubKey()
    {
        return  new XPubKey(Key.CreateXOnlyPubKey());
    }

    public bool Equals(PrivKey? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Key.Equals(other.Key);
    }

    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((PrivKey) obj);
    }

    public override int GetHashCode()
    {
        return Key.GetHashCode();
    }

    public static bool operator ==(PrivKey? left, PrivKey? right)
    {
        return Equals(left, right);
    }

    public static bool operator !=(PrivKey? left, PrivKey? right)
    {
        return !Equals(left, right);
    }
}