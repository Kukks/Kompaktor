using Kompaktor.Credentials;
using Kompaktor.Models;
using NBitcoin;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Randomness;
using Xunit;

namespace Kompaktor.Tests;

public class EquivocationDetectionTests
{
    private static readonly CredentialIssuerSecretKey Key = new(new InsecureRandom());

    private static KompaktorRoundEventCreated MakeCreated(
        string roundId = "round-1",
        long feeRateSatPerK = 1000,
        int inputTimeoutMin = 5,
        int outputTimeoutMin = 5,
        int signingTimeoutMin = 5,
        int inputCountMin = 1, int inputCountMax = 10,
        long inputAmountMinSat = 5000, long inputAmountMaxSat = 100_000_000,
        int outputCountMin = 1, int outputCountMax = 10,
        long outputAmountMinSat = 5000, long outputAmountMaxSat = 100_000_000,
        long credMax = 1000L,
        string? blameOf = null)
    {
        var created = new KompaktorRoundEventCreated(
            roundId,
            new FeeRate(Money.Satoshis(feeRateSatPerK)),
            TimeSpan.FromMinutes(inputTimeoutMin),
            TimeSpan.FromMinutes(outputTimeoutMin),
            TimeSpan.FromMinutes(signingTimeoutMin),
            new IntRange(inputCountMin, inputCountMax),
            new MoneyRange(Money.Satoshis(inputAmountMinSat), Money.Satoshis(inputAmountMaxSat)),
            new IntRange(outputCountMin, outputCountMax),
            new MoneyRange(Money.Satoshis(outputAmountMinSat), Money.Satoshis(outputAmountMaxSat)),
            new Dictionary<CredentialType, CredentialConfiguration>
            {
                [CredentialType.Amount] = new(credMax, new IntRange(2, 2), new IntRange(2, 2),
                    Key.ComputeCredentialIssuerParameters())
            });

        if (blameOf is not null)
            created = created with { BlameOf = blameOf };

        return created;
    }

    [Fact]
    public void FromCreatedEvent_ExtractsAllFields()
    {
        var created = MakeCreated();
        var info = RoundInfoResponse.FromCreatedEvent(created);

        Assert.Equal(created.RoundId, info.RoundId);
        Assert.Equal(created.FeeRate.FeePerK.Satoshi, info.FeeRateSatPerK);
        Assert.Equal(created.InputTimeout.Ticks, info.InputTimeoutTicks);
        Assert.Equal(created.OutputTimeout.Ticks, info.OutputTimeoutTicks);
        Assert.Equal(created.SigningTimeout.Ticks, info.SigningTimeoutTicks);
        Assert.Equal(created.InputCount.Min, info.InputCountMin);
        Assert.Equal(created.InputCount.Max, info.InputCountMax);
        Assert.Equal(created.InputAmount.Min.Satoshi, info.InputAmountMinSat);
        Assert.Equal(created.InputAmount.Max.Satoshi, info.InputAmountMaxSat);
        Assert.Equal(created.OutputCount.Min, info.OutputCountMin);
        Assert.Equal(created.OutputCount.Max, info.OutputCountMax);
        Assert.Equal(created.OutputAmount.Min.Satoshi, info.OutputAmountMinSat);
        Assert.Equal(created.OutputAmount.Max.Satoshi, info.OutputAmountMaxSat);
        Assert.Equal(created.AllowedInputTypes, info.AllowedInputTypes);
        Assert.Equal(created.IsBlameRound, info.IsBlameRound);
        Assert.Equal(created.BlameOf, info.BlameOf);
    }

    [Fact]
    public void FromCreatedEvent_ExtractsCredentials()
    {
        var created = MakeCreated(credMax: 2000);
        var info = RoundInfoResponse.FromCreatedEvent(created);

        Assert.Single(info.Credentials);
        Assert.True(info.Credentials.ContainsKey("Amount"));
        Assert.Equal(2000, info.Credentials["Amount"].Max);
    }

    [Fact]
    public void CompareWith_IdenticalRound_NoMismatches()
    {
        var created = MakeCreated();
        var info = RoundInfoResponse.FromCreatedEvent(created);
        var mismatches = info.CompareWith(created);
        Assert.Empty(mismatches);
    }

    [Fact]
    public void CompareWith_DifferentRoundId_DetectsMismatch()
    {
        var created = MakeCreated(roundId: "round-1");
        var info = RoundInfoResponse.FromCreatedEvent(created) with { RoundId = "round-FAKE" };
        var mismatches = info.CompareWith(created);
        Assert.Contains("RoundId", mismatches);
    }

    [Fact]
    public void CompareWith_DifferentFeeRate_DetectsMismatch()
    {
        var created = MakeCreated(feeRateSatPerK: 1000);
        var info = RoundInfoResponse.FromCreatedEvent(created) with { FeeRateSatPerK = 9999 };
        var mismatches = info.CompareWith(created);
        Assert.Contains("FeeRate", mismatches);
    }

    [Fact]
    public void CompareWith_DifferentInputTimeout_DetectsMismatch()
    {
        var created = MakeCreated();
        var info = RoundInfoResponse.FromCreatedEvent(created) with { InputTimeoutTicks = 999 };
        var mismatches = info.CompareWith(created);
        Assert.Contains("InputTimeout", mismatches);
    }

    [Fact]
    public void CompareWith_DifferentOutputTimeout_DetectsMismatch()
    {
        var created = MakeCreated();
        var info = RoundInfoResponse.FromCreatedEvent(created) with { OutputTimeoutTicks = 999 };
        var mismatches = info.CompareWith(created);
        Assert.Contains("OutputTimeout", mismatches);
    }

    [Fact]
    public void CompareWith_DifferentSigningTimeout_DetectsMismatch()
    {
        var created = MakeCreated();
        var info = RoundInfoResponse.FromCreatedEvent(created) with { SigningTimeoutTicks = 999 };
        var mismatches = info.CompareWith(created);
        Assert.Contains("SigningTimeout", mismatches);
    }

    [Fact]
    public void CompareWith_DifferentInputCount_DetectsMismatch()
    {
        var created = MakeCreated(inputCountMin: 1, inputCountMax: 10);
        var info = RoundInfoResponse.FromCreatedEvent(created) with { InputCountMax = 20 };
        var mismatches = info.CompareWith(created);
        Assert.Contains("InputCount", mismatches);
    }

    [Fact]
    public void CompareWith_DifferentInputAmount_DetectsMismatch()
    {
        var created = MakeCreated();
        var info = RoundInfoResponse.FromCreatedEvent(created) with { InputAmountMinSat = 1 };
        var mismatches = info.CompareWith(created);
        Assert.Contains("InputAmount", mismatches);
    }

    [Fact]
    public void CompareWith_DifferentOutputCount_DetectsMismatch()
    {
        var created = MakeCreated();
        var info = RoundInfoResponse.FromCreatedEvent(created) with { OutputCountMax = 99 };
        var mismatches = info.CompareWith(created);
        Assert.Contains("OutputCount", mismatches);
    }

    [Fact]
    public void CompareWith_DifferentOutputAmount_DetectsMismatch()
    {
        var created = MakeCreated();
        var info = RoundInfoResponse.FromCreatedEvent(created) with { OutputAmountMaxSat = 1 };
        var mismatches = info.CompareWith(created);
        Assert.Contains("OutputAmount", mismatches);
    }

    [Fact]
    public void CompareWith_DifferentAllowedInputTypes_DetectsMismatch()
    {
        var created = MakeCreated();
        var info = RoundInfoResponse.FromCreatedEvent(created) with
        {
            AllowedInputTypes = new HashSet<ScriptType> { ScriptType.P2WPKH } // removed Taproot
        };
        var mismatches = info.CompareWith(created);
        Assert.Contains("AllowedInputTypes", mismatches);
    }

    [Fact]
    public void CompareWith_DifferentBlameOf_DetectsMismatch()
    {
        var created = MakeCreated(blameOf: "parent-round");
        var info = RoundInfoResponse.FromCreatedEvent(created) with { BlameOf = "different-parent" };
        var mismatches = info.CompareWith(created);
        Assert.Contains("BlameOf", mismatches);
    }

    [Fact]
    public void CompareWith_DifferentIsBlameRound_DetectsMismatch()
    {
        var created = MakeCreated();
        var info = RoundInfoResponse.FromCreatedEvent(created) with { IsBlameRound = true };
        var mismatches = info.CompareWith(created);
        Assert.Contains("IsBlameRound", mismatches);
    }

    [Fact]
    public void CompareWith_MissingCredential_DetectsMismatch()
    {
        var created = MakeCreated();
        var info = RoundInfoResponse.FromCreatedEvent(created) with
        {
            Credentials = new Dictionary<string, CredentialConfiguration>()
        };
        var mismatches = info.CompareWith(created);
        Assert.Contains("Credentials[Amount].Missing", mismatches);
    }

    [Fact]
    public void CompareWith_DifferentCredentialMax_DetectsMismatch()
    {
        var created = MakeCreated(credMax: 1000);
        var info = RoundInfoResponse.FromCreatedEvent(created);
        var creds = new Dictionary<string, CredentialConfiguration>(info.Credentials);
        var tampered = creds["Amount"] with { Max = 9999 };
        creds["Amount"] = tampered;
        info = info with { Credentials = creds };
        var mismatches = info.CompareWith(created);
        Assert.Contains("Credentials[Amount].Max", mismatches);
    }

    [Fact]
    public void CompareWith_DifferentIssuerParameters_DetectsMismatch()
    {
        var created = MakeCreated();
        var info = RoundInfoResponse.FromCreatedEvent(created);
        var differentKey = new CredentialIssuerSecretKey(new InsecureRandom());
        var creds = new Dictionary<string, CredentialConfiguration>(info.Credentials);
        creds["Amount"] = creds["Amount"] with { Parameters = differentKey.ComputeCredentialIssuerParameters() };
        info = info with { Credentials = creds };
        var mismatches = info.CompareWith(created);
        Assert.Contains("Credentials[Amount].IssuerParameters", mismatches);
    }

    [Fact]
    public void CompareWith_MultipleMismatches_ReportsAll()
    {
        var created = MakeCreated();
        var info = RoundInfoResponse.FromCreatedEvent(created) with
        {
            RoundId = "tampered",
            FeeRateSatPerK = 9999,
            InputTimeoutTicks = 1
        };
        var mismatches = info.CompareWith(created);
        Assert.Contains("RoundId", mismatches);
        Assert.Contains("FeeRate", mismatches);
        Assert.Contains("InputTimeout", mismatches);
        Assert.Equal(3, mismatches.Count);
    }

    [Fact]
    public void EquivocationDetected_ErrorCode_Exists()
    {
        var code = Kompaktor.Errors.KompaktorProtocolErrorCode.EquivocationDetected;
        Assert.Equal("EquivocationDetected", code.ToString());
    }

    [Fact]
    public void FromCreatedEvent_BlameRound_SetsCorrectly()
    {
        var created = MakeCreated(blameOf: "parent-abc");
        var info = RoundInfoResponse.FromCreatedEvent(created);
        Assert.True(info.IsBlameRound);
        Assert.Equal("parent-abc", info.BlameOf);
    }

    [Fact]
    public void FromCreatedEvent_NormalRound_NotBlame()
    {
        var created = MakeCreated();
        var info = RoundInfoResponse.FromCreatedEvent(created);
        Assert.False(info.IsBlameRound);
        Assert.Null(info.BlameOf);
    }
}
