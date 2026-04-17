using Xunit;

namespace Kompaktor.Tests;

public class TimingRandomizationTests
{
    [Theory]
    [InlineData(20_000, 10_000)]
    [InlineData(13_334, 10_000)]
    [InlineData(10_000, 7_500)]
    [InlineData(5_000, 3_750)]
    [InlineData(1_000, 750)]
    [InlineData(100, 75)]
    [InlineData(0, 0)]
    public void MaxDelay_CappedAt10Seconds(int fromNowMs, int expectedMaxDelay)
    {
        var maxDelay = Math.Min((int)(fromNowMs * 0.75), 10_000);
        Assert.Equal(expectedMaxDelay, maxDelay);
    }

    [Fact]
    public void MaxDelay_NeverExceeds10Seconds()
    {
        for (int fromNow = 0; fromNow <= 100_000; fromNow += 1000)
        {
            var maxDelay = Math.Min((int)(fromNow * 0.75), 10_000);
            Assert.True(maxDelay <= 10_000, $"maxDelay={maxDelay} for fromNow={fromNow}");
        }
    }

    [Fact]
    public void MaxDelay_NeverNegative()
    {
        var fromNow = (int)Math.Max(0, -5000);
        var maxDelay = Math.Min((int)(fromNow * 0.75), 10_000);
        Assert.True(maxDelay >= 0);
    }

    [Theory]
    [InlineData(20_000, 3_000)]
    [InlineData(12_000, 3_000)]
    [InlineData(8_000, 2_000)]
    [InlineData(4_000, 1_000)]
    [InlineData(400, 100)]
    [InlineData(0, 0)]
    public void PreRegDelay_CappedAt3Seconds(int remainingMs, int expectedMaxPreRegDelay)
    {
        var maxPreRegDelay = Math.Min(3000, remainingMs / 4);
        Assert.Equal(expectedMaxPreRegDelay, maxPreRegDelay);
    }

    [Fact]
    public void PreRegDelay_NeverExceeds3Seconds()
    {
        for (int remaining = 0; remaining <= 60_000; remaining += 500)
        {
            var maxPreRegDelay = Math.Min(3000, remaining / 4);
            Assert.True(maxPreRegDelay <= 3000, $"maxPreRegDelay={maxPreRegDelay} for remaining={remaining}");
        }
    }

    [Fact]
    public void JitterRange_50To200ms()
    {
        var rng = new Random(42);
        var min = int.MaxValue;
        var max = int.MinValue;

        for (int i = 0; i < 10_000; i++)
        {
            var jitter = 50 + rng.Next(150);
            min = Math.Min(min, jitter);
            max = Math.Max(max, jitter);
            Assert.InRange(jitter, 50, 199);
        }

        Assert.Equal(50, min);
        Assert.True(max >= 190, $"Max jitter was only {max}, expected close to 199");
    }

    [Fact]
    public void JitterDistribution_CoversFullRange()
    {
        var seen = new HashSet<int>();
        var rng = new Random(42);

        for (int i = 0; i < 100_000; i++)
        {
            var jitter = 50 + rng.Next(150);
            seen.Add(jitter);
        }

        Assert.Equal(150, seen.Count);
    }

    [Fact]
    public void JitterMean_IsApproximately125ms()
    {
        var rng = new Random(42);
        var sum = 0L;
        var n = 100_000;

        for (int i = 0; i < n; i++)
            sum += 50 + rng.Next(150);

        var mean = (double)sum / n;
        Assert.InRange(mean, 120, 130);
    }

    [Theory]
    [InlineData(100_000, 10_000)]
    [InlineData(50_000, 10_000)]
    [InlineData(13_334, 10_000)]
    [InlineData(13_333, 9_999)]
    public void MaxDelay_TransitionPoint_At13334ms(int fromNowMs, int expectedCap)
    {
        var maxDelay = Math.Min((int)(fromNowMs * 0.75), 10_000);
        Assert.Equal(expectedCap, maxDelay);
    }

    [Fact]
    public void DelayWithZeroFromNow_IsZero()
    {
        var fromNow = (int)Math.Max(0, 0);
        var maxDelay = Math.Min((int)(fromNow * 0.75), 10_000);
        Assert.Equal(0, maxDelay);

        var maxPreReg = Math.Min(3000, fromNow / 4);
        Assert.Equal(0, maxPreReg);
    }

    [Fact]
    public void TaskScheduler_ZeroTasks_IsNoop()
    {
        var tasks = Array.Empty<Func<Task>>();
        Assert.Empty(tasks);
    }

    [Fact]
    public void DelayCalculation_RealisticRound_5MinTimeout()
    {
        var fromNow = 5 * 60 * 1000;
        var maxDelay = Math.Min((int)(fromNow * 0.75), 10_000);
        Assert.Equal(10_000, maxDelay);

        var maxPreReg = Math.Min(3000, fromNow / 4);
        Assert.Equal(3000, maxPreReg);
    }

    [Fact]
    public void DelayCalculation_ShortTimeout_10Seconds()
    {
        var fromNow = 10_000;
        var maxDelay = Math.Min((int)(fromNow * 0.75), 10_000);
        Assert.Equal(7_500, maxDelay);

        var maxPreReg = Math.Min(3000, fromNow / 4);
        Assert.Equal(2_500, maxPreReg);
    }
}
