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
    /// Loads and scores all unspent UTXOs for a wallet (confirmed, and optionally unconfirmed coinjoin outputs).
    /// </summary>
    public async Task<IReadOnlyList<ScoredUtxo>> GetScoredUtxosAsync(
        string walletId, bool includeFrozen = false, bool includeUnconfirmedCoinjoinOutputs = false, CancellationToken ct = default)
    {
        var query = _db.Utxos
            .Include(u => u.Address)
            .ThenInclude(a => a.Account)
            .ThenInclude(acc => acc.Wallet)
            .Include(u => u.Address)
            .ThenInclude(a => a.Utxos)
            .Where(u => u.SpentByTxId == null)
            .Where(u => u.Address.Account.Wallet.Id == walletId);

        // Include confirmed UTXOs, and optionally unconfirmed coinjoin outputs for fast re-mixing
        if (includeUnconfirmedCoinjoinOutputs)
            query = query.Where(u => u.ConfirmedHeight != null || u.IsCoinJoinOutput);
        else
            query = query.Where(u => u.ConfirmedHeight != null);

        if (!includeFrozen)
            query = query.Where(u => !u.IsFrozen);

        var utxos = await query
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
        var scored = await GetScoredUtxosAsync(walletId, ct: ct);
        return _advisor.SelectCoins(scored, targetAmountSat, strategy);
    }

    /// <summary>
    /// Uses exactly the specified outpoints as the coin selection — bypasses
    /// the advisor entirely. Frozen UTXOs are allowed here because the user
    /// has made an explicit, per-coin decision to spend them. Throws if any
    /// outpoint doesn't belong to the wallet or isn't spendable.
    /// </summary>
    public async Task<CoinSelectionResult> SelectSpecificUtxosAsync(
        string walletId,
        IReadOnlyList<(string TxId, int OutputIndex)> outpoints,
        CancellationToken ct = default)
    {
        if (outpoints.Count == 0)
            throw new ArgumentException("At least one outpoint required", nameof(outpoints));

        // includeFrozen:true — manual selection is an explicit override
        var scored = await GetScoredUtxosAsync(walletId, includeFrozen: true, ct: ct);
        var byOutpoint = scored.ToDictionary(s => (s.Utxo.TxId, s.Utxo.OutputIndex));

        var selected = new List<ScoredUtxo>(outpoints.Count);
        foreach (var op in outpoints)
        {
            if (!byOutpoint.TryGetValue((op.TxId, op.OutputIndex), out var match))
                throw new InvalidOperationException(
                    $"Outpoint {op.TxId}:{op.OutputIndex} is not a spendable UTXO for this wallet");
            selected.Add(match);
        }

        var warnings = new List<string>();
        var frozenCount = selected.Count(s => s.Utxo.IsFrozen);
        if (frozenCount > 0)
            warnings.Add($"{frozenCount} of {selected.Count} selected UTXO(s) are frozen — spending anyway due to explicit selection");

        return new CoinSelectionResult(
            selected,
            selected.Sum(s => s.Utxo.AmountSat),
            warnings.ToArray());
    }

    /// <summary>
    /// Returns UTXOs that would benefit from additional coinjoin mixing.
    /// These are coins with low anonymity scores that should be prioritized.
    /// </summary>
    public async Task<IReadOnlyList<ScoredUtxo>> GetCoinjoinCandidatesAsync(
        string walletId,
        double? minEffectiveScore = null,
        bool includeUnconfirmedCoinjoinOutputs = false,
        CancellationToken ct = default)
    {
        var scored = await GetScoredUtxosAsync(walletId, includeUnconfirmedCoinjoinOutputs: includeUnconfirmedCoinjoinOutputs, ct: ct);
        return _advisor.GetCoinjoinCandidates(scored, minEffectiveScore);
    }

    /// <summary>
    /// Returns a privacy summary for the wallet's UTXO set.
    /// Useful for dashboard display and monitoring.
    /// </summary>
    public async Task<WalletPrivacySummary> GetPrivacySummaryAsync(
        string walletId, CancellationToken ct = default)
    {
        var scored = await GetScoredUtxosAsync(walletId, ct: ct);

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
