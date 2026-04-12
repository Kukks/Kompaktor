using System.Collections.Concurrent;
using NBitcoin;

namespace Kompaktor.Utils;

/// <summary>
/// Tracks input sets from failed rounds to detect and prevent intersection attacks.
/// A malicious coordinator can deliberately fail rounds to learn which inputs belong
/// to the same wallet by observing which inputs are repeatedly co-registered.
/// This tracker limits the information leaked across rounds by:
/// 1. Recording which inputs were registered together in failed rounds
/// 2. Evaluating whether a proposed coin selection would reveal new pairings
/// 3. Suggesting which coins to exclude to limit cluster disclosure
/// </summary>
public class RoundHistoryTracker
{
    private readonly ConcurrentBag<HashSet<OutPoint>> _failedRoundInputs = new();
    private readonly int _maxConsecutiveFailures;

    /// <summary>Number of consecutive round failures tracked.</summary>
    public int ConsecutiveFailures => _failedRoundInputs.Count;

    public RoundHistoryTracker(int maxConsecutiveFailures = 3)
    {
        _maxConsecutiveFailures = maxConsecutiveFailures;
    }

    /// <summary>
    /// Records the set of inputs that were registered in a failed round.
    /// </summary>
    public void RecordFailedRound(IEnumerable<OutPoint> registeredInputs)
    {
        _failedRoundInputs.Add(new HashSet<OutPoint>(registeredInputs));
    }

    /// <summary>
    /// Resets the failure history (call after a successful round).
    /// </summary>
    public void RecordSuccess()
    {
        _failedRoundInputs.Clear();
    }

    /// <summary>
    /// Returns true if too many consecutive rounds have failed,
    /// suggesting the coordinator may be deliberately failing rounds to harvest clusters.
    /// The client should back off or switch coordinators.
    /// </summary>
    public bool ShouldBackOff()
    {
        return _failedRoundInputs.Count >= _maxConsecutiveFailures;
    }

    /// <summary>
    /// Given a proposed set of coins, returns the subset that should be excluded
    /// to avoid revealing new input pairings beyond what's already known.
    ///
    /// Strategy: if a coin appeared in a previous failed round, at most ONE such coin
    /// should appear in the new selection (to avoid confirming that the others also
    /// belong to the same wallet).
    /// </summary>
    public HashSet<OutPoint> GetCoinsToExclude(IEnumerable<Coin> proposedCoins)
    {
        var proposed = proposedCoins.Select(c => c.Outpoint).ToHashSet();
        var toExclude = new HashSet<OutPoint>();

        foreach (var previousSet in _failedRoundInputs)
        {
            // How many of the proposed coins were in this previous failed round?
            var overlap = proposed.Where(o => previousSet.Contains(o)).ToList();

            // If more than 1 coin from a previous round appears in the new selection,
            // exclude all but one to avoid confirming they're in the same wallet
            if (overlap.Count > 1)
            {
                // Keep the first one, exclude the rest
                foreach (var outpoint in overlap.Skip(1))
                    toExclude.Add(outpoint);
            }
        }

        return toExclude;
    }
}
