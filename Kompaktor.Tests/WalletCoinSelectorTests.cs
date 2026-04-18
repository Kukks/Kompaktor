using Kompaktor.Scoring;
using Kompaktor.Wallet;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Xunit;

namespace Kompaktor.Tests;

public class WalletCoinSelectorTests : IDisposable
{
    private readonly WalletDbContext _db;

    public WalletCoinSelectorTests()
    {
        var options = new DbContextOptionsBuilder<WalletDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _db = new WalletDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    private (WalletEntity wallet, AddressEntity address) SeedWalletWithAddress()
    {
        var wallet = new WalletEntity { Name = "ScoringTest" };
        var account = new AccountEntity { Purpose = 84, AccountIndex = 0, Wallet = wallet };
        var address = new AddressEntity { KeyPath = "0/0", ScriptPubKey = [0xAA], Account = account };
        _db.Wallets.Add(wallet);
        _db.Addresses.Add(address);
        _db.SaveChanges();
        return (wallet, address);
    }

    [Fact]
    public async Task GetScoredUtxos_VirginCoins_HaveScoreOfOne()
    {
        var (wallet, address) = SeedWalletWithAddress();

        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "tx1", OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100_000, ScriptPubKey = [0xAA], ConfirmedHeight = 100
        });
        _db.SaveChanges();

        var selector = new WalletCoinSelector(_db);
        var scored = await selector.GetScoredUtxosAsync(wallet.Id);

        Assert.Single(scored);
        Assert.Equal(1, scored[0].Score.RawAnonSet);
        Assert.Equal(1.0, scored[0].Score.EffectiveScore);
    }

    [Fact]
    public async Task GetScoredUtxos_CoinjoinedCoin_HigherScore()
    {
        var (wallet, address) = SeedWalletWithAddress();

        var utxo = new UtxoEntity
        {
            TxId = "tx1", OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100_000, ScriptPubKey = [0xAA], ConfirmedHeight = 100
        };
        _db.Utxos.Add(utxo);
        _db.SaveChanges();

        // Record a coinjoin round where this UTXO was an output
        var tx = new TransactionEntity { Id = "cjtx1", RawHex = "0200..." };
        _db.Transactions.Add(tx);
        var record = new CoinJoinRecordEntity
        {
            TransactionId = "cjtx1", RoundId = "round-1", Status = "Completed",
            ParticipantCount = 10, OurInputCount = 1, TotalInputCount = 12,
            OurOutputCount = 1, TotalOutputCount = 15,
            OutputValuesSat = Enumerable.Repeat(100_000L, 15).ToArray()
        };
        record.Participations.Add(new CoinJoinParticipationEntity
        {
            UtxoId = utxo.Id, Role = "Output"
        });
        _db.CoinJoinRecords.Add(record);
        _db.SaveChanges();

        var selector = new WalletCoinSelector(_db);
        var scored = await selector.GetScoredUtxosAsync(wallet.Id);

        Assert.Single(scored);
        Assert.Equal(10, scored[0].Score.RawAnonSet);
        Assert.True(scored[0].Score.EffectiveScore >= 10.0);
    }

    [Fact]
    public async Task SelectForSpend_PrivacyFirst_PrefersHighAnon()
    {
        var (wallet, address) = SeedWalletWithAddress();

        // Virgin coin
        var utxo1 = new UtxoEntity
        {
            TxId = "tx1", OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100_000, ScriptPubKey = [0xAA], ConfirmedHeight = 100
        };
        // Coinjoined coin
        var utxo2 = new UtxoEntity
        {
            TxId = "tx2", OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100_000, ScriptPubKey = [0xAA], ConfirmedHeight = 101
        };
        _db.Utxos.AddRange(utxo1, utxo2);
        _db.SaveChanges();

        var tx = new TransactionEntity { Id = "cjtx1", RawHex = "0200..." };
        _db.Transactions.Add(tx);
        var record = new CoinJoinRecordEntity
        {
            TransactionId = "cjtx1", RoundId = "round-1", Status = "Completed",
            ParticipantCount = 10, OurInputCount = 1, TotalInputCount = 12,
            OurOutputCount = 1, TotalOutputCount = 15,
            OutputValuesSat = Enumerable.Repeat(100_000L, 15).ToArray()
        };
        record.Participations.Add(new CoinJoinParticipationEntity
        {
            UtxoId = utxo2.Id, Role = "Output"
        });
        _db.CoinJoinRecords.Add(record);
        _db.SaveChanges();

        var selector = new WalletCoinSelector(_db);
        var result = await selector.SelectForSpendAsync(wallet.Id, 80_000);

        // Should prefer the coinjoined coin
        Assert.Single(result.Selected);
        Assert.Equal(utxo2.Id, result.Selected[0].Utxo.Id);
    }

    [Fact]
    public async Task GetCoinjoinCandidates_ReturnsLowScoreCoins()
    {
        var (wallet, address) = SeedWalletWithAddress();

        // Virgin coin (score 1.0 — below threshold)
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "tx1", OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100_000, ScriptPubKey = [0xAA], ConfirmedHeight = 100
        });
        _db.SaveChanges();

        var selector = new WalletCoinSelector(_db);
        var candidates = await selector.GetCoinjoinCandidatesAsync(wallet.Id);

        Assert.Single(candidates);
        Assert.True(candidates[0].Score.EffectiveScore < 5.0);
    }

    [Fact]
    public async Task GetPrivacySummary_ReturnsCorrectStats()
    {
        var (wallet, address) = SeedWalletWithAddress();

        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "tx1", OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100_000, ScriptPubKey = [0xAA], ConfirmedHeight = 100
        });
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "tx2", OutputIndex = 0, AddressId = address.Id,
            AmountSat = 200_000, ScriptPubKey = [0xAA], ConfirmedHeight = 101
        });
        _db.SaveChanges();

        var selector = new WalletCoinSelector(_db);
        var summary = await selector.GetPrivacySummaryAsync(wallet.Id);

        Assert.Equal(2, summary.TotalUtxos);
        Assert.Equal(300_000, summary.TotalAmountSat);
        Assert.Equal(0, summary.MixedUtxoCount);
        Assert.Equal(2, summary.NeedsMixingCount);
    }

    [Fact]
    public async Task GetPrivacySummary_EmptyWallet_ReturnsZeros()
    {
        var (wallet, _) = SeedWalletWithAddress();

        var selector = new WalletCoinSelector(_db);
        var summary = await selector.GetPrivacySummaryAsync(wallet.Id);

        Assert.Equal(0, summary.TotalUtxos);
        Assert.Equal(0, summary.TotalAmountSat);
    }

    [Fact]
    public async Task ScoringWalletAdapter_ReturnsOnlyLowScoreCoins()
    {
        var (wallet, address) = SeedWalletWithAddress();

        // Add two confirmed UTXOs — both virgin (score 1.0, below default threshold 5.0)
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "aa11000000000000000000000000000000000000000000000000000000000000",
            OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100_000, ScriptPubKey = [0xAA], ConfirmedHeight = 100
        });
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "bb22000000000000000000000000000000000000000000000000000000000000",
            OutputIndex = 0, AddressId = address.Id,
            AmountSat = 200_000, ScriptPubKey = [0xAA], ConfirmedHeight = 101
        });
        _db.SaveChanges();

        var hdWallet = await KompaktorHdWallet.CreateAsync(_db, Network.RegTest, "Adapter", "pass");
        var selector = new WalletCoinSelector(_db);
        var adapter = new ScoringWalletAdapter(hdWallet, selector, wallet.Id);

        var coins = await adapter.GetCoins();

        // Both coins are virgin (score 1.0 < 5.0 threshold) so both should be returned
        Assert.Equal(2, coins.Length);
    }

    [Fact]
    public async Task ScoringWalletAdapter_ReturnsEmpty_WhenAllCoinsWellMixed()
    {
        var (wallet, address) = SeedWalletWithAddress();

        var utxo = new UtxoEntity
        {
            TxId = "cc33000000000000000000000000000000000000000000000000000000000000",
            OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100_000, ScriptPubKey = [0xAA], ConfirmedHeight = 100
        };
        _db.Utxos.Add(utxo);
        _db.SaveChanges();

        // Fake a coinjoin with high participant count to push score above threshold
        var tx = new TransactionEntity { Id = "cjtx99", RawHex = "0200..." };
        _db.Transactions.Add(tx);
        var record = new CoinJoinRecordEntity
        {
            TransactionId = "cjtx99", RoundId = "round-99", Status = "Completed",
            ParticipantCount = 50, OurInputCount = 1, TotalInputCount = 60,
            OurOutputCount = 1, TotalOutputCount = 60,
            OutputValuesSat = Enumerable.Repeat(100_000L, 60).ToArray()
        };
        record.Participations.Add(new CoinJoinParticipationEntity
        {
            UtxoId = utxo.Id, Role = "Output"
        });
        _db.CoinJoinRecords.Add(record);
        _db.SaveChanges();

        var hdWallet = await KompaktorHdWallet.CreateAsync(_db, Network.RegTest, "Adapter2", "pass");
        var selector = new WalletCoinSelector(_db);
        var adapter = new ScoringWalletAdapter(hdWallet, selector, wallet.Id);

        var coins = await adapter.GetCoins();

        // Score should be 50 (well above 5.0 threshold) — no candidates
        Assert.Empty(coins);
    }

    [Fact]
    public async Task GetScoredUtxos_ExcludesSpentAndUnconfirmed()
    {
        var (wallet, address) = SeedWalletWithAddress();

        // Confirmed unspent (should be included)
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "tx1", OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100_000, ScriptPubKey = [0xAA], ConfirmedHeight = 100
        });
        // Unconfirmed (excluded)
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "tx2", OutputIndex = 0, AddressId = address.Id,
            AmountSat = 50_000, ScriptPubKey = [0xAA], ConfirmedHeight = null
        });
        // Spent (excluded)
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "tx3", OutputIndex = 0, AddressId = address.Id,
            AmountSat = 30_000, ScriptPubKey = [0xAA], ConfirmedHeight = 99,
            SpentByTxId = "spender"
        });
        _db.SaveChanges();

        var selector = new WalletCoinSelector(_db);
        var scored = await selector.GetScoredUtxosAsync(wallet.Id);

        Assert.Single(scored);
        Assert.Equal("tx1", scored[0].Utxo.TxId);
    }
}
