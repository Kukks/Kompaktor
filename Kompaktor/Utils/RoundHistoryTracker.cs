using System.Collections.Concurrent;
using Kompaktor.Contracts;
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
public class RoundHistoryTracker : IRoundHistoryTracker
{
    private readonly ConcurrentBag<HashSet<OutPoint>> _failedRoundInputs = new();
    private readonly int _maxConsecutiveFailures;
    private int _consecutiveFailures;

    public int ConsecutiveFailures => _consecutiveFailures;

    public RoundHistoryTracker(int maxConsecutiveFailures = 3)
    {
        _maxConsecutiveFailures = maxConsecutiveFailures;
    }

    public void RecordFailedRound(IEnumerable<OutPoint> registeredInputs)
    {
        _failedRoundInputs.Add(new HashSet<OutPoint>(registeredInputs));
        Interlocked.Increment(ref _consecutiveFailures);
    }

    public void RecordSuccess()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
    }

    public bool ShouldBackOff()
    {
        return _consecutiveFailures >= _maxConsecutiveFailures;
    }

    /// <summary>
    /// Given a proposed set of coins, returns the subset that should be excluded
    /// to avoid revealing new input pairings beyond what's already known.
    ///
    /// Strategy: build connected components from all previous failed rounds (coins
    /// that appeared together form edges). From each component, allow at most ONE
    /// coin in the new selection to prevent confirming cross-cluster links.
    /// </summary>
    public HashSet<OutPoint> GetCoinsToExclude(IEnumerable<Coin> proposedCoins)
    {
        var proposed = proposedCoins.Select(c => c.Outpoint).ToHashSet();
        if (_failedRoundInputs.IsEmpty)
            return new HashSet<OutPoint>();

        var components = BuildConnectedComponents();
        var toExclude = new HashSet<OutPoint>();

        foreach (var component in components)
        {
            var overlap = proposed.Where(component.Contains).ToList();
            if (overlap.Count > 1)
            {
                foreach (var outpoint in overlap.Skip(1))
                    toExclude.Add(outpoint);
            }
        }

        return toExclude;
    }

    private List<HashSet<OutPoint>> BuildConnectedComponents()
    {
        var parent = new Dictionary<OutPoint, OutPoint>();

        OutPoint Find(OutPoint x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Union(OutPoint a, OutPoint b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        foreach (var roundInputs in _failedRoundInputs)
        {
            OutPoint? first = null;
            foreach (var op in roundInputs)
            {
                parent.TryAdd(op, op);
                if (first is not null)
                    Union(first, op);
                first ??= op;
            }
        }

        var groups = new Dictionary<OutPoint, HashSet<OutPoint>>();
        foreach (var op in parent.Keys)
        {
            var root = Find(op);
            if (!groups.TryGetValue(root, out var set))
            {
                set = new HashSet<OutPoint>();
                groups[root] = set;
            }
            set.Add(op);
        }

        return groups.Values.ToList();
    }
}
