using NBitcoin;
using Xunit;

namespace Kompaktor.Tests;

public class CoinSelectionHardeningTests
{
    private static Coin MakeCoin(int id) =>
        new(new OutPoint(uint256.Parse($"{id:D64}"), 0),
            new TxOut(Money.Satoshis(100_000), new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit)));

    private static void FisherYatesShuffle<T>(List<T> list, Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    [Fact]
    public void Shuffle_PreservesAllElements()
    {
        var coins = Enumerable.Range(0, 20).Select(MakeCoin).ToList();
        var original = coins.Select(c => c.Outpoint).ToHashSet();
        FisherYatesShuffle(coins, new Random(42));

        Assert.Equal(20, coins.Count);
        Assert.True(coins.All(c => original.Contains(c.Outpoint)));
    }

    [Fact]
    public void Shuffle_NoDuplicates()
    {
        var coins = Enumerable.Range(0, 30).Select(MakeCoin).ToList();
        FisherYatesShuffle(coins, new Random(99));
        Assert.Equal(coins.Count, coins.Select(c => c.Outpoint).Distinct().Count());
    }

    [Fact]
    public void Shuffle_EmptyList_NoThrow()
    {
        var coins = new List<Coin>();
        var ex = Record.Exception(() => FisherYatesShuffle(coins, new Random(1)));
        Assert.Null(ex);
        Assert.Empty(coins);
    }

    [Fact]
    public void Shuffle_SingleElement_Unchanged()
    {
        var coin = MakeCoin(1);
        var coins = new List<Coin> { coin };
        FisherYatesShuffle(coins, new Random(1));
        Assert.Single(coins);
        Assert.Same(coin, coins[0]);
    }

    [Fact]
    public void Shuffle_TwoElements_CanSwap()
    {
        var a = MakeCoin(1);
        var b = MakeCoin(2);
        var swapped = false;

        for (int seed = 0; seed < 100; seed++)
        {
            var coins = new List<Coin> { a, b };
            FisherYatesShuffle(coins, new Random(seed));
            if (coins[0].Outpoint == b.Outpoint)
            {
                swapped = true;
                break;
            }
        }
        Assert.True(swapped, "Two-element list never swapped in 100 trials");
    }

    [Fact]
    public void Shuffle_DifferentSeeds_DifferentOrderings()
    {
        var coins = Enumerable.Range(0, 10).Select(MakeCoin).ToList();
        var refOrder = coins.Select(c => c.Outpoint).ToList();
        var sawDifferent = false;

        for (int seed = 0; seed < 100; seed++)
        {
            var shuffled = coins.ToList();
            FisherYatesShuffle(shuffled, new Random(seed));
            if (!refOrder.SequenceEqual(shuffled.Select(c => c.Outpoint)))
            {
                sawDifferent = true;
                break;
            }
        }
        Assert.True(sawDifferent, "100 shuffles all same — not random");
    }

    [Fact]
    public void Shuffle_SameSeed_SameResult()
    {
        var coins1 = Enumerable.Range(0, 20).Select(MakeCoin).ToList();
        var coins2 = coins1.ToList();
        FisherYatesShuffle(coins1, new Random(42));
        FisherYatesShuffle(coins2, new Random(42));
        Assert.Equal(
            coins1.Select(c => c.Outpoint),
            coins2.Select(c => c.Outpoint));
    }

    [Fact]
    public void Shuffle_UniformDistribution_ChiSquared()
    {
        const int n = 4;
        const int trials = 120_000;
        var counts = new Dictionary<string, int>();

        for (int trial = 0; trial < trials; trial++)
        {
            var items = Enumerable.Range(0, n).ToList();
            FisherYatesShuffle(items, new Random(trial));
            var key = string.Join(",", items);
            counts.TryGetValue(key, out var c);
            counts[key] = c + 1;
        }

        Assert.Equal(24, counts.Count);

        double expected = (double)trials / 24;
        double chiSq = counts.Values.Sum(o => Math.Pow(o - expected, 2) / expected);
        Assert.True(chiSq < 60, $"Chi-squared={chiSq:F1} — shuffle is biased");
    }

    [Fact]
    public void Shuffle_EachPositionUnbiased()
    {
        const int n = 6;
        const int trials = 60_000;
        var positionCounts = new int[n, n];

        for (int trial = 0; trial < trials; trial++)
        {
            var items = Enumerable.Range(0, n).ToList();
            FisherYatesShuffle(items, new Random(trial));
            for (int pos = 0; pos < n; pos++)
                positionCounts[pos, items[pos]]++;
        }

        double expected = (double)trials / n;
        for (int pos = 0; pos < n; pos++)
            for (int val = 0; val < n; val++)
                Assert.InRange(positionCounts[pos, val], expected * 0.9, expected * 1.1);
    }

    [Fact]
    public void Shuffle_FirstPositionVaries()
    {
        var firstElements = new HashSet<int>();
        for (int seed = 0; seed < 200; seed++)
        {
            var items = Enumerable.Range(0, 10).ToList();
            FisherYatesShuffle(items, new Random(seed));
            firstElements.Add(items[0]);
        }
        Assert.True(firstElements.Count >= 5, $"Only {firstElements.Count} distinct first elements");
    }

    [Fact]
    public void Shuffle_PreventsFirstCoinPrediction()
    {
        var walletCoins = Enumerable.Range(0, 5).Select(MakeCoin).ToList();
        var firstPicked = new HashSet<OutPoint>();

        for (int round = 0; round < 50; round++)
        {
            var candidates = walletCoins.ToList();
            FisherYatesShuffle(candidates, new Random(round + Environment.TickCount));
            firstPicked.Add(candidates[0].Outpoint);
        }

        Assert.True(firstPicked.Count >= 2,
            "Same coin picked first in all 50 rounds — shuffle not mitigating ordering attacks");
    }
}
