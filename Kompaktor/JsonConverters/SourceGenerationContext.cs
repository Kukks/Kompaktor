using System.Text.Json.Serialization;
using NBitcoin;
using NBitcoin.Secp256k1;
using WabiSabi;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Groups;
using WabiSabi.Crypto.ZeroKnowledge;

namespace Kompaktor.JsonConverters;

[JsonSerializable(typeof(Scalar))]
[JsonSerializable(typeof(GroupElement))]
[JsonSerializable(typeof(ScalarVector))]
[JsonSerializable(typeof(GroupElementVector))]
[JsonSerializable(typeof(MAC))]
[JsonSerializable(typeof(CredentialPresentation))]
[JsonSerializable(typeof(IssuanceRequest))]
[JsonSerializable(typeof(Proof))]
[JsonSerializable(typeof(CredentialIssuerParameters))]
[JsonSerializable(typeof(Coin))]
[JsonSerializable(typeof(TxOut))]
[JsonSerializable(typeof(Money))]
[JsonSerializable(typeof(OutPoint))]
[JsonSerializable(typeof(Script))]
[JsonSerializable(typeof(WitScript))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(Scalar[]))]
[JsonSerializable(typeof(GroupElement[]))]
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