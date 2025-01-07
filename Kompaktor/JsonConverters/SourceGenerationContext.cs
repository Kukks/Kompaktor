using System.Text.Json.Serialization;
using NBitcoin.Secp256k1;
using WabiSabi.Crypto.Groups;

namespace Kompaktor.JsonConverters;

[JsonSerializable(typeof(Scalar))]
[JsonSerializable(typeof(GroupElement))]
internal partial class SourceGenerationContext : JsonSerializerContext
{
    internal static readonly JsonConverter[] DefaultConverters =
    [
        new CoinJsonConverter(),

        new CredentialIssuerParametersJsonConverter(),

        new CredentialJsonConverter(),

        new CredentialPresentationJsonConverter(),

        new CredentialsRequestJsonConverter(),

        new CredentialsResponseJsonConverter(),


        new GroupElementJsonConverter(),
        new GroupElementVectorJsonConverter(),

        new IssuanceRequestJsonConverter(),
        new MACJsonConverter(),
        new MoneyJsonConverter(),
        new OutPointJsonConverter(),
        new PrivKeyJsonConverter(),

        new ProofJsonConverter(),

        new ScalarJsonConverter(),
        new ScalarVectorJsonConverter(),

        new ScriptJsonConverter(),
        new SignatureJsonConverter(),

        new TxOutJsonConverter(),
        new UnixToNullableDateTimOffsetConverter(),

        new WitScriptJsonConverter(),


        new XPubKeyJsonConverter(),
    ];
}