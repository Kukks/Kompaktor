using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kompaktor.Tests;

public class WalletDbTests : IDisposable
{
    private readonly WalletDbContext _db;

    public WalletDbTests()
    {
        var options = new DbContextOptionsBuilder<WalletDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _db = new WalletDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void CanCreateWalletWithAccountsAndAddresses()
    {
        var wallet = new WalletEntity
        {
            Name = "Test Wallet",
            EncryptedMnemonic = [1, 2, 3],
            MnemonicSalt = [4, 5, 6],
            Network = "RegTest"
        };
        wallet.Accounts.Add(new AccountEntity
        {
            Purpose = 84,
            AccountIndex = 0,
            Addresses =
            {
                new AddressEntity { KeyPath = "0/0", ScriptPubKey = [0xAA], IsChange = false },
                new AddressEntity { KeyPath = "1/0", ScriptPubKey = [0xBB], IsChange = true }
            }
        });

        _db.Wallets.Add(wallet);
        _db.SaveChanges();

        var loaded = _db.Wallets.Include(w => w.Accounts).ThenInclude(a => a.Addresses).Single();
        Assert.Equal("Test Wallet", loaded.Name);
        Assert.Single(loaded.Accounts);
        Assert.Equal(2, loaded.Accounts[0].Addresses.Count);
    }

    [Fact]
    public void UtxoUniqueIndex_PreventsDoubleInsert()
    {
        var wallet = new WalletEntity { Name = "W" };
        var account = new AccountEntity { Purpose = 84, AccountIndex = 0, Wallet = wallet };
        var address = new AddressEntity { KeyPath = "0/0", ScriptPubKey = [0xAA], Account = account };
        _db.Wallets.Add(wallet);
        _db.Addresses.Add(address);
        _db.SaveChanges();

        _db.Utxos.Add(new UtxoEntity { TxId = "abc123", OutputIndex = 0, AddressId = address.Id, AmountSat = 50000, ScriptPubKey = [0xAA] });
        _db.SaveChanges();

        _db.Utxos.Add(new UtxoEntity { TxId = "abc123", OutputIndex = 0, AddressId = address.Id, AmountSat = 50000, ScriptPubKey = [0xAA] });
        Assert.Throws<DbUpdateException>(() => _db.SaveChanges());
    }

    [Fact]
    public void QueryUnspentConfirmedUtxos()
    {
        var wallet = new WalletEntity { Name = "W" };
        var account = new AccountEntity { Purpose = 84, AccountIndex = 0, Wallet = wallet };
        var address = new AddressEntity { KeyPath = "0/0", ScriptPubKey = [0xAA], Account = account };
        _db.Wallets.Add(wallet);
        _db.Addresses.Add(address);
        _db.SaveChanges();

        // Confirmed, unspent
        _db.Utxos.Add(new UtxoEntity { TxId = "tx1", OutputIndex = 0, AddressId = address.Id, AmountSat = 10000, ScriptPubKey = [0xAA], ConfirmedHeight = 100 });
        // Unconfirmed
        _db.Utxos.Add(new UtxoEntity { TxId = "tx2", OutputIndex = 0, AddressId = address.Id, AmountSat = 20000, ScriptPubKey = [0xAA], ConfirmedHeight = null });
        // Confirmed but spent
        _db.Utxos.Add(new UtxoEntity { TxId = "tx3", OutputIndex = 0, AddressId = address.Id, AmountSat = 30000, ScriptPubKey = [0xAA], ConfirmedHeight = 101, SpentByTxId = "spender" });
        _db.SaveChanges();

        var coins = _db.Utxos.Where(u => u.SpentByTxId == null && u.ConfirmedHeight != null).ToList();
        Assert.Single(coins);
        Assert.Equal("tx1", coins[0].TxId);
    }

    [Fact]
    public void CoinJoinRecordWithParticipations()
    {
        var wallet = new WalletEntity { Name = "W" };
        var account = new AccountEntity { Purpose = 84, AccountIndex = 0, Wallet = wallet };
        var address = new AddressEntity { KeyPath = "0/0", ScriptPubKey = [0xAA], Account = account };
        _db.Wallets.Add(wallet);
        _db.Addresses.Add(address);
        _db.SaveChanges();

        var utxo = new UtxoEntity { TxId = "tx1", OutputIndex = 0, AddressId = address.Id, AmountSat = 50000, ScriptPubKey = [0xAA], ConfirmedHeight = 100 };
        _db.Utxos.Add(utxo);

        var tx = new TransactionEntity { Id = "cjtx1", RawHex = "0200000001..." };
        _db.Transactions.Add(tx);
        _db.SaveChanges();

        var record = new CoinJoinRecordEntity
        {
            TransactionId = "cjtx1",
            RoundId = "round-1",
            Status = "Completed",
            OurInputCount = 1,
            TotalInputCount = 5,
            OurOutputCount = 1,
            TotalOutputCount = 5
        };
        record.Participations.Add(new CoinJoinParticipationEntity { UtxoId = utxo.Id, Role = "Input" });
        _db.CoinJoinRecords.Add(record);
        _db.SaveChanges();

        var loaded = _db.CoinJoinRecords.Include(c => c.Participations).Single();
        Assert.Single(loaded.Participations);
        Assert.Equal("Input", loaded.Participations[0].Role);
    }

    [Fact]
    public void LabelEntity_PolymorphicIndex()
    {
        _db.Labels.Add(new LabelEntity { EntityType = "Address", EntityId = "1", Text = "Exchange deposit" });
        _db.Labels.Add(new LabelEntity { EntityType = "Transaction", EntityId = "tx1", Text = "CoinJoin" });
        _db.SaveChanges();

        var addressLabels = _db.Labels.Where(l => l.EntityType == "Address" && l.EntityId == "1").ToList();
        Assert.Single(addressLabels);
        Assert.Equal("Exchange deposit", addressLabels[0].Text);
    }

    [Fact]
    public async Task CoinJoinRecorder_RecordsRound_CreatesOutputUtxos()
    {
        var wallet = new WalletEntity { Name = "RecorderTest" };
        var account = new AccountEntity { Purpose = 86, AccountIndex = 0, Wallet = wallet };

        // Create an address and input UTXO
        var key = new NBitcoin.Key();
        var script = key.PubKey.GetScriptPubKey(NBitcoin.ScriptPubKeyType.TaprootBIP86);
        var address = new AddressEntity
        {
            KeyPath = "0/0", ScriptPubKey = script.ToBytes(), Account = account
        };
        _db.Wallets.Add(wallet);
        _db.Addresses.Add(address);
        _db.SaveChanges();

        var inputUtxo = new UtxoEntity
        {
            TxId = "aabb000000000000000000000000000000000000000000000000000000000000",
            OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100_000, ScriptPubKey = script.ToBytes(),
            ConfirmedHeight = 100
        };
        _db.Utxos.Add(inputUtxo);
        _db.SaveChanges();

        // Build a fake coinjoin transaction with one of our outputs
        var tx = NBitcoin.Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new NBitcoin.TxIn(new NBitcoin.OutPoint(
            NBitcoin.uint256.Parse("aabb000000000000000000000000000000000000000000000000000000000000"), 0)));
        tx.Outputs.Add(new NBitcoin.TxOut(NBitcoin.Money.Satoshis(90_000), script));

        var recorder = new Kompaktor.Wallet.CoinJoinRecorder(_db, wallet.Id);
        await recorder.RecordRoundAsync(
            "test-round",
            tx,
            [new NBitcoin.OutPoint(NBitcoin.uint256.Parse("aabb000000000000000000000000000000000000000000000000000000000000"), 0)],
            [script],
            totalParticipants: 5);

        // Verify the coinjoin record
        var record = _db.CoinJoinRecords
            .Include(r => r.Participations)
            .Single();
        Assert.Equal("Completed", record.Status);
        Assert.Equal("test-round", record.RoundId);
        Assert.Equal(1, record.OurInputCount);
        Assert.Equal(5, record.ParticipantCount);
        Assert.Equal(2, record.Participations.Count); // 1 input + 1 output

        // Verify input was marked as spent
        var spentUtxo = _db.Utxos.Single(u => u.Id == inputUtxo.Id);
        Assert.NotNull(spentUtxo.SpentByTxId);

        // Verify output UTXO was created
        var outputUtxos = _db.Utxos.Where(u => u.TxId == tx.GetHash().ToString()).ToList();
        Assert.Single(outputUtxos);
        Assert.Equal(90_000, outputUtxos[0].AmountSat);
    }

    [Fact]
    public async Task CoinJoinRecorder_RecordFailedRound_TracksForIntersectionAnalysis()
    {
        var wallet = new WalletEntity { Name = "FailedRoundTest" };
        _db.Wallets.Add(wallet);
        _db.SaveChanges();

        var recorder = new Kompaktor.Wallet.CoinJoinRecorder(_db, wallet.Id);
        var inputOutpoints = new[]
        {
            new NBitcoin.OutPoint(NBitcoin.uint256.Parse("aabb000000000000000000000000000000000000000000000000000000000000"), 0),
            new NBitcoin.OutPoint(NBitcoin.uint256.Parse("ccdd000000000000000000000000000000000000000000000000000000000000"), 1)
        };

        await recorder.RecordFailedRoundAsync("failed-round-1", inputOutpoints);

        var record = _db.CoinJoinRecords.Single();
        Assert.Equal("Failed", record.Status);
        Assert.Equal("failed-round-1", record.RoundId);
        Assert.Equal(2, record.OurInputCount);
    }

    [Fact]
    public async Task WalletSyncService_GetBalance_SeparatesConfirmedAndUnconfirmed()
    {
        var wallet = new WalletEntity { Name = "SyncTest" };
        var account = new AccountEntity { Purpose = 84, AccountIndex = 0, Wallet = wallet };
        var address = new AddressEntity { KeyPath = "0/0", ScriptPubKey = [0xAA], Account = account };
        _db.Wallets.Add(wallet);
        _db.Addresses.Add(address);
        _db.SaveChanges();

        // Confirmed UTXO
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "tx1", OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100_000, ScriptPubKey = [0xAA], ConfirmedHeight = 100
        });
        // Unconfirmed UTXO
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "tx2", OutputIndex = 0, AddressId = address.Id,
            AmountSat = 50_000, ScriptPubKey = [0xAA], ConfirmedHeight = null
        });
        // Spent UTXO (should not count)
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "tx3", OutputIndex = 0, AddressId = address.Id,
            AmountSat = 30_000, ScriptPubKey = [0xAA], ConfirmedHeight = 99,
            SpentByTxId = "spender"
        });
        _db.SaveChanges();

        var sync = new Kompaktor.Wallet.WalletSyncService(
            _db, null!, NBitcoin.Network.RegTest);
        var (confirmed, unconfirmed) = await sync.GetBalanceAsync(wallet.Id);

        Assert.Equal(100_000, confirmed);
        Assert.Equal(50_000, unconfirmed);
    }
}
