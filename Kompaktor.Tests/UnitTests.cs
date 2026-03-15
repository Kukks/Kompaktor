using Kompaktor.Contracts;
using Kompaktor.Credentials;
using Kompaktor.Errors;
using Kompaktor.Models;
using Kompaktor.Prison;
using Kompaktor.Utils;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Xunit;

namespace Kompaktor.Tests;

#region Prison Tests

public class KompaktorPrisonTests
{
    private static OutPoint MakeOutpoint(int n = 0) =>
        new(uint256.Parse($"{n:D64}"), 0);

    [Fact]
    public void NewPrison_NoBans()
    {
        var prison = new KompaktorPrison();
        Assert.False(prison.IsBanned(MakeOutpoint()));
        Assert.Empty(prison.ActiveBans);
    }

    [Fact]
    public void Ban_CreatesRecord_WithCorrectFields()
    {
        var prison = new KompaktorPrison();
        var outpoint = MakeOutpoint(1);

        var record = prison.Ban(outpoint, BanReason.FailedToSign);

        Assert.True(prison.IsBanned(outpoint));
        Assert.Equal(outpoint, record.Outpoint);
        Assert.Equal(BanReason.FailedToSign, record.Reason);
        Assert.Equal(1, record.OffenseCount);
        Assert.True(record.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void GetBan_ReturnsRecord_WhenBanned()
    {
        var prison = new KompaktorPrison();
        var outpoint = MakeOutpoint(1);
        prison.Ban(outpoint, BanReason.DoubleSpend);

        var record = prison.GetBan(outpoint);

        Assert.NotNull(record);
        Assert.Equal(BanReason.DoubleSpend, record.Reason);
    }

    [Fact]
    public void GetBan_ReturnsNull_WhenNotBanned()
    {
        var prison = new KompaktorPrison();
        Assert.Null(prison.GetBan(MakeOutpoint()));
    }

    [Fact]
    public void RepeatOffense_IncreasesOffenseCount()
    {
        var prison = new KompaktorPrison();
        var outpoint = MakeOutpoint(1);

        var first = prison.Ban(outpoint, BanReason.FailedToSign);
        var second = prison.Ban(outpoint, BanReason.FailedToSign);

        Assert.Equal(1, first.OffenseCount);
        Assert.Equal(2, second.OffenseCount);
    }

    [Fact]
    public void RepeatOffense_IncreasesExpirationDuration()
    {
        var prison = new KompaktorPrison();
        var outpoint = MakeOutpoint(1);

        var first = prison.Ban(outpoint, BanReason.FailedToSign);
        var firstDuration = first.ExpiresAt - first.BannedAt;

        var second = prison.Ban(outpoint, BanReason.FailedToSign);
        var secondDuration = second.ExpiresAt - second.BannedAt;

        // Second offense should have 2x duration (PenaltyFactor = 2.0)
        Assert.True(secondDuration > firstDuration);
    }

    [Fact]
    public void BanDuration_CappedAtMax()
    {
        var options = new PrisonOptions
        {
            FailedToSignDuration = TimeSpan.FromDays(10),
            PenaltyFactor = 10.0,
            MaxBanDuration = TimeSpan.FromDays(30)
        };
        var prison = new KompaktorPrison(options);
        var outpoint = MakeOutpoint(1);

        // First ban: 10 days, second: 100 days -> capped at 30
        prison.Ban(outpoint, BanReason.FailedToSign);
        var record = prison.Ban(outpoint, BanReason.FailedToSign);
        var duration = record.ExpiresAt - record.BannedAt;

        Assert.True(duration <= options.MaxBanDuration + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void BanDisruptors_BansMultipleCoins()
    {
        var prison = new KompaktorPrison();
        var outpoints = Enumerable.Range(1, 5).Select(MakeOutpoint).ToList();

        prison.BanDisruptors(outpoints);

        foreach (var op in outpoints)
            Assert.True(prison.IsBanned(op));
    }

    [Fact]
    public void ExpiredBan_IsNotReported()
    {
        var options = new PrisonOptions
        {
            FailedToSignDuration = TimeSpan.FromMilliseconds(1)
        };
        var prison = new KompaktorPrison(options);
        var outpoint = MakeOutpoint(1);

        prison.Ban(outpoint, BanReason.FailedToSign);
        Thread.Sleep(10); // Let ban expire

        Assert.False(prison.IsBanned(outpoint));
        Assert.Null(prison.GetBan(outpoint));
    }

    [Fact]
    public void PurgeExpired_RemovesExpiredBans()
    {
        var options = new PrisonOptions
        {
            FailedToSignDuration = TimeSpan.FromMilliseconds(1)
        };
        var prison = new KompaktorPrison(options);

        prison.Ban(MakeOutpoint(1), BanReason.FailedToSign);
        prison.Ban(MakeOutpoint(2), BanReason.FailedToSign);
        Thread.Sleep(10);

        var purged = prison.PurgeExpired();
        Assert.Equal(2, purged);
        Assert.Empty(prison.ActiveBans);
    }

    [Fact]
    public void ActiveBans_OnlyReturnsNonExpired()
    {
        var options = new PrisonOptions
        {
            FailedToSignDuration = TimeSpan.FromMilliseconds(1),
            DoubleSpendDuration = TimeSpan.FromHours(1)
        };
        var prison = new KompaktorPrison(options);

        prison.Ban(MakeOutpoint(1), BanReason.FailedToSign); // expires immediately
        prison.Ban(MakeOutpoint(2), BanReason.DoubleSpend);  // long ban
        Thread.Sleep(10);

        Assert.Single(prison.ActiveBans);
    }

    [Theory]
    [InlineData(BanReason.FailedToSign)]
    [InlineData(BanReason.FailedToVerify)]
    [InlineData(BanReason.DoubleSpend)]
    [InlineData(BanReason.RepeatedFailure)]
    [InlineData(BanReason.BannedCoinReuse)]
    public void AllBanReasons_ProduceBan(BanReason reason)
    {
        var prison = new KompaktorPrison();
        var outpoint = MakeOutpoint((int)reason);

        var record = prison.Ban(outpoint, reason);

        Assert.True(prison.IsBanned(outpoint));
        Assert.Equal(reason, record.Reason);
    }
}

#endregion

#region RetryHelper Tests

public class RetryHelperTests
{
    [Fact]
    public async Task SuccessOnFirstAttempt_NoRetries()
    {
        var callCount = 0;

        var result = await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            callCount++;
            return 42;
        }, maxRetries: 3, baseDelay: TimeSpan.FromMilliseconds(1));

        Assert.Equal(42, result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task TransientFailure_RetriesAndSucceeds()
    {
        var callCount = 0;

        var result = await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            callCount++;
            if (callCount < 3)
                throw new HttpRequestException("transient");
            return "success";
        }, maxRetries: 3, baseDelay: TimeSpan.FromMilliseconds(1));

        Assert.Equal("success", result);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task AllRetriesExhausted_ThrowsLastException()
    {
        var callCount = 0;

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await RetryHelper.ExecuteWithRetryAsync<int>(async () =>
            {
                callCount++;
                throw new HttpRequestException($"attempt {callCount}");
            }, maxRetries: 2, baseDelay: TimeSpan.FromMilliseconds(1));
        });

        Assert.Equal(3, callCount); // initial + 2 retries
    }

    [Fact]
    public async Task ProtocolException_NeverRetried()
    {
        var callCount = 0;

        await Assert.ThrowsAsync<KompaktorProtocolException>(async () =>
        {
            await RetryHelper.ExecuteWithRetryAsync<int>(async () =>
            {
                callCount++;
                throw new KompaktorProtocolException(KompaktorProtocolErrorCode.WrongPhase, "nope");
            }, maxRetries: 3, baseDelay: TimeSpan.FromMilliseconds(1));
        });

        Assert.Equal(1, callCount); // No retries
    }

    [Fact]
    public async Task CancellationToken_StopsRetries()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await RetryHelper.ExecuteWithRetryAsync<int>(async () =>
            {
                throw new HttpRequestException("fail");
            }, maxRetries: 5, baseDelay: TimeSpan.FromMilliseconds(1), cancellationToken: cts.Token);
        });
    }

    [Fact]
    public async Task VoidOverload_Works()
    {
        var callCount = 0;

        await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            callCount++;
            if (callCount < 2)
                throw new TimeoutException("timeout");
        }, maxRetries: 3, baseDelay: TimeSpan.FromMilliseconds(1));

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task CustomShouldRetry_Respected()
    {
        var callCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await RetryHelper.ExecuteWithRetryAsync<int>(async () =>
            {
                callCount++;
                throw new InvalidOperationException("custom");
            }, maxRetries: 3, baseDelay: TimeSpan.FromMilliseconds(1),
            shouldRetry: _ => false); // Never retry
        });

        Assert.Equal(1, callCount);
    }
}

#endregion

#region Error Types Tests

public class KompaktorErrorsTests
{
    [Fact]
    public void ProtocolException_PreservesFields()
    {
        var inner = new Exception("inner");
        var ex = new KompaktorProtocolException(
            KompaktorProtocolErrorCode.InputBanned,
            "coin is banned",
            "round-123",
            inner);

        Assert.Equal(KompaktorProtocolErrorCode.InputBanned, ex.ErrorCode);
        Assert.Equal("coin is banned", ex.Message);
        Assert.Equal("round-123", ex.RoundId);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void ProtocolException_OptionalRoundId()
    {
        var ex = new KompaktorProtocolException(KompaktorProtocolErrorCode.InternalError, "oops");
        Assert.Null(ex.RoundId);
    }

    [Fact]
    public void OperationResult_Success_HasValue()
    {
        var result = OperationResult<int>.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void OperationResult_Failure_HasError()
    {
        var error = new KompaktorProtocolException(KompaktorProtocolErrorCode.WrongPhase, "bad");
        var result = OperationResult<int>.Failure(error);

        Assert.False(result.IsSuccess);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public void OperationResult_ImplicitConversion_FromValue()
    {
        OperationResult<string> result = "hello";

        Assert.True(result.IsSuccess);
        Assert.Equal("hello", result.Value);
    }
}

#endregion

#region Circuit Tests

public class CircuitTests
{
    [Fact]
    public void DefaultCircuitFactory_CreatesCircuitWithId()
    {
        var factory = new DefaultCircuitFactory();
        var circuit = factory.Create("test-identity");

        Assert.Equal("test-identity", circuit.Id);
    }

    [Fact]
    public void DefaultCircuit_CreateHandler_ReturnsHttpClientHandler()
    {
        var factory = new DefaultCircuitFactory();
        var circuit = factory.Create("test");

        using var handler = circuit.CreateHandler();
        Assert.IsType<HttpClientHandler>(handler);
    }

    [Fact]
    public void DifferentIdentities_ProduceDifferentCircuits()
    {
        var factory = new DefaultCircuitFactory();
        var c1 = factory.Create("alice");
        var c2 = factory.Create("bob");

        Assert.NotEqual(c1.Id, c2.Id);
    }

    [Fact]
    public async Task DefaultCircuit_DisposeAsync_Completes()
    {
        var factory = new DefaultCircuitFactory();
        var circuit = factory.Create("disposable");

        // Should not throw
        await circuit.DisposeAsync();
    }
}

#endregion

#region Configuration Tests

public class ConfigurationTests
{
    [Fact]
    public void CoordinatorOptions_HasSensibleDefaults()
    {
        var opts = new KompaktorCoordinatorOptions();

        Assert.True(opts.FeeRate.SatoshiPerByte > 0);
        Assert.True(opts.InputTimeout > TimeSpan.Zero);
        Assert.True(opts.OutputTimeout > TimeSpan.Zero);
        Assert.True(opts.SigningTimeout > TimeSpan.Zero);
        Assert.True(opts.MinInputCount >= 1);
        Assert.True(opts.MaxInputCount >= opts.MinInputCount);
        Assert.True(opts.MaxCredentialValue > 0);
        Assert.True(opts.MaxConcurrentRounds >= 1);
    }

    [Fact]
    public void ClientOptions_HasSensibleDefaults()
    {
        var opts = new KompaktorClientOptions();

        Assert.True(opts.MaxCoinsPerRound > 0);
        Assert.True(opts.ApiCallTimeout > TimeSpan.Zero);
        Assert.True(opts.MaxRetries > 0);
        Assert.True(opts.RetryBaseDelay > TimeSpan.Zero);
    }

    [Fact]
    public void PrisonOptions_HasSensibleDefaults()
    {
        var opts = new PrisonOptions();

        Assert.True(opts.FailedToSignDuration > TimeSpan.Zero);
        Assert.True(opts.FailedToVerifyDuration > opts.FailedToSignDuration);
        Assert.True(opts.DoubleSpendDuration > opts.FailedToVerifyDuration);
        Assert.True(opts.PenaltyFactor > 1.0);
        Assert.True(opts.MaxBanDuration >= opts.DoubleSpendDuration);
    }
}

#endregion

#region CredentialConfiguration Tests

public class CredentialConfigurationTests
{
    [Fact]
    public void Deconstruct_RoundTrips()
    {
        var key = new WabiSabi.Crypto.CredentialIssuerSecretKey(new WabiSabi.Crypto.Randomness.InsecureRandom());
        var config = new CredentialConfiguration(
            1000L,
            new IntRange(2, 2),
            new IntRange(2, 2),
            key.ComputeCredentialIssuerParameters());

        var (max, issuanceIn, issuanceOut, parameters) = config;

        Assert.Equal(1000L, max);
        Assert.Equal(2, issuanceIn.Min);
        Assert.Equal(2, issuanceOut.Max);
        Assert.NotNull(parameters);
    }
}

#endregion

#region KompaktorRoundEventCreated Tests

public class RoundEventCreatedTests
{
    [Fact]
    public void Constructor_SetsAllFields()
    {
        var roundId = "test-round";
        var feeRate = new FeeRate(5m);
        var creds = new Dictionary<CredentialType, CredentialConfiguration>();

        var evt = new KompaktorRoundEventCreated(
            roundId, feeRate,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
            new IntRange(1, 10), new MoneyRange(Money.Satoshis(1000), Money.Coins(1)),
            new IntRange(1, 20), new MoneyRange(Money.Satoshis(500), Money.Coins(1)),
            creds);

        Assert.Equal(roundId, evt.RoundId);
        Assert.Equal(feeRate, evt.FeeRate);
        Assert.Equal(KompaktorStatus.InputRegistration, evt.Status);
        Assert.Same(creds, evt.Credentials);
    }

    [Fact]
    public void Deconstruct_RoundTrips()
    {
        var creds = new Dictionary<CredentialType, CredentialConfiguration>();
        var evt = new KompaktorRoundEventCreated(
            "r1", new FeeRate(1m),
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30),
            new IntRange(1, 5), new MoneyRange(Money.Satoshis(100), Money.Coins(1)),
            new IntRange(1, 10), new MoneyRange(Money.Satoshis(50), Money.Coins(1)),
            creds);

        var (roundId, feeRate, inputTimeout, outputTimeout, signingTimeout,
            inputCount, inputAmount, outputCount, outputAmount, credentials) = evt;

        Assert.Equal("r1", roundId);
        Assert.Equal(TimeSpan.FromSeconds(10), inputTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), outputTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), signingTimeout);
    }
}

#endregion

#region Prison Concurrency Tests

public class PrisonConcurrencyTests
{
    private static OutPoint MakeOutpoint(int n) =>
        new(uint256.Parse($"{n:D64}"), 0);

    [Fact]
    public void ConcurrentBans_AllRecorded()
    {
        var prison = new KompaktorPrison();
        var count = 100;

        Parallel.For(0, count, i =>
        {
            prison.Ban(MakeOutpoint(i), BanReason.FailedToSign);
        });

        Assert.Equal(count, prison.ActiveBans.Count);
    }

    [Fact]
    public void ConcurrentBanAndCheck_NoExceptions()
    {
        var prison = new KompaktorPrison();
        var outpoint = MakeOutpoint(1);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var tasks = new[]
        {
            Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                    prison.Ban(outpoint, BanReason.RepeatedFailure);
            }),
            Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                    prison.IsBanned(outpoint);
            }),
            Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                    _ = prison.ActiveBans;
            })
        };

        Task.WaitAll(tasks);
    }
}

#endregion

#region DependencyGraph2 Scale Tests

public class DependencyGraph2ScaleTests
{
    [Fact]
    public void CanMergeManyInputsIntoSingleOutput()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        var logger = loggerFactory.CreateLogger("dg-scale");
        var inputRange = new IntRange(2, 2);
        var outputRange = new IntRange(2, 2);

        // Simulate receiver scenario: 200 credentials of 10M sats each + 200 zeros = 400 inputs
        // Target: single output of 2B sats (200 * 10M)
        var count = 200;
        var perInput = 10_000_000L;
        var ins = Enumerable.Range(0, count)
            .SelectMany(_ => new[] { perInput, 0L })
            .ToArray();
        var outs = new[] { perInput * count };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = DependencyGraph2.Compute(logger, ins, outs, inputRange, outputRange);
        sw.Stop();

        logger.LogInformation("Computed {Actions} actions with {Depth} depth in {Elapsed}ms",
            result.CountDescendants(), result.GetMaxDepth(), sw.ElapsedMilliseconds);

        // Verify all outputs were registered
        var registered = result.NestedOutputsRegistered;
        Assert.Single(registered);
        Assert.Equal(perInput * count, registered[0].Amount);

        // Should complete in reasonable time (< 30 seconds)
        Assert.True(sw.ElapsedMilliseconds < 30000, $"Took too long: {sw.ElapsedMilliseconds}ms");
    }
}

#endregion
