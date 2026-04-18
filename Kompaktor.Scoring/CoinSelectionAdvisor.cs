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

        // Check for cluster mixing (labeled + unlabeled = cluster leak)
        var hasLabeled = selected.Any(c => c.Labels.Length > 0);
        var hasUnlabeled = selected.Any(c => c.Labels.Length == 0 && c.Score.RawAnonSet > 1);
        if (hasLabeled && hasUnlabeled)
            warnings.Add("Warning: mixing labeled and unlabeled coins reduces cluster privacy");

        // Check for wide anonymity spread (merging high + low anon coins)
        if (selected.Count >= 2)
        {
            var maxScore = selected.Max(c => c.Score.EffectiveScore);
            var minScore = selected.Min(c => c.Score.EffectiveScore);
            if (maxScore > 1 && minScore > 0 && maxScore / minScore > 10)
                warnings.Add($"Warning: wide anonymity spread ({minScore:F1} to {maxScore:F1}) — merging degrades the higher-scored coin's privacy");
        }

        // Check for same-round coins (spending outputs from same coinjoin = linkability)
        var coinTxIds = selected
            .Where(c => c.Score.CoinJoinCount > 0)
            .Select(c => c.Utxo.TxId)
            .ToList();
        var duplicateTxIds = coinTxIds
            .GroupBy(t => t)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateTxIds.Count > 0)
            warnings.Add($"Warning: spending {duplicateTxIds.Count} pair(s) of outputs from the same coinjoin reduces anonymity set");

        // Check for address reuse risk (all unmixed coins = no privacy benefit)
        if (selected.All(c => c.Score.CoinJoinCount == 0) && selected.Count > 1)
            warnings.Add("Note: all selected coins are unmixed — consider mixing before spending for better privacy");

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
