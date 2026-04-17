using Kompaktor.Wallet;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Xunit;

namespace Kompaktor.Tests;

public class KompaktorHdWalletTests : IDisposable
{
    private readonly WalletDbContext _db;
    private readonly Network _network = Network.RegTest;
    private const string Passphrase = "test-passphrase";

    public KompaktorHdWalletTests()
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
    public async Task Create_GeneratesWalletWithAccounts()
    {
        await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);

        var entity = _db.Wallets.Include(w => w.Accounts).ThenInclude(a => a.Addresses).Single();
        Assert.Equal("Test", entity.Name);
        Assert.Equal(2, entity.Accounts.Count); // P2WPKH (84) + P2TR (86)
        Assert.All(entity.Accounts, a => Assert.True(a.Addresses.Count >= 20)); // Gap limit per chain
    }

    [Fact]
    public async Task Create_GeneratesCorrectAddressCounts()
    {
        await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);

        var entity = _db.Wallets.Include(w => w.Accounts).ThenInclude(a => a.Addresses).Single();
        foreach (var account in entity.Accounts)
        {
            // 20 external + 20 change = 40 per account
            Assert.Equal(40, account.Addresses.Count);
            Assert.Equal(20, account.Addresses.Count(a => !a.IsChange));
            Assert.Equal(20, account.Addresses.Count(a => a.IsChange));
        }
    }

    [Fact]
    public async Task Open_DecryptsMnemonic_DerivesKeys()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);
        wallet.Close();

        var reopened = await KompaktorHdWallet.OpenAsync(_db, wallet.WalletId, _network, Passphrase);
        Assert.NotNull(reopened);
        Assert.Equal(wallet.WalletId, reopened.WalletId);
    }

    [Fact]
    public async Task Open_WrongPassphrase_Throws()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);
        wallet.Close();

        await Assert.ThrowsAnyAsync<Exception>(
            () => KompaktorHdWallet.OpenAsync(_db, wallet.WalletId, _network, "wrong-password"));
    }

    [Fact]
    public async Task GetCoins_ReturnsOnlyConfirmedUnspent()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);
        var walletEntity = _db.Wallets.Include(w => w.Accounts).ThenInclude(a => a.Addresses).Single();
        var address = walletEntity.Accounts[0].Addresses[0];

        // Add a confirmed unspent UTXO
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "abc1230000000000000000000000000000000000000000000000000000000000",
            OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100000, ScriptPubKey = address.ScriptPubKey,
            ConfirmedHeight = 100
        });
        // Add an unconfirmed UTXO (should be excluded)
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "def4560000000000000000000000000000000000000000000000000000000000",
            OutputIndex = 0, AddressId = address.Id,
            AmountSat = 50000, ScriptPubKey = address.ScriptPubKey,
            ConfirmedHeight = null
        });
        _db.SaveChanges();

        var coins = await wallet.GetCoins();
        Assert.Single(coins);
        Assert.Equal(Money.Satoshis(100000), coins[0].Amount);
    }

    [Fact]
    public async Task MarkScriptsExposed_SetsFlag()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);
        var walletEntity = _db.Wallets.Include(w => w.Accounts).ThenInclude(a => a.Addresses).Single();
        var address = walletEntity.Accounts[0].Addresses.First(a => !a.IsChange);
        var script = new Script(address.ScriptPubKey);

        await wallet.MarkScriptsExposed([script]);

        // Reload
        _db.ChangeTracker.Clear();
        var reloaded = _db.Addresses.Single(a => a.Id == address.Id);
        Assert.True(reloaded.IsExposed);
    }

    [Fact]
    public async Task VerifyUtxo_WithoutBlockchain_ReturnsNull()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);
        // No blockchain backend set
        var result = await wallet.VerifyUtxo(new OutPoint(uint256.One, 0), new TxOut(Money.Coins(1), new Key().GetScriptPubKey(ScriptPubKeyType.Segwit)));
        Assert.Null(result);
    }
}
