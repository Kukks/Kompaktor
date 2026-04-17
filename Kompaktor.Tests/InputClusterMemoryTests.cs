using Kompaktor.Contracts;
using Kompaktor.Utils;
using NBitcoin;
using Xunit;

namespace Kompaktor.Tests;

public class InputClusterMemoryTests
{
    private static OutPoint Op(int id) =>
        new(uint256.Parse($"{id:D64}"), 0);

    private static Coin FakeCoin(OutPoint op) =>
        new(op, new TxOut(Money.Satoshis(100_000), Script.Empty));

    [Fact]
    public void EmptyHistory_NoExclusions()
    {
        var tracker = new RoundHistoryTracker();
        var excluded = tracker.GetCoinsToExclude(new[] { FakeCoin(Op(1)), FakeCoin(Op(2)) });
        Assert.Empty(excluded);
    }

    [Fact]
    public void SingleCoinRound_NoCluster()
    {
        var tracker = new RoundHistoryTracker();
        tracker.RecordFailedRound(new[] { Op(1) });
        var excluded = tracker.GetCoinsToExclude(new[] { FakeCoin(Op(1)) });
        Assert.Empty(excluded);
    }

    [Fact]
    public void TwoCoinsSameRound_OneExcluded()
    {
        var tracker = new RoundHistoryTracker();
        tracker.RecordFailedRound(new[] { Op(1), Op(2) });

        var excluded = tracker.GetCoinsToExclude(new[] { FakeCoin(Op(1)), FakeCoin(Op(2)) });
        Assert.Single(excluded);
    }

    [Fact]
    public void ThreeCoinsSameRound_TwoExcluded()
    {
        var tracker = new RoundHistoryTracker();
        tracker.RecordFailedRound(new[] { Op(1), Op(2), Op(3) });

        var excluded = tracker.GetCoinsToExclude(new[] { FakeCoin(Op(1)), FakeCoin(Op(2)), FakeCoin(Op(3)) });
        Assert.Equal(2, excluded.Count);
    }

    [Fact]
    public void DisjointRounds_SeparateClusters()
    {
        var tracker = new RoundHistoryTracker();
        tracker.RecordFailedRound(new[] { Op(1), Op(2) });
        tracker.RecordFailedRound(new[] { Op(3), Op(4) });

        var excluded = tracker.GetCoinsToExclude(new[] { FakeCoin(Op(1)), FakeCoin(Op(3)) });
        Assert.Empty(excluded);
    }

    [Fact]
    public void DisjointClusters_EachLimitedToOneRepresentative()
    {
        var tracker = new RoundHistoryTracker();
        tracker.RecordFailedRound(new[] { Op(1), Op(2) });
        tracker.RecordFailedRound(new[] { Op(3), Op(4) });

        var excluded = tracker.GetCoinsToExclude(new[]
        {
            FakeCoin(Op(1)), FakeCoin(Op(2)), FakeCoin(Op(3)), FakeCoin(Op(4))
        });
        Assert.Equal(2, excluded.Count);
    }

    [Fact]
    public void TransitiveClosure_BridgesMergeClusters()
    {
        var tracker = new RoundHistoryTracker();
        tracker.RecordFailedRound(new[] { Op(1), Op(2) });
        tracker.RecordFailedRound(new[] { Op(2), Op(3) });

        var excluded = tracker.GetCoinsToExclude(new[]
        {
            FakeCoin(Op(1)), FakeCoin(Op(2)), FakeCoin(Op(3))
        });
        Assert.Equal(2, excluded.Count);
    }

    [Fact]
    public void TransitiveClosure_ChainedRoundsMerge()
    {
        var tracker = new RoundHistoryTracker();
        tracker.RecordFailedRound(new[] { Op(1), Op(2) });
        tracker.RecordFailedRound(new[] { Op(2), Op(3) });
        tracker.RecordFailedRound(new[] { Op(3), Op(4) });

        var excluded = tracker.GetCoinsToExclude(new[]
        {
            FakeCoin(Op(1)), FakeCoin(Op(2)), FakeCoin(Op(3)), FakeCoin(Op(4))
        });
        Assert.Equal(3, excluded.Count);
    }

    [Fact]
    public void ProposedCoinNotInHistory_NeverExcluded()
    {
        var tracker = new RoundHistoryTracker();
        tracker.RecordFailedRound(new[] { Op(1), Op(2) });

        var excluded = tracker.GetCoinsToExclude(new[] { FakeCoin(Op(1)), FakeCoin(Op(99)) });
        Assert.Empty(excluded);
    }

    [Fact]
    public void OnlyOneClusterMember_NoExclusion()
    {
        var tracker = new RoundHistoryTracker();
        tracker.RecordFailedRound(new[] { Op(1), Op(2) });

        var excluded = tracker.GetCoinsToExclude(new[] { FakeCoin(Op(1)) });
        Assert.Empty(excluded);
    }

    [Fact]
    public void ShouldBackOff_FalseInitially()
    {
        var tracker = new RoundHistoryTracker(maxConsecutiveFailures: 3);
        Assert.False(tracker.ShouldBackOff());
        Assert.Equal(0, tracker.ConsecutiveFailures);
    }

    [Fact]
    public void ShouldBackOff_TrueAfterThreshold()
    {
        var tracker = new RoundHistoryTracker(maxConsecutiveFailures: 3);
        tracker.RecordFailedRound(new[] { Op(1) });
        tracker.RecordFailedRound(new[] { Op(1) });
        Assert.False(tracker.ShouldBackOff());
        Assert.Equal(2, tracker.ConsecutiveFailures);

        tracker.RecordFailedRound(new[] { Op(1) });
        Assert.True(tracker.ShouldBackOff());
        Assert.Equal(3, tracker.ConsecutiveFailures);
    }

    [Fact]
    public void ShouldBackOff_ResetsAfterSuccess()
    {
        var tracker = new RoundHistoryTracker(maxConsecutiveFailures: 2);
        tracker.RecordFailedRound(new[] { Op(1) });
        tracker.RecordFailedRound(new[] { Op(1) });
        Assert.True(tracker.ShouldBackOff());

        tracker.RecordSuccess();
        Assert.False(tracker.ShouldBackOff());
        Assert.Equal(0, tracker.ConsecutiveFailures);
    }

    [Fact]
    public void SuccessResets_ButClusterMemoryPersists()
    {
        var tracker = new RoundHistoryTracker(maxConsecutiveFailures: 2);
        tracker.RecordFailedRound(new[] { Op(1), Op(2) });
        tracker.RecordSuccess();

        Assert.False(tracker.ShouldBackOff());
        var excluded = tracker.GetCoinsToExclude(new[] { FakeCoin(Op(1)), FakeCoin(Op(2)) });
        Assert.Single(excluded);
    }

    [Fact]
    public void IncrementalClusterGrowth()
    {
        var tracker = new RoundHistoryTracker();

        tracker.RecordFailedRound(new[] { Op(1), Op(2) });
        var excluded1 = tracker.GetCoinsToExclude(new[] { FakeCoin(Op(1)), FakeCoin(Op(3)) });
        Assert.Empty(excluded1);

        tracker.RecordFailedRound(new[] { Op(2), Op(3) });
        var excluded2 = tracker.GetCoinsToExclude(new[] { FakeCoin(Op(1)), FakeCoin(Op(3)) });
        Assert.Single(excluded2);
    }

    [Fact]
    public void LargeCluster_OnlyOneRepresentativeAllowed()
    {
        var tracker = new RoundHistoryTracker();
        var coins = Enumerable.Range(1, 10).Select(Op).ToArray();
        tracker.RecordFailedRound(coins);

        var excluded = tracker.GetCoinsToExclude(coins.Select(FakeCoin));
        Assert.Equal(9, excluded.Count);
    }

    [Fact]
    public void ImplementsInterface()
    {
        IRoundHistoryTracker tracker = new RoundHistoryTracker();
        tracker.RecordFailedRound(new[] { Op(1) });
        tracker.RecordSuccess();
        _ = tracker.ShouldBackOff();
        _ = tracker.ConsecutiveFailures;
        _ = tracker.GetCoinsToExclude(new[] { FakeCoin(Op(1)) });
    }

    [Fact]
    public void DefaultMaxConsecutiveFailures_IsThree()
    {
        var tracker = new RoundHistoryTracker();
        tracker.RecordFailedRound(new[] { Op(1) });
        tracker.RecordFailedRound(new[] { Op(1) });
        Assert.False(tracker.ShouldBackOff());
        tracker.RecordFailedRound(new[] { Op(1) });
        Assert.True(tracker.ShouldBackOff());
    }
}
