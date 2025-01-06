using System.Collections;
using System.Text.Json.Serialization;
using Kompaktor.JsonConverters;
using Kompaktor.Utils;
using WabiSabi.Crypto.ZeroKnowledge;

namespace Kompaktor.Mapper;

[JsonConverter(typeof(CredentialJsonConverter))]
public record BlindedCredential : Credential, IEqualityComparer<BlindedCredential>, IEqualityComparer
{
    public BlindedCredential(Credential credential) : base(credential.Value, credential.Randomness, credential.Mac)
    {
    }

    public override int GetHashCode()
    {
        return Mac.ToBytes().GetHashCode();
    }

    public bool Equals(BlindedCredential? x, BlindedCredential? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return x.Mac.ToBytes().SequenceEqual(y.Mac.ToBytes());
    }

    public int GetHashCode(BlindedCredential obj)
    {
        return obj.Mac.GetHashCode();
    }

    public new bool Equals(object? x, object? y)
    {
        return Equals(x as BlindedCredential, y as BlindedCredential);
    }

    public int GetHashCode(object obj)
    {
        return GetHashCode(obj as BlindedCredential);
    }

    public override string ToString()
    {
        return this.ToBytes().ToHex();
    }
}