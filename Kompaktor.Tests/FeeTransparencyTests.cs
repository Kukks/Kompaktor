using Kompaktor.Models;
using NBitcoin;
using Xunit;

namespace Kompaktor.Tests;

public class FeeTransparencyTests
{
    private static FeeBreakdown MakeBreakdown(
        long totalInputsSat, long totalOutputsSat,
        long expectedMiningFeeSat)
    {
        var totalInputs = Money.Satoshis(totalInputsSat);
        var totalOutputs = Money.Satoshis(totalOutputsSat);
        var actualFee = totalInputs - totalOutputs;
        var expectedMiningFee = Money.Satoshis(expectedMiningFeeSat);
        var surplus = actualFee - expectedMiningFee;
        return new FeeBreakdown(totalInputs, totalOutputs, actualFee, expectedMiningFee, surplus);
    }

    [Fact]
    public void FeeBreakdown_ActualFee_IsInputsMinusOutputs()
    {
        var bd = MakeBreakdown(100_000, 95_000, 4_000);
        Assert.Equal(Money.Satoshis(5_000), bd.ActualFee);
    }

    [Fact]
    public void FeeBreakdown_Surplus_IsActualFeeMinusExpected()
    {
        var bd = MakeBreakdown(100_000, 95_000, 4_000);
        Assert.Equal(Money.Satoshis(1_000), bd.Surplus);
    }

    [Fact]
    public void FeeBreakdown_ZeroSurplus_WhenFeesMatch()
    {
        var bd = MakeBreakdown(100_000, 95_000, 5_000);
        Assert.Equal(Money.Zero, bd.Surplus);
    }

    [Fact]
    public void HasExcessiveSurplus_False_WhenZero()
    {
        var bd = MakeBreakdown(100_000, 95_000, 5_000);
        Assert.False(bd.HasExcessiveSurplus());
    }

    [Fact]
    public void HasExcessiveSurplus_False_WhenBelowDustThreshold()
    {
        var bd = MakeBreakdown(100_000, 95_000, 4_455);
        Assert.Equal(Money.Satoshis(545), bd.Surplus);
        Assert.False(bd.HasExcessiveSurplus());
    }

    [Fact]
    public void HasExcessiveSurplus_False_WhenExactlyAtThreshold()
    {
        var bd = MakeBreakdown(100_000, 95_000, 4_454);
        Assert.Equal(Money.Satoshis(546), bd.Surplus);
        Assert.False(bd.HasExcessiveSurplus());
    }

    [Fact]
    public void HasExcessiveSurplus_True_WhenAboveThreshold()
    {
        var bd = MakeBreakdown(100_000, 95_000, 4_453);
        Assert.Equal(Money.Satoshis(547), bd.Surplus);
        Assert.True(bd.HasExcessiveSurplus());
    }

    [Fact]
    public void HasExcessiveSurplus_CustomThreshold()
    {
        var bd = MakeBreakdown(100_000, 95_000, 4_000);
        Assert.Equal(Money.Satoshis(1_000), bd.Surplus);
        Assert.False(bd.HasExcessiveSurplus(Money.Satoshis(1_000)));
        Assert.True(bd.HasExcessiveSurplus(Money.Satoshis(999)));
    }

    [Fact]
    public void HasExcessiveSurplus_ZeroThreshold_AnyPositiveSurplusExcessive()
    {
        var bd = MakeBreakdown(100_000, 99_999, 0);
        Assert.True(bd.HasExcessiveSurplus(Money.Zero));
    }

    [Fact]
    public void HasExcessiveSurplus_NegativeSurplus_IsNotExcessive()
    {
        var bd = MakeBreakdown(100_000, 95_000, 6_000);
        Assert.Equal(Money.Satoshis(-1_000), bd.Surplus);
        Assert.False(bd.HasExcessiveSurplus());
    }

    [Fact]
    public void FeeBreakdown_RealisticCoinjoin()
    {
        var inputsSat = 5 * 100_000L;
        var miningFee = 2_500L;
        var outputsSat = inputsSat - miningFee;
        var bd = MakeBreakdown(inputsSat, outputsSat, miningFee);

        Assert.Equal(Money.Zero, bd.Surplus);
        Assert.False(bd.HasExcessiveSurplus());
    }

    [Fact]
    public void FeeBreakdown_CoordinatorSkimmingAttack_Detected()
    {
        var inputsSat = 10 * 100_000L;
        var legitimateMiningFee = 5_000L;
        var coordinatorSkim = 1_000L;
        var outputsSat = inputsSat - legitimateMiningFee - coordinatorSkim;
        var bd = MakeBreakdown(inputsSat, outputsSat, legitimateMiningFee);

        Assert.Equal(Money.Satoshis(coordinatorSkim), bd.Surplus);
        Assert.True(bd.HasExcessiveSurplus());
    }

    [Fact]
    public void FeeBreakdown_LargeCoordinatorSkim_Detected()
    {
        var inputsSat = 10 * 1_000_000L;
        var legitimateMiningFee = 10_000L;
        var coordinatorSkim = 50_000L;
        var outputsSat = inputsSat - legitimateMiningFee - coordinatorSkim;
        var bd = MakeBreakdown(inputsSat, outputsSat, legitimateMiningFee);

        Assert.Equal(Money.Satoshis(coordinatorSkim), bd.Surplus);
        Assert.True(bd.HasExcessiveSurplus());
    }

    [Fact]
    public void CompactSizeSize_SmallValues()
    {
        Assert.Equal(1, CompactSizeSize(0));
        Assert.Equal(1, CompactSizeSize(1));
        Assert.Equal(1, CompactSizeSize(252));
    }

    [Fact]
    public void CompactSizeSize_MediumValues()
    {
        Assert.Equal(3, CompactSizeSize(253));
        Assert.Equal(3, CompactSizeSize(0xFFFF));
    }

    [Fact]
    public void CompactSizeSize_LargeValues()
    {
        Assert.Equal(5, CompactSizeSize(0x10000));
        Assert.Equal(5, CompactSizeSize(0xFFFFFF));
    }

    [Fact]
    public void CompactSizeSize_VeryLargeValues()
    {
        Assert.Equal(9, CompactSizeSize(0x1000000));
    }

    private static int CompactSizeSize(int count) => count switch
    {
        < 253 => 1,
        <= 0xFFFF => 3,
        <= 0xFFFFFF => 5,
        _ => 9
    };
}
