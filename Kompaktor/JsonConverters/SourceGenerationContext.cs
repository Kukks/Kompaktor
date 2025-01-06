using System.Text.Json.Serialization;
using NBitcoin.Secp256k1;
using WabiSabi.Crypto.Groups;

namespace Kompaktor.JsonConverters;

[JsonSerializable(typeof(Scalar))]
[JsonSerializable(typeof(GroupElement))]
internal partial class SourceGenerationContext : JsonSerializerContext
{
    internal static JsonConverter[] DefaultConverters =
    [
        new ScalarJsonConverter(),
        new GroupElementJsonConverter(),
        new GroupElementVectorJsonConverter(),
        new CredentialJsonConverter(),
        new XPubKeyJsonConverter(),
        new PrivKeyJsonConverter()
    ];
}