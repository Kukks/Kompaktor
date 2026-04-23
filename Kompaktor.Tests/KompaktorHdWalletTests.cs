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
    public async Task GetCoins_ExcludesFrozenUtxos()
    {
        // Regression: freezing a UTXO is the user's explicit "do not spend"
        // signal. It was respected by the scoring-based selector but leaked
        // through the wallet's raw GetCoins() — which the scoring adapter
        // falls back to when scoring fails, and which any non-mixing caller
        // hits directly. A frozen UTXO must never surface as a spendable coin.
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);
        var walletEntity = _db.Wallets.Include(w => w.Accounts).ThenInclude(a => a.Addresses).Single();
        var address = walletEntity.Accounts[0].Addresses[0];

        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "aaa1230000000000000000000000000000000000000000000000000000000000",
            OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100000, ScriptPubKey = address.ScriptPubKey,
            ConfirmedHeight = 100,
            IsFrozen = false
        });
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "bbb4560000000000000000000000000000000000000000000000000000000000",
            OutputIndex = 0, AddressId = address.Id,
            AmountSat = 77777, ScriptPubKey = address.ScriptPubKey,
            ConfirmedHeight = 100,
            IsFrozen = true
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

    [Fact]
    public async Task GetFreshAddress_ReturnsFreshExternalAddress()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);

        var script = await wallet.GetFreshAddressAsync();
        Assert.NotNull(script);

        // Should be a valid script
        var address = script.GetDestinationAddress(_network);
        Assert.NotNull(address);
    }

    [Fact]
    public async Task GetFreshAddress_PrefersTaproot()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);

        var script = await wallet.GetFreshAddressAsync();
        var address = script.GetDestinationAddress(_network);

        // P2TR address on regtest starts with "bcrt1p"
        Assert.StartsWith("bcrt1p", address!.ToString());
    }

    [Fact]
    public async Task GetFreshAddress_SkipsExposedAddresses()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);

        // Get first fresh address and mark it as exposed
        var first = await wallet.GetFreshAddressAsync();
        await wallet.MarkScriptsExposed([first]);

        // Next address should be different
        var second = await wallet.GetFreshAddressAsync();
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task GetFreshAddress_ChangeAddress_ReturnsDifferentFromReceive()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);

        var receive = await wallet.GetFreshAddressAsync(isChange: false);
        var change = await wallet.GetFreshAddressAsync(isChange: true);

        Assert.NotEqual(receive, change);
    }

    [Fact]
    public async Task GetChangeScript_ReturnsFreshChangeScript()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);

        var script = wallet.GetChangeScript();
        Assert.NotNull(script);

        var address = script.GetDestinationAddress(_network);
        Assert.NotNull(address);
    }

    [Fact]
    public async Task Restore_RecreatesWalletFromMnemonic()
    {
        // Create a wallet and export its mnemonic
        var original = await KompaktorHdWallet.CreateAsync(_db, _network, "Original", Passphrase);
        var mnemonic = await original.ExportMnemonicAsync(Passphrase);
        var originalAddresses = _db.Addresses
            .Include(a => a.Account)
            .Where(a => a.Account.WalletId == original.WalletId)
            .Select(a => a.ScriptPubKey)
            .ToList();

        // Delete the original wallet (simulate fresh database)
        var opts = new DbContextOptionsBuilder<WalletDbContext>()
            .UseSqlite("DataSource=restore-test;Mode=Memory;Cache=Shared")
            .Options;
        using var freshDb = new WalletDbContext(opts);
        freshDb.Database.OpenConnection();
        freshDb.Database.EnsureCreated();

        // Restore from mnemonic
        var restored = await KompaktorHdWallet.RestoreAsync(freshDb, _network, "Restored", mnemonic, Passphrase);

        var restoredAddresses = freshDb.Addresses
            .Include(a => a.Account)
            .Where(a => a.Account.WalletId == restored.WalletId)
            .Select(a => a.ScriptPubKey)
            .ToList();

        // Same number of addresses
        Assert.Equal(originalAddresses.Count, restoredAddresses.Count);

        // All addresses match (deterministic derivation)
        foreach (var orig in originalAddresses)
        {
            Assert.Contains(restoredAddresses, r => r.SequenceEqual(orig));
        }

        freshDb.Database.CloseConnection();
    }

    [Fact]
    public async Task ExportMnemonic_WrongPassphrase_Throws()
    {
        await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);
        var wallet = await KompaktorHdWallet.OpenAsync(
            _db, _db.Wallets.Single().Id, _network, Passphrase);

        await Assert.ThrowsAnyAsync<Exception>(
            () => wallet.ExportMnemonicAsync("wrong-passphrase"));
    }

    [Fact]
    public async Task ExportMnemonic_ReturnsValidBip39Words()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);

        var mnemonic = await wallet.ExportMnemonicAsync(Passphrase);

        Assert.NotNull(mnemonic);
        var words = mnemonic.Split(' ');
        Assert.Equal(12, words.Length);
        // Verify it's a valid BIP-39 mnemonic by parsing it
        var parsed = new Mnemonic(mnemonic);
        Assert.NotNull(parsed);
    }

    [Fact]
    public async Task Restore_InvalidMnemonic_Throws()
    {
        await Assert.ThrowsAnyAsync<Exception>(
            () => KompaktorHdWallet.RestoreAsync(_db, _network, "Bad", "not a valid mnemonic", Passphrase));
    }

    [Fact]
    public async Task Create_StoresAccountXPub()
    {
        await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);

        var accounts = _db.Accounts.ToList();
        Assert.All(accounts, a => Assert.NotNull(a.AccountXPub));
        Assert.All(accounts, a => Assert.True(a.AccountXPub!.Length > 0));
    }

    [Fact]
    public async Task DeriveAddressesFromXPub_MatchesOriginalDerivation()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);

        var account = _db.Accounts.Include(a => a.Addresses)
            .First(a => a.Purpose == 86);

        // Derive address at index 0 from xpub
        var derived = KompaktorHdWallet.DeriveAddressesFromXPub(
            account.AccountXPub!, _network, 86, 0, 0, 1);

        Assert.Single(derived);
        // Should match the original address at 0/0
        var original = account.Addresses.First(a => a.KeyPath == "0/0");
        Assert.Equal(original.ScriptPubKey, derived[0].ScriptPubKey);
    }

    [Fact]
    public async Task GetFreshAddress_AutoExtendsGap_WhenExhausted()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);

        // Mark ALL external (non-change) addresses as used
        var externalAddrs = _db.Addresses
            .Include(a => a.Account)
            .Where(a => a.Account.WalletId == wallet.WalletId && !a.IsChange)
            .ToList();
        foreach (var addr in externalAddrs)
            addr.IsUsed = true;
        _db.SaveChanges();

        var countBefore = _db.Addresses.Count(a => a.Account.WalletId == wallet.WalletId && !a.IsChange);

        // This should auto-extend the gap rather than throwing
        var script = await wallet.GetFreshAddressAsync();
        Assert.NotNull(script);

        var countAfter = _db.Addresses.Count(a => a.Account.WalletId == wallet.WalletId && !a.IsChange);
        Assert.True(countAfter > countBefore, "Gap extension should have added new addresses");
    }
}
