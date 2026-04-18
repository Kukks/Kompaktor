using Kompaktor.Wallet;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Xunit;

namespace Kompaktor.Tests;

public class PersistentRoundHistoryTests : IDisposable
{
    private readonly WalletDbContext _db;
    private const string WalletId = "test-wallet";

    public PersistentRoundHistoryTests()
    {
        var options = new DbContextOptionsBuilder<WalletDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _db = new WalletDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    private static OutPoint Op(int id) =>
        new(uint256.Parse($"{id:D64}"), 0);

    private static Coin FakeCoin(OutPoint op) =>
        new(op, new TxOut(Money.Satoshis(100_000), Script.Empty));

    [Fact]
    public async Task RecordAndLoad_PreservesClusterMemory()
    {
        // Record a failed round
        var tracker1 = new PersistentRoundHistoryTracker(_db, WalletId);
        tracker1.RecordFailedRound(new[] { Op(1), Op(2) });

        // Create a new tracker and load from DB — simulates service restart
        var tracker2 = new PersistentRoundHistoryTracker(_db, WalletId);
        await tracker2.LoadAsync();

        // The loaded tracker should know about the cluster
        var excluded = tracker2.GetCoinsToExclude(new[] { FakeCoin(Op(1)), FakeCoin(Op(2)) });
        Assert.Single(excluded);
    }

    [Fact]
    public async Task MultipleRounds_AllPersistedAndLoaded()
    {
        var tracker1 = new PersistentRoundHistoryTracker(_db, WalletId);
        tracker1.RecordFailedRound(new[] { Op(1), Op(2) });
        tracker1.RecordFailedRound(new[] { Op(3), Op(4) });

        var tracker2 = new PersistentRoundHistoryTracker(_db, WalletId);
        await tracker2.LoadAsync();

        // Disjoint clusters: one from each
        var excluded = tracker2.GetCoinsToExclude(new[]
        {
            FakeCoin(Op(1)), FakeCoin(Op(2)),
            FakeCoin(Op(3)), FakeCoin(Op(4))
        });
        Assert.Equal(2, excluded.Count);
    }

    [Fact]
    public async Task TransitiveClosure_PreservedAcrossRestart()
    {
        var tracker1 = new PersistentRoundHistoryTracker(_db, WalletId);
        tracker1.RecordFailedRound(new[] { Op(1), Op(2) });
        tracker1.RecordFailedRound(new[] { Op(2), Op(3) });

        var tracker2 = new PersistentRoundHistoryTracker(_db, WalletId);
        await tracker2.LoadAsync();

        // 1-2-3 should be one cluster after load
        var excluded = tracker2.GetCoinsToExclude(new[]
        {
            FakeCoin(Op(1)), FakeCoin(Op(2)), FakeCoin(Op(3))
        });
        Assert.Equal(2, excluded.Count);
    }

    [Fact]
    public async Task DifferentWallets_Isolated()
    {
        var tracker1 = new PersistentRoundHistoryTracker(_db, WalletId);
        tracker1.RecordFailedRound(new[] { Op(1), Op(2) });

        // Load for a different wallet
        var tracker2 = new PersistentRoundHistoryTracker(_db, "other-wallet");
        await tracker2.LoadAsync();

        // Should have no history
        var excluded = tracker2.GetCoinsToExclude(new[] { FakeCoin(Op(1)), FakeCoin(Op(2)) });
        Assert.Empty(excluded);
    }

    [Fact]
    public async Task EmptyHistory_LoadSucceeds()
    {
        var tracker = new PersistentRoundHistoryTracker(_db, WalletId);
        await tracker.LoadAsync();
        Assert.False(tracker.ShouldBackOff());
        Assert.Equal(0, tracker.ConsecutiveFailures);
    }

    [Fact]
    public async Task ConsecutiveFailures_NotPersisted()
    {
        // Consecutive failure count is session-local, not persisted
        var tracker1 = new PersistentRoundHistoryTracker(_db, WalletId, maxConsecutiveFailures: 2);
        tracker1.RecordFailedRound(new[] { Op(1) });
        tracker1.RecordFailedRound(new[] { Op(2) });
        Assert.True(tracker1.ShouldBackOff());

        // After restart, consecutive count resets
        var tracker2 = new PersistentRoundHistoryTracker(_db, WalletId, maxConsecutiveFailures: 2);
        await tracker2.LoadAsync();
        Assert.False(tracker2.ShouldBackOff());
    }

    [Fact]
    public void DbEntitiesPersisted()
    {
        var tracker = new PersistentRoundHistoryTracker(_db, WalletId);
        tracker.RecordFailedRound(new[] { Op(1), Op(2), Op(3) });

        Assert.Equal(3, _db.FailedRoundInputs.Count());
        Assert.All(_db.FailedRoundInputs, e => Assert.Equal(WalletId, e.WalletId));

        // All in same group
        var groups = _db.FailedRoundInputs.Select(e => e.RoundGroupId).Distinct().ToList();
        Assert.Single(groups);
    }
}
