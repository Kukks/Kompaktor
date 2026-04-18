using Kompaktor.Scoring;
using Kompaktor.Wallet.Data;
using Xunit;

namespace Kompaktor.Tests;

public class CoinSelectionAdvisorTests
{
    [Fact]
    public void PrivacyFirst_PrefersHighAnonCoins()
    {
        var coins = new[]
        {
            MakeScoredUtxo(1, 100_000, rawAnon: 50, effective: 50.0),
            MakeScoredUtxo(2, 100_000, rawAnon: 5, effective: 5.0),
            MakeScoredUtxo(3, 100_000, rawAnon: 1, effective: 1.0),
        };
        var advisor = new CoinSelectionAdvisor(new ScoringOptions());

        var result = advisor.SelectCoins(coins, 150_000, CoinSelectionStrategy.PrivacyFirst);

        Assert.Equal(2, result.Selected.Count);
        // Should pick the two highest-anon coins
        Assert.Contains(result.Selected, s => s.Utxo.Id == 1);
        Assert.Contains(result.Selected, s => s.Utxo.Id == 2);
    }

    [Fact]
    public void FeeSaver_PrefersFewerLargerInputs()
    {
        var coins = new[]
        {
            MakeScoredUtxo(1, 200_000, rawAnon: 1, effective: 1.0),
            MakeScoredUtxo(2, 50_000, rawAnon: 50, effective: 50.0),
            MakeScoredUtxo(3, 50_000, rawAnon: 50, effective: 50.0),
            MakeScoredUtxo(4, 50_000, rawAnon: 50, effective: 50.0),
        };
        var advisor = new CoinSelectionAdvisor(new ScoringOptions());

        var result = advisor.SelectCoins(coins, 150_000, CoinSelectionStrategy.FeeSaver);

        Assert.Single(result.Selected);
        Assert.Equal(1, result.Selected[0].Utxo.Id);
    }

    [Fact]
    public void MixingClusters_ProducesWarning()
    {
        var coins = new[]
        {
            MakeScoredUtxo(1, 100_000, rawAnon: 1, effective: 1.0, labels: ["Exchange:Coinbase"]),
            MakeScoredUtxo(2, 100_000, rawAnon: 50, effective: 50.0), // unlabeled, coinjoined
        };
        var advisor = new CoinSelectionAdvisor(new ScoringOptions());

        var result = advisor.SelectCoins(coins, 150_000, CoinSelectionStrategy.PrivacyFirst);

        Assert.Contains(result.Warnings, w => w.Contains("cluster", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InsufficientFunds_ReturnsWarning()
    {
        var coins = new[]
        {
            MakeScoredUtxo(1, 50_000, rawAnon: 10, effective: 10.0),
        };
        var advisor = new CoinSelectionAdvisor(new ScoringOptions());

        var result = advisor.SelectCoins(coins, 100_000, CoinSelectionStrategy.PrivacyFirst);

        Assert.Single(result.Selected);
        Assert.Contains(result.Warnings, w => w.Contains("insufficient", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetCoinjoinCandidates_FiltersLowScoreCoins()
    {
        var coins = new[]
        {
            MakeScoredUtxo(1, 100_000, rawAnon: 1, effective: 1.0),
            MakeScoredUtxo(2, 100_000, rawAnon: 50, effective: 50.0),
            MakeScoredUtxo(3, 100_000, rawAnon: 3, effective: 3.0),
        };
        var advisor = new CoinSelectionAdvisor(new ScoringOptions { MinEffectiveScoreForCoinjoin = 5.0 });

        var candidates = advisor.GetCoinjoinCandidates(coins);

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, c => c.Utxo.Id == 1);
        Assert.Contains(candidates, c => c.Utxo.Id == 3);
        Assert.DoesNotContain(candidates, c => c.Utxo.Id == 2);
    }

    [Fact]
    public void GetCoinjoinCandidates_OrderedByScore()
    {
        var coins = new[]
        {
            MakeScoredUtxo(1, 100_000, rawAnon: 3, effective: 3.0),
            MakeScoredUtxo(2, 100_000, rawAnon: 1, effective: 1.0),
        };
        var advisor = new CoinSelectionAdvisor(new ScoringOptions { MinEffectiveScoreForCoinjoin = 5.0 });

        var candidates = advisor.GetCoinjoinCandidates(coins);

        Assert.Equal(2, candidates[0].Utxo.Id); // Score 1.0 first (lowest)
        Assert.Equal(1, candidates[1].Utxo.Id); // Score 3.0 second
    }

    [Fact]
    public void Consolidation_StartsWithSmallest()
    {
        var coins = new[]
        {
            MakeScoredUtxo(1, 300_000, rawAnon: 10, effective: 10.0),
            MakeScoredUtxo(2, 100_000, rawAnon: 10, effective: 10.0),
            MakeScoredUtxo(3, 200_000, rawAnon: 10, effective: 10.0),
        };
        var advisor = new CoinSelectionAdvisor(new ScoringOptions());

        var result = advisor.SelectCoins(coins, 250_000, CoinSelectionStrategy.Consolidation);

        // Consolidation sorts ascending — picks 100k, 200k (= 300k >= 250k target)
        Assert.Equal(2, result.Selected.Count);
        Assert.Equal(2, result.Selected[0].Utxo.Id); // 100k
        Assert.Equal(3, result.Selected[1].Utxo.Id); // 200k
    }

    [Fact]
    public void WideAnonSpread_ProducesWarning()
    {
        var coins = new[]
        {
            MakeScoredUtxo(1, 100_000, rawAnon: 50, effective: 50.0),
            MakeScoredUtxo(2, 100_000, rawAnon: 2, effective: 2.0),
        };
        var advisor = new CoinSelectionAdvisor(new ScoringOptions());

        var result = advisor.SelectCoins(coins, 150_000, CoinSelectionStrategy.PrivacyFirst);

        Assert.Contains(result.Warnings, w => w.Contains("anonymity spread", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SameRoundOutputs_ProducesWarning()
    {
        // Two outputs from the same coinjoin tx
        var coins = new[]
        {
            MakeScoredUtxo(1, 100_000, rawAnon: 10, effective: 10.0, coinJoinCount: 1, txId: "cjtx1"),
            MakeScoredUtxo(2, 100_000, rawAnon: 10, effective: 10.0, coinJoinCount: 1, txId: "cjtx1"),
        };
        var advisor = new CoinSelectionAdvisor(new ScoringOptions());

        var result = advisor.SelectCoins(coins, 150_000, CoinSelectionStrategy.PrivacyFirst);

        Assert.Contains(result.Warnings, w => w.Contains("same coinjoin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AllUnmixedCoins_ProducesNote()
    {
        var coins = new[]
        {
            MakeScoredUtxo(1, 100_000, rawAnon: 1, effective: 1.0),
            MakeScoredUtxo(2, 100_000, rawAnon: 1, effective: 1.0),
        };
        var advisor = new CoinSelectionAdvisor(new ScoringOptions());

        var result = advisor.SelectCoins(coins, 150_000, CoinSelectionStrategy.PrivacyFirst);

        Assert.Contains(result.Warnings, w => w.Contains("unmixed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SimilarAnonScores_NoSpreadWarning()
    {
        var coins = new[]
        {
            MakeScoredUtxo(1, 100_000, rawAnon: 8, effective: 8.0),
            MakeScoredUtxo(2, 100_000, rawAnon: 10, effective: 10.0),
        };
        var advisor = new CoinSelectionAdvisor(new ScoringOptions());

        var result = advisor.SelectCoins(coins, 150_000, CoinSelectionStrategy.PrivacyFirst);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("anonymity spread", StringComparison.OrdinalIgnoreCase));
    }

    private static ScoredUtxo MakeScoredUtxo(
        int id, long amountSat, int rawAnon, double effective,
        string[]? labels = null, int coinJoinCount = -1, string? txId = null)
    {
        var addr = new AddressEntity { Id = id, ScriptPubKey = [(byte)id] };
        var utxo = new UtxoEntity
        {
            Id = id, TxId = txId ?? $"tx{id}", OutputIndex = 0, AmountSat = amountSat,
            ScriptPubKey = [(byte)id], AddressId = id, Address = addr
        };
        var cjCount = coinJoinCount >= 0 ? coinJoinCount : (rawAnon > 1 ? 1 : 0);
        var score = new AnonymityScore(rawAnon, effective, cjCount, ConfidenceLevel.High);
        return new ScoredUtxo(utxo, score, labels ?? []);
    }
}
