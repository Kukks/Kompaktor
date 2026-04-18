using Microsoft.Extensions.Logging;
using NBitcoin;

namespace Kompaktor.Behaviors;

/// <summary>
/// Prioritizes coins with low anonymity scores for coinjoin participation.
/// Coins already above the threshold are deprioritized (moved to the end of candidates),
/// so the protocol naturally focuses on improving privacy where it's most needed.
/// </summary>
public class PrivacyAwareCoinSelectionTrait : KompaktorClientBaseBehaviorTrait
{
    private ILogger Logger => Client.Logger;
    private readonly Func<OutPoint, double?> _scoreLookup;
    private readonly double _minEffectiveScoreThreshold;

    /// <param name="scoreLookup">
    /// Returns the effective anonymity score for a coin, or null if unknown.
    /// Unknown coins are treated as low-score (needing privacy improvement).
    /// </param>
    /// <param name="minEffectiveScoreThreshold">
    /// Coins at or above this score are deprioritized for coinjoin.
    /// Default 5.0 means coins with effective score >= 5 are considered adequately mixed.
    /// </param>
    public PrivacyAwareCoinSelectionTrait(
        Func<OutPoint, double?> scoreLookup,
        double minEffectiveScoreThreshold = 5.0)
    {
        _scoreLookup = scoreLookup;
        _minEffectiveScoreThreshold = minEffectiveScoreThreshold;
    }

    public override void Start(KompaktorRoundClient client)
    {
        base.Start(client);
        // Hook before other traits so they see reordered candidates
        Client.StartCoinSelection += OnStartCoinSelection;
    }

    private Task OnStartCoinSelection(object sender)
    {
        var candidates = Client.CoinCandidates;
        if (candidates is not { Count: > 0 })
            return Task.CompletedTask;

        // Partition into needs-mixing and already-mixed
        var needsMixing = new List<Coin>();
        var alreadyMixed = new List<Coin>();

        foreach (var coin in candidates)
        {
            var score = _scoreLookup(coin.Outpoint);
            if (score is null || score.Value < _minEffectiveScoreThreshold)
                needsMixing.Add(coin);
            else
                alreadyMixed.Add(coin);
        }

        if (alreadyMixed.Count > 0)
        {
            Logger.LogInformation(
                "Privacy selection: {NeedsMixing} coins need mixing, {AlreadyMixed} already above threshold {Threshold}",
                needsMixing.Count, alreadyMixed.Count, _minEffectiveScoreThreshold);
        }

        // Reorder: needs-mixing first, already-mixed last
        // The shuffle in KompaktorRoundClient.ShuffleCoinCandidates runs after StartCoinSelection,
        // but other traits (ConsolidationBehaviorTrait, InteractivePaymentSenderBehaviorTrait)
        // pick from RemainingCoinCandidates which respects AllocatedSelectedCoins ordering.
        // By putting low-score coins first, the consolidation trait naturally picks them.
        candidates.Clear();
        candidates.AddRange(needsMixing);
        candidates.AddRange(alreadyMixed);

        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        Client.StartCoinSelection -= OnStartCoinSelection;
        base.Dispose();
    }
}
