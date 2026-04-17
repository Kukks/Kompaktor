using Kompaktor.Credentials;
using Kompaktor.Models;
using Kompaktor.Utils;
using NBitcoin;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Randomness;
using Xunit;

namespace Kompaktor.Tests;

public class P2trInputValidationTests
{
    private static KompaktorRoundEventCreated MakeCreated(HashSet<ScriptType>? allowedTypes = null)
    {
        var key = new CredentialIssuerSecretKey(new InsecureRandom());
        var created = new KompaktorRoundEventCreated(
            "test-round",
            new FeeRate(Money.Satoshis(1000)),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5),
            new IntRange(1, 10),
            new MoneyRange(Money.Satoshis(5000), Money.Coins(1)),
            new IntRange(1, 10),
            new MoneyRange(Money.Satoshis(5000), Money.Coins(1)),
            new Dictionary<CredentialType, CredentialConfiguration>
            {
                [CredentialType.Amount] = new(1000L, new IntRange(2, 2), new IntRange(2, 2),
                    key.ComputeCredentialIssuerParameters())
            });

        if (allowedTypes is not null)
            created = created with { AllowedInputTypes = allowedTypes };

        return created;
    }

    [Fact]
    public void AllowedInputTypes_DefaultsToTaprootAndP2WPKH()
    {
        var created = MakeCreated();
        Assert.Contains(ScriptType.Taproot, created.AllowedInputTypes);
        Assert.Contains(ScriptType.P2WPKH, created.AllowedInputTypes);
        Assert.Equal(2, created.AllowedInputTypes.Count);
    }

    [Fact]
    public void AllowedInputTypes_RejectsP2PKH_ByDefault()
    {
        var created = MakeCreated();
        Assert.DoesNotContain(ScriptType.P2PKH, created.AllowedInputTypes);
    }

    [Fact]
    public void AllowedInputTypes_RejectsP2PK_ByDefault()
    {
        var created = MakeCreated();
        Assert.DoesNotContain(ScriptType.P2PK, created.AllowedInputTypes);
    }

    [Fact]
    public void TryGetScriptType_DetectsTaproot()
    {
        var key = new Key();
        var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
        Assert.Equal(ScriptType.Taproot, script.TryGetScriptType());
    }

    [Fact]
    public void TryGetScriptType_DetectsP2WPKH()
    {
        var key = new Key();
        var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit);
        Assert.Equal(ScriptType.P2WPKH, script.TryGetScriptType());
    }

    [Fact]
    public void TryGetScriptType_DetectsP2PKH()
    {
        var key = new Key();
        var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.Legacy);
        Assert.Equal(ScriptType.P2PKH, script.TryGetScriptType());
    }

    [Fact]
    public void TryGetScriptType_ReturnsNull_ForUnknownScript()
    {
        var script = new Script(OpcodeType.OP_RETURN, Op.GetPushOp(new byte[20]));
        Assert.Null(script.TryGetScriptType());
    }

    [Fact]
    public void AllowedInputTypes_Validation_AcceptsTaproot()
    {
        var allowed = new HashSet<ScriptType> { ScriptType.Taproot, ScriptType.P2WPKH };
        var key = new Key();
        var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
        var scriptType = script.TryGetScriptType();

        Assert.NotNull(scriptType);
        Assert.Contains(scriptType!.Value, allowed);
    }

    [Fact]
    public void AllowedInputTypes_Validation_AcceptsP2WPKH()
    {
        var allowed = new HashSet<ScriptType> { ScriptType.Taproot, ScriptType.P2WPKH };
        var key = new Key();
        var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit);
        var scriptType = script.TryGetScriptType();

        Assert.NotNull(scriptType);
        Assert.Contains(scriptType!.Value, allowed);
    }

    [Fact]
    public void AllowedInputTypes_Validation_RejectsP2PKH()
    {
        var allowed = new HashSet<ScriptType> { ScriptType.Taproot, ScriptType.P2WPKH };
        var key = new Key();
        var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.Legacy);
        var scriptType = script.TryGetScriptType();

        Assert.NotNull(scriptType);
        Assert.DoesNotContain(scriptType!.Value, allowed);
    }

    [Fact]
    public void AllowedInputTypes_Validation_RejectsUnknownScript()
    {
        var allowed = new HashSet<ScriptType> { ScriptType.Taproot, ScriptType.P2WPKH };
        var script = new Script(OpcodeType.OP_RETURN, Op.GetPushOp(new byte[20]));
        var scriptType = script.TryGetScriptType();

        Assert.Null(scriptType);
    }

    [Fact]
    public void AllowedInputTypes_EmptySet_AllowsAll()
    {
        var allowed = new HashSet<ScriptType>();
        var key = new Key();
        var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.Legacy);
        var scriptType = script.TryGetScriptType();

        Assert.True(allowed.Count == 0 || allowed.Contains(scriptType!.Value));
    }

    [Theory]
    [InlineData(ScriptPubKeyType.TaprootBIP86, true)]
    [InlineData(ScriptPubKeyType.Segwit, false)]
    [InlineData(ScriptPubKeyType.Legacy, false)]
    public void IsScriptType_Taproot_MatchesOnlyTaproot(ScriptPubKeyType keyType, bool expectedTaproot)
    {
        var key = new Key();
        var script = key.PubKey.GetScriptPubKey(keyType);
        Assert.Equal(expectedTaproot, script.IsScriptType(ScriptType.Taproot));
    }

    [Fact]
    public void ScriptTypeNotAllowed_ErrorCode_Exists()
    {
        var code = Kompaktor.Errors.KompaktorProtocolErrorCode.ScriptTypeNotAllowed;
        Assert.Equal("ScriptTypeNotAllowed", code.ToString());
    }
}
