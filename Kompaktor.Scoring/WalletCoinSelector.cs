using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;

namespace Kompaktor.Scoring;

/// <summary>
/// Bridges the wallet UTXO set with the anonymity scorer and coin selection advisor.
/// Provides privacy-aware coin selection for both spending and coinjoin participation.
/// </summary>
public class WalletCoinSelector
{
    private readonly WalletDbContext _db;
    private readonly AnonymityScorer _scorer;
    private readonly CoinSelectionAdvisor _advisor;

    public WalletCoinSelector(WalletDbContext db, ScoringOptions? options = null)
    {
        _db = db;
        options ??= new ScoringOptions();
        _scorer = new AnonymityScorer(options);
        _advisor = new CoinSelectionAdvisor(options);
    }

    /// <summary>
    /// Loads and scores all unspent confirmed UTXOs for a wallet.
    /// </summary>
    public async Task<IReadOnlyList<ScoredUtxo>> GetScoredUtxosAsync(
        string walletId, CancellationToken ct = default)
    {
        var utxos = await _db.Utxos
            .Include(u => u.Address)
            .ThenInclude(a => a.Account)
            .ThenInclude(acc => acc.Wallet)
            .Include(u => u.Address)
            .ThenInclude(a => a.Utxos)
            .Where(u => u.SpentByTxId == null && u.ConfirmedHeight != null)
            .Where(u => u.Address.Account.Wallet.Id == walletId)
            .ToListAsync(ct);

        var utxoIds = utxos.Select(u => u.Id).ToHashSet();
        var addressIds = utxos.Select(u => u.AddressId).ToHashSet();

        // Load all participations for these UTXOs (including ancestor chains)
        var participations = await _db.Set<CoinJoinParticipationEntity>()
            .Include(p => p.CoinJoinRecord)
            .Where(p => utxoIds.Contains(p.UtxoId))
            .ToListAsync(ct);

        // Load labels for these UTXOs and their addresses
        var utxoIdStrings = utxoIds.Select(id => id.ToString()).ToHashSet();
        var addressIdStrings = addressIds.Select(id => id.ToString()).ToHashSet();
        var labels = await _db.Labels
            .Where(l =>
                (l.EntityType == "Utxo" && utxoIdStrings.Contains(l.EntityId)) ||
                (l.EntityType == "Address" && addressIdStrings.Contains(l.EntityId)))
            .ToListAsync(ct);

        var result = new List<ScoredUtxo>(utxos.Count);
        foreach (var utxo in utxos)
        {
            var score = _scorer.Score(utxo, participations, labels);
            var utxoLabels = labels
                .Where(l => (l.EntityType == "Utxo" && l.EntityId == utxo.Id.ToString()) ||
                            (l.EntityType == "Address" && l.EntityId == utxo.AddressId.ToString()))
                .Select(l => l.Text)
                .ToArray();
            result.Add(new ScoredUtxo(utxo, score, utxoLabels));
        }

        return result;
    }

    /// <summary>
    /// Selects coins for a target amount using privacy-aware scoring.
    /// </summary>
    public async Task<CoinSelectionResult> SelectForSpendAsync(
        string walletId,
        long targetAmountSat,
        CoinSelectionStrategy strategy = CoinSelectionStrategy.PrivacyFirst,
        CancellationToken ct = default)
    {
        var scored = await GetScoredUtxosAsync(walletId, ct);
        return _advisor.SelectCoins(scored, targetAmountSat, strategy);
    }

    /// <summary>
    /// Returns UTXOs that would benefit from additional coinjoin mixing.
    /// These are coins with low anonymity scores that should be prioritized.
    /// </summary>
    public async Task<IReadOnlyList<ScoredUtxo>> GetCoinjoinCandidatesAsync(
        string walletId,
        double? minEffectiveScore = null,
        CancellationToken ct = default)
    {
        var scored = await GetScoredUtxosAsync(walletId, ct);
        return _advisor.GetCoinjoinCandidates(scored, minEffectiveScore);
    }

    /// <summary>
    /// Returns a privacy summary for the wallet's UTXO set.
    /// Useful for dashboard display and monitoring.
    /// </summary>
    public async Task<WalletPrivacySummary> GetPrivacySummaryAsync(
        string walletId, CancellationToken ct = default)
    {
        var scored = await GetScoredUtxosAsync(walletId, ct);

        if (scored.Count == 0)
            return new WalletPrivacySummary(0, 0, 0, 0, 0, 0, 0);

        var totalSat = scored.Sum(s => s.Utxo.AmountSat);
        var avgScore = scored.Average(s => s.Score.EffectiveScore);
        var minScore = scored.Min(s => s.Score.EffectiveScore);
        var maxScore = scored.Max(s => s.Score.EffectiveScore);
        var mixedCount = scored.Count(s => s.Score.CoinJoinCount > 0);
        var needsMixing = scored.Count(s => s.Score.EffectiveScore < 5.0);

        return new WalletPrivacySummary(
            scored.Count, totalSat, avgScore, minScore, maxScore, mixedCount, needsMixing);
    }
}

/// <summary>
/// Privacy health summary for a wallet's UTXO set.
/// </summary>
public readonly record struct WalletPrivacySummary(
    int TotalUtxos,
    long TotalAmountSat,
    double AverageEffectiveScore,
    double MinEffectiveScore,
    double MaxEffectiveScore,
    int MixedUtxoCount,
    int NeedsMixingCount);
