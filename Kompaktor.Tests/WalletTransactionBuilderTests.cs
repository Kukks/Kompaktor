using Kompaktor.Scoring;
using Kompaktor.Wallet;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Xunit;

namespace Kompaktor.Tests;

public class WalletTransactionBuilderTests : IDisposable
{
    private readonly WalletDbContext _db;
    private readonly Network _network = Network.RegTest;

    public WalletTransactionBuilderTests()
    {
        var options = new DbContextOptionsBuilder<WalletDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _db = new WalletDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    private (WalletEntity wallet, AddressEntity external, AddressEntity change) SeedWallet()
    {
        var wallet = new WalletEntity { Name = "TxBuilder" };
        var account = new AccountEntity { Purpose = 84, AccountIndex = 0, Wallet = wallet };

        // Create a real key so we get a valid scriptPubKey
        var key = new Key();
        var script = key.GetScriptPubKey(ScriptPubKeyType.Segwit);

        var external = new AddressEntity
        {
            KeyPath = "0/0", ScriptPubKey = script.ToBytes(),
            Account = account, IsChange = false
        };

        var changeKey = new Key();
        var changeScript = changeKey.GetScriptPubKey(ScriptPubKeyType.Segwit);
        var change = new AddressEntity
        {
            KeyPath = "1/0", ScriptPubKey = changeScript.ToBytes(),
            Account = account, IsChange = true
        };

        _db.Wallets.Add(wallet);
        _db.Addresses.AddRange(external, change);
        _db.SaveChanges();
        return (wallet, external, change);
    }

    [Fact]
    public async Task PlanTransaction_CreatesValidUnsignedTx()
    {
        var (wallet, address, _) = SeedWallet();

        // Fund the wallet
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "aa11000000000000000000000000000000000000000000000000000000000000",
            OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100_000, ScriptPubKey = address.ScriptPubKey,
            ConfirmedHeight = 100
        });
        _db.SaveChanges();

        var destination = new Key().GetScriptPubKey(ScriptPubKeyType.Segwit);
        var builder = new WalletTransactionBuilder(_db, _network);

        var plan = await builder.PlanTransactionAsync(
            wallet.Id, destination, Money.Satoshis(50_000),
            new FeeRate(2m));

        Assert.NotNull(plan.Transaction);
        Assert.Single(plan.InputCoins);
        Assert.True(plan.EstimatedFee.Satoshi > 0);
        Assert.Single(plan.SelectedUtxos);
    }

    [Fact]
    public async Task PlanTransaction_InsufficientFunds_Throws()
    {
        var (wallet, address, _) = SeedWallet();

        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "aa11000000000000000000000000000000000000000000000000000000000000",
            OutputIndex = 0, AddressId = address.Id,
            AmountSat = 10_000, ScriptPubKey = address.ScriptPubKey,
            ConfirmedHeight = 100
        });
        _db.SaveChanges();

        var destination = new Key().GetScriptPubKey(ScriptPubKeyType.Segwit);
        var builder = new WalletTransactionBuilder(_db, _network);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.PlanTransactionAsync(wallet.Id, destination,
                Money.Satoshis(50_000), new FeeRate(2m)));
    }

    [Fact]
    public async Task PlanTransaction_SelectsHighAnonCoinsFirst()
    {
        var (wallet, address, _) = SeedWallet();

        // Virgin coin (low score)
        var utxo1 = new UtxoEntity
        {
            TxId = "aa11000000000000000000000000000000000000000000000000000000000000",
            OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100_000, ScriptPubKey = address.ScriptPubKey,
            ConfirmedHeight = 100
        };
        // Coinjoined coin (high score)
        var utxo2 = new UtxoEntity
        {
            TxId = "bb22000000000000000000000000000000000000000000000000000000000000",
            OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100_000, ScriptPubKey = address.ScriptPubKey,
            ConfirmedHeight = 101
        };
        _db.Utxos.AddRange(utxo1, utxo2);
        _db.SaveChanges();

        // Make utxo2 a coinjoin output
        var tx = new TransactionEntity { Id = "cjtx1", RawHex = "0200..." };
        _db.Transactions.Add(tx);
        var record = new CoinJoinRecordEntity
        {
            TransactionId = "cjtx1", RoundId = "round-1", Status = "Completed",
            ParticipantCount = 20, OurInputCount = 1, TotalInputCount = 25,
            OurOutputCount = 1, TotalOutputCount = 25,
            OutputValuesSat = Enumerable.Repeat(100_000L, 25).ToArray()
        };
        record.Participations.Add(new CoinJoinParticipationEntity
        {
            UtxoId = utxo2.Id, Role = "Output"
        });
        _db.CoinJoinRecords.Add(record);
        _db.SaveChanges();

        var destination = new Key().GetScriptPubKey(ScriptPubKeyType.Segwit);
        var builder = new WalletTransactionBuilder(_db, _network);

        var plan = await builder.PlanTransactionAsync(
            wallet.Id, destination, Money.Satoshis(50_000),
            new FeeRate(2m), CoinSelectionStrategy.PrivacyFirst);

        // Privacy-first should prefer the coinjoined coin
        Assert.Single(plan.SelectedUtxos);
        Assert.Equal(utxo2.Id, plan.SelectedUtxos[0].Utxo.Id);
    }

    [Fact]
    public async Task PlanTransaction_FeeSaver_PrefersLargestCoin()
    {
        var (wallet, address, _) = SeedWallet();

        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "aa11000000000000000000000000000000000000000000000000000000000000",
            OutputIndex = 0, AddressId = address.Id,
            AmountSat = 200_000, ScriptPubKey = address.ScriptPubKey,
            ConfirmedHeight = 100
        });
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "bb22000000000000000000000000000000000000000000000000000000000000",
            OutputIndex = 0, AddressId = address.Id,
            AmountSat = 50_000, ScriptPubKey = address.ScriptPubKey,
            ConfirmedHeight = 101
        });
        _db.SaveChanges();

        var destination = new Key().GetScriptPubKey(ScriptPubKeyType.Segwit);
        var builder = new WalletTransactionBuilder(_db, _network);

        var plan = await builder.PlanTransactionAsync(
            wallet.Id, destination, Money.Satoshis(30_000),
            new FeeRate(2m), CoinSelectionStrategy.FeeSaver);

        // Fee saver should prefer the single larger coin (fewer inputs = lower fees)
        Assert.Single(plan.SelectedUtxos);
        Assert.Equal(200_000, plan.SelectedUtxos[0].Utxo.AmountSat);
    }

    [Fact]
    public async Task PlanTransaction_WarnsWhenCoSpendingExposedAndNonExposedUtxos()
    {
        // Build a wallet with two spendable addresses — one flagged exposed
        // (simulating a prior coinjoin revealed the script), one not. Fund
        // each with enough to force the selector to pick BOTH, then verify
        // the planner adds a warning about the linkage leakage.
        var (wallet, external, _) = SeedWallet();

        var exposedKey = new Key();
        var exposedScript = exposedKey.GetScriptPubKey(ScriptPubKeyType.Segwit);
        var exposed = new AddressEntity
        {
            KeyPath = "0/1", ScriptPubKey = exposedScript.ToBytes(),
            Account = external.Account, IsChange = false, IsExposed = true
        };
        _db.Addresses.Add(exposed);
        _db.SaveChanges();

        // Two UTXOs, 60k each — neither alone covers a 80k send after fees
        // so the selector MUST pick both.
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "aa11000000000000000000000000000000000000000000000000000000000000",
            OutputIndex = 0, AddressId = external.Id,
            AmountSat = 60_000, ScriptPubKey = external.ScriptPubKey,
            ConfirmedHeight = 100
        });
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "bb22000000000000000000000000000000000000000000000000000000000000",
            OutputIndex = 0, AddressId = exposed.Id,
            AmountSat = 60_000, ScriptPubKey = exposed.ScriptPubKey,
            ConfirmedHeight = 101
        });
        _db.SaveChanges();

        var destination = new Key().GetScriptPubKey(ScriptPubKeyType.Segwit);
        var builder = new WalletTransactionBuilder(_db, _network);

        var plan = await builder.PlanTransactionAsync(
            wallet.Id, destination, Money.Satoshis(80_000),
            new FeeRate(2m));

        Assert.Equal(2, plan.SelectedUtxos.Count);
        Assert.Contains(plan.Warnings, w => w.Contains("exposed") && w.Contains("non-exposed"));
    }
}
