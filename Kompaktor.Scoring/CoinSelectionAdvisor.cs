namespace Kompaktor.Scoring;

public enum CoinSelectionStrategy { PrivacyFirst, FeeSaver, Consolidation }

public record CoinSelectionResult(
    IReadOnlyList<ScoredUtxo> Selected,
    long TotalAmountSat,
    string[] Warnings);

public class CoinSelectionAdvisor
{
    private readonly ScoringOptions _options;

    public CoinSelectionAdvisor(ScoringOptions options) => _options = options;

    public CoinSelectionResult SelectCoins(
        IReadOnlyList<ScoredUtxo> availableCoins,
        long targetAmountSat,
        CoinSelectionStrategy strategy = CoinSelectionStrategy.PrivacyFirst)
    {
        var warnings = new List<string>();

        var sorted = strategy switch
        {
            CoinSelectionStrategy.PrivacyFirst => availableCoins
                .OrderByDescending(c => c.Score.EffectiveScore)
                .ThenByDescending(c => c.Utxo.AmountSat)
                .ToList(),
            CoinSelectionStrategy.FeeSaver => availableCoins
                .OrderByDescending(c => c.Utxo.AmountSat)
                .ToList(),
            CoinSelectionStrategy.Consolidation => availableCoins
                .OrderBy(c => c.Utxo.AmountSat)
                .ToList(),
            _ => availableCoins.ToList()
        };

        var selected = new List<ScoredUtxo>();
        long total = 0;

        foreach (var coin in sorted)
        {
            selected.Add(coin);
            total += coin.Utxo.AmountSat;
            if (total >= targetAmountSat) break;
        }

        if (total < targetAmountSat)
            warnings.Add("Insufficient funds: available coins do not cover target amount");

        // Check for cluster mixing
        var hasLabeled = selected.Any(c => c.Labels.Length > 0);
        var hasUnlabeled = selected.Any(c => c.Labels.Length == 0 && c.Score.RawAnonSet > 1);
        if (hasLabeled && hasUnlabeled)
            warnings.Add("Warning: mixing labeled and unlabeled coins reduces cluster privacy");

        return new CoinSelectionResult(selected, total, warnings.ToArray());
    }

    public IReadOnlyList<ScoredUtxo> GetCoinjoinCandidates(
        IReadOnlyList<ScoredUtxo> allCoins,
        double? minEffectiveScore = null)
    {
        var threshold = minEffectiveScore ?? _options.MinEffectiveScoreForCoinjoin;
        return allCoins
            .Where(c => c.Score.EffectiveScore < threshold)
            .OrderBy(c => c.Score.EffectiveScore)
            .ToList();
    }
}
