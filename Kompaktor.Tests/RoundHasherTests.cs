using Kompaktor.Credentials;
using Kompaktor.Models;
using Kompaktor.Utils;
using NBitcoin;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Randomness;
using Xunit;

namespace Kompaktor.Tests;

public class RoundHasherTests
{
    private static readonly CredentialIssuerSecretKey Key = new(new InsecureRandom());

    private static Dictionary<CredentialType, CredentialConfiguration> MakeCredentials(
        long max = 1000L, int issuanceMin = 2, int issuanceMax = 2) =>
        new()
        {
            [CredentialType.Amount] = new CredentialConfiguration(
                max,
                new IntRange(issuanceMin, issuanceMax),
                new IntRange(issuanceMin, issuanceMax),
                Key.ComputeCredentialIssuerParameters())
        };

    private static string Hash(
        DateTimeOffset? startTime = null,
        FeeRate? feeRate = null,
        TimeSpan? inputTimeout = null,
        TimeSpan? outputTimeout = null,
        TimeSpan? signingTimeout = null,
        IntRange? inputCount = null,
        MoneyRange? inputAmount = null,
        IntRange? outputCount = null,
        MoneyRange? outputAmount = null,
        Dictionary<CredentialType, CredentialConfiguration>? credentials = null,
        string? blameOf = null) =>
        RoundHasher.CalculateHash(
            startTime ?? DateTimeOffset.UnixEpoch,
            feeRate ?? new FeeRate(Money.Satoshis(1000)),
            inputTimeout ?? TimeSpan.FromMinutes(5),
            outputTimeout ?? TimeSpan.FromMinutes(5),
            signingTimeout ?? TimeSpan.FromMinutes(5),
            inputCount ?? new IntRange(1, 10),
            inputAmount ?? new MoneyRange(Money.Satoshis(5000), Money.Coins(1)),
            outputCount ?? new IntRange(1, 10),
            outputAmount ?? new MoneyRange(Money.Satoshis(5000), Money.Coins(1)),
            credentials ?? MakeCredentials(),
            blameOf);

    [Fact]
    public void SameParameters_ProduceSameHash()
    {
        var hash1 = Hash();
        var hash2 = Hash();
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void Hash_IsLowercaseHex_64Chars()
    {
        var hash = Hash();
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void DifferentStartTime_ProducesDifferentHash()
    {
        var hash1 = Hash(startTime: DateTimeOffset.UnixEpoch);
        var hash2 = Hash(startTime: DateTimeOffset.UnixEpoch.AddSeconds(1));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void DifferentFeeRate_ProducesDifferentHash()
    {
        var hash1 = Hash(feeRate: new FeeRate(Money.Satoshis(1000)));
        var hash2 = Hash(feeRate: new FeeRate(Money.Satoshis(2000)));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void DifferentInputTimeout_ProducesDifferentHash()
    {
        var hash1 = Hash(inputTimeout: TimeSpan.FromMinutes(5));
        var hash2 = Hash(inputTimeout: TimeSpan.FromMinutes(10));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void DifferentOutputTimeout_ProducesDifferentHash()
    {
        var hash1 = Hash(outputTimeout: TimeSpan.FromMinutes(5));
        var hash2 = Hash(outputTimeout: TimeSpan.FromMinutes(10));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void DifferentSigningTimeout_ProducesDifferentHash()
    {
        var hash1 = Hash(signingTimeout: TimeSpan.FromMinutes(5));
        var hash2 = Hash(signingTimeout: TimeSpan.FromMinutes(10));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void DifferentInputCount_ProducesDifferentHash()
    {
        var hash1 = Hash(inputCount: new IntRange(1, 10));
        var hash2 = Hash(inputCount: new IntRange(2, 10));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void DifferentInputAmount_ProducesDifferentHash()
    {
        var hash1 = Hash(inputAmount: new MoneyRange(Money.Satoshis(5000), Money.Coins(1)));
        var hash2 = Hash(inputAmount: new MoneyRange(Money.Satoshis(10000), Money.Coins(1)));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void DifferentOutputCount_ProducesDifferentHash()
    {
        var hash1 = Hash(outputCount: new IntRange(1, 10));
        var hash2 = Hash(outputCount: new IntRange(1, 20));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void DifferentOutputAmount_ProducesDifferentHash()
    {
        var hash1 = Hash(outputAmount: new MoneyRange(Money.Satoshis(5000), Money.Coins(1)));
        var hash2 = Hash(outputAmount: new MoneyRange(Money.Satoshis(5000), Money.Coins(2)));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void DifferentCredentialMax_ProducesDifferentHash()
    {
        var hash1 = Hash(credentials: MakeCredentials(max: 1000));
        var hash2 = Hash(credentials: MakeCredentials(max: 2000));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void DifferentCredentialIssuance_ProducesDifferentHash()
    {
        var hash1 = Hash(credentials: MakeCredentials(issuanceMin: 2, issuanceMax: 2));
        var hash2 = Hash(credentials: MakeCredentials(issuanceMin: 1, issuanceMax: 3));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void BlameOf_ChangesHash()
    {
        var hashNormal = Hash(blameOf: null);
        var hashBlame = Hash(blameOf: "abc123");
        Assert.NotEqual(hashNormal, hashBlame);
    }

    [Fact]
    public void DifferentBlameOf_ProducesDifferentHashes()
    {
        var hash1 = Hash(blameOf: "round-a");
        var hash2 = Hash(blameOf: "round-b");
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void BlameOf_IsDeterministic()
    {
        var hash1 = Hash(blameOf: "blame-round-id");
        var hash2 = Hash(blameOf: "blame-round-id");
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void EveryParameter_IsIncludedInHash()
    {
        var baseline = Hash();

        Assert.NotEqual(baseline, Hash(startTime: DateTimeOffset.UtcNow));
        Assert.NotEqual(baseline, Hash(feeRate: new FeeRate(Money.Satoshis(9999))));
        Assert.NotEqual(baseline, Hash(inputTimeout: TimeSpan.FromMinutes(99)));
        Assert.NotEqual(baseline, Hash(outputTimeout: TimeSpan.FromMinutes(99)));
        Assert.NotEqual(baseline, Hash(signingTimeout: TimeSpan.FromMinutes(99)));
        Assert.NotEqual(baseline, Hash(inputCount: new IntRange(3, 7)));
        Assert.NotEqual(baseline, Hash(inputAmount: new MoneyRange(Money.Satoshis(1), Money.Coins(1))));
        Assert.NotEqual(baseline, Hash(outputCount: new IntRange(3, 7)));
        Assert.NotEqual(baseline, Hash(outputAmount: new MoneyRange(Money.Satoshis(1), Money.Coins(1))));
        Assert.NotEqual(baseline, Hash(credentials: MakeCredentials(max: 9999)));
        Assert.NotEqual(baseline, Hash(blameOf: "any-blame"));
    }
}
