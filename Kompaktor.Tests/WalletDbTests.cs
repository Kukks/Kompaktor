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
}
