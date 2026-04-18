using System.Collections.Concurrent;
using Kompaktor.Contracts;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;

namespace Kompaktor.Wallet;

/// <summary>
/// Persists failed round input sets to the wallet database so intersection attack
/// tracking survives service restarts. Without persistence, a coordinator running
/// an intersection attack can simply wait for the client to restart and re-disclose
/// all previously-leaked coin pairings.
///
/// Wraps the same union-find algorithm as RoundHistoryTracker but loads prior
/// round history from the DB on construction.
/// </summary>
public class PersistentRoundHistoryTracker : IRoundHistoryTracker
{
    private readonly WalletDbContext _db;
    private readonly string _walletId;
    private readonly int _maxConsecutiveFailures;
    private readonly ConcurrentBag<HashSet<OutPoint>> _failedRoundInputs = new();
    private int _consecutiveFailures;

    public int ConsecutiveFailures => _consecutiveFailures;

    public PersistentRoundHistoryTracker(WalletDbContext db, string walletId, int maxConsecutiveFailures = 3)
    {
        _db = db;
        _walletId = walletId;
        _maxConsecutiveFailures = maxConsecutiveFailures;
    }

    /// <summary>
    /// Loads previously recorded failed round inputs from the database.
    /// Call this once after construction before starting rounds.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var entries = await _db.FailedRoundInputs
            .Where(f => f.WalletId == _walletId)
            .ToListAsync(ct);

        var grouped = entries.GroupBy(e => e.RoundGroupId);
        foreach (var group in grouped)
        {
            var inputSet = group
                .Select(e => new OutPoint(uint256.Parse(e.TxId), e.OutputIndex))
                .ToHashSet();
            _failedRoundInputs.Add(inputSet);
        }
    }

    public void RecordFailedRound(IEnumerable<OutPoint> registeredInputs)
    {
        var inputs = registeredInputs.ToList();
        if (inputs.Count == 0) return;

        var inputSet = new HashSet<OutPoint>(inputs);
        _failedRoundInputs.Add(inputSet);
        Interlocked.Increment(ref _consecutiveFailures);

        // Persist to DB (fire-and-forget with error swallowing —
        // losing a single round's data is acceptable; crashing is not)
        var groupId = Guid.NewGuid().ToString();
        var entities = inputs.Select(op => new FailedRoundInputEntity
        {
            WalletId = _walletId,
            RoundGroupId = groupId,
            TxId = op.Hash.ToString(),
            OutputIndex = (int)op.N
        }).ToList();

        try
        {
            _db.FailedRoundInputs.AddRange(entities);
            _db.SaveChanges();
        }
        catch
        {
            // DB write failure is not fatal — in-memory tracking still works
        }
    }

    public void RecordSuccess()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
    }

    public bool ShouldBackOff()
    {
        return _consecutiveFailures >= _maxConsecutiveFailures;
    }

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
