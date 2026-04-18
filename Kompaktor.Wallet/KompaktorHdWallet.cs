using Kompaktor.Blockchain;
using Kompaktor.Contracts;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using NBitcoin.BIP322;

namespace Kompaktor.Wallet;

public class KompaktorHdWallet : IKompaktorWalletInterface
{
    private readonly WalletDbContext _db;
    private readonly Network _network;
    private ExtKey? _masterKey;
    private IBlockchainBackend? _blockchain;

    public string WalletId { get; }

    private const int GapLimit = 20;

    private KompaktorHdWallet(WalletDbContext db, Network network, string walletId)
    {
        _db = db;
        _network = network;
        WalletId = walletId;
    }

    public void SetBlockchainBackend(IBlockchainBackend blockchain) => _blockchain = blockchain;

    public static async Task<KompaktorHdWallet> CreateAsync(
        WalletDbContext db, Network network, string name, string passphrase, int wordCount = 12)
    {
        var mnemonic = new Mnemonic(Wordlist.English, wordCount == 24 ? WordCount.TwentyFour : WordCount.Twelve);
        var mnemonicStr = mnemonic.ToString();
        var (encrypted, salt) = MnemonicEncryption.Encrypt(mnemonicStr, passphrase);

        var walletEntity = new WalletEntity
        {
            Name = name,
            EncryptedMnemonic = encrypted,
            MnemonicSalt = salt,
            Network = network.Name
        };

        var masterKey = mnemonic.DeriveExtKey();
        var coinType = network == Network.Main ? 0 : 1;

        // Create P2WPKH account (purpose 84) and P2TR account (purpose 86)
        foreach (var purpose in new[] { 84, 86 })
        {
            var account = new AccountEntity { Purpose = purpose, AccountIndex = 0 };
            var accountKey = masterKey.Derive(new KeyPath($"m/{purpose}'/{coinType}'/0'"));

            // Generate gap limit addresses for external (0) and internal/change (1) chains
            foreach (var chain in new[] { 0, 1 })
            {
                for (var i = 0; i < GapLimit; i++)
                {
                    var childKey = accountKey.Derive(new KeyPath($"{chain}/{i}"));
                    var script = purpose == 84
                        ? childKey.PrivateKey.GetScriptPubKey(ScriptPubKeyType.Segwit)
                        : childKey.PrivateKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

                    account.Addresses.Add(new AddressEntity
                    {
                        KeyPath = $"{chain}/{i}",
                        ScriptPubKey = script.ToBytes(),
                        IsChange = chain == 1
                    });
                }
            }

            walletEntity.Accounts.Add(account);
        }

        db.Wallets.Add(walletEntity);
        await db.SaveChangesAsync();

        var wallet = new KompaktorHdWallet(db, network, walletEntity.Id) { _masterKey = masterKey };
        return wallet;
    }

    public static async Task<KompaktorHdWallet> OpenAsync(
        WalletDbContext db, string walletId, Network network, string passphrase)
    {
        var entity = await db.Wallets.SingleAsync(w => w.Id == walletId);
        var mnemonicStr = MnemonicEncryption.Decrypt(entity.EncryptedMnemonic, entity.MnemonicSalt, passphrase);
        var mnemonic = new Mnemonic(mnemonicStr);
        var masterKey = mnemonic.DeriveExtKey();

        return new KompaktorHdWallet(db, network, walletId) { _masterKey = masterKey };
    }

    /// <summary>
    /// Restores a wallet from a BIP-39 mnemonic. Creates the wallet entity with
    /// encrypted mnemonic storage and pre-derives addresses up to the gap limit.
    /// After restore, call WalletSyncService.FullSyncAsync to discover UTXOs.
    /// </summary>
    public static async Task<KompaktorHdWallet> RestoreAsync(
        WalletDbContext db, Network network, string name, string mnemonicWords, string passphrase)
    {
        var mnemonic = new Mnemonic(mnemonicWords);
        var (encrypted, salt) = MnemonicEncryption.Encrypt(mnemonicWords, passphrase);

        var walletEntity = new WalletEntity
        {
            Name = name,
            EncryptedMnemonic = encrypted,
            MnemonicSalt = salt,
            Network = network.Name
        };

        var masterKey = mnemonic.DeriveExtKey();
        var coinType = network == Network.Main ? 0 : 1;

        foreach (var purpose in new[] { 84, 86 })
        {
            var account = new AccountEntity { Purpose = purpose, AccountIndex = 0 };
            var accountKey = masterKey.Derive(new KeyPath($"m/{purpose}'/{coinType}'/0'"));

            foreach (var chain in new[] { 0, 1 })
            {
                for (var i = 0; i < GapLimit; i++)
                {
                    var childKey = accountKey.Derive(new KeyPath($"{chain}/{i}"));
                    var script = purpose == 84
                        ? childKey.PrivateKey.GetScriptPubKey(ScriptPubKeyType.Segwit)
                        : childKey.PrivateKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

                    account.Addresses.Add(new AddressEntity
                    {
                        KeyPath = $"{chain}/{i}",
                        ScriptPubKey = script.ToBytes(),
                        IsChange = chain == 1
                    });
                }
            }

            walletEntity.Accounts.Add(account);
        }

        db.Wallets.Add(walletEntity);
        await db.SaveChangesAsync();

        return new KompaktorHdWallet(db, network, walletEntity.Id) { _masterKey = masterKey };
    }

    /// <summary>
    /// Exports the decrypted mnemonic words for backup purposes.
    /// The wallet must be unlocked (opened with passphrase) first.
    /// </summary>
    public async Task<string> ExportMnemonicAsync(string passphrase)
    {
        var entity = await _db.Wallets.SingleAsync(w => w.Id == WalletId);
        return MnemonicEncryption.Decrypt(entity.EncryptedMnemonic, entity.MnemonicSalt, passphrase);
    }

    public void Close()
    {
        _masterKey = null;
    }

    /// <summary>
    /// Returns a fresh (unused, unexposed) receive address script.
    /// Prefers P2TR (purpose 86) over P2WPKH (purpose 84) for privacy.
    /// </summary>
    public async Task<Script> GetFreshAddressAsync(bool isChange = false)
    {
        var address = await _db.Addresses
            .Include(a => a.Account)
            .Where(a => a.Account.WalletId == WalletId)
            .Where(a => !a.IsUsed && !a.IsExposed)
            .Where(a => a.IsChange == isChange)
            .OrderByDescending(a => a.Account.Purpose) // Prefer P2TR (86) over P2WPKH (84)
            .ThenBy(a => a.Id)
            .FirstOrDefaultAsync();

        if (address is null)
            throw new InvalidOperationException(
                "No fresh addresses available. Gap limit may need extension.");

        return new Script(address.ScriptPubKey);
    }

    /// <summary>
    /// Returns a fresh change address script. Convenience wrapper for behavior traits.
    /// </summary>
    public Script GetChangeScript()
    {
        // Synchronous wrapper for SelfSendChangeBehaviorTrait's Func<Script> delegate.
        // Safe because EF Core SQLite operations complete synchronously in practice.
        return GetFreshAddressAsync(isChange: true).GetAwaiter().GetResult();
    }

    public async Task<Coin[]> GetCoins()
    {
        var utxos = await _db.Utxos
            .Include(u => u.Address)
            .ThenInclude(a => a.Account)
            .Where(u => u.SpentByTxId == null && u.ConfirmedHeight != null)
            .Where(u => u.Address.Account.Wallet.Id == WalletId)
            .ToListAsync();

        return utxos.Select(u => new Coin(
            new OutPoint(uint256.Parse(u.TxId), u.OutputIndex),
            new TxOut(Money.Satoshis(u.AmountSat), new Script(u.ScriptPubKey))
        )).ToArray();
    }

    public async Task<BIP322Signature.Full> GenerateOwnershipProof(string message, Coin[] coins)
    {
        if (_masterKey is null) throw new InvalidOperationException("Wallet is locked");

        var addressToSignWith = coins.First().ScriptPubKey.GetDestinationAddress(_network);
        var psbt = addressToSignWith!.CreateBIP322PSBT(message, fundProofOutputs: coins);
        psbt = psbt.AddCoins(coins);

        var keys = new Key[coins.Length];
        for (int i = 0; i < coins.Length; i++)
            keys[i] = await DeriveKeyForScriptAsync(coins[i].ScriptPubKey);
        psbt = psbt.SignWithKeys(keys);

        return (BIP322Signature.Full)BIP322Signature.FromPSBT(psbt, SignatureType.Full);
    }

    public async Task<WitScript> GenerateWitness(Coin coin, Transaction tx, IEnumerable<Coin> txCoins)
    {
        if (_masterKey is null) throw new InvalidOperationException("Wallet is locked");

        var key = await DeriveKeyForScriptAsync(coin.ScriptPubKey);
        var allCoins = txCoins.ToArray();
        var idx = Array.FindIndex(allCoins, c => c.Outpoint == coin.Outpoint);

        var builder = _network.CreateTransactionBuilder();
        builder.AddKeys(key);
        builder.AddCoins(allCoins);

        var signed = builder.SignTransaction(tx);
        return signed.Inputs[idx].WitScript;
    }

    public async Task<bool?> VerifyUtxo(OutPoint outpoint, TxOut expectedTxOut)
    {
        if (_blockchain is null) return null;

        var info = await _blockchain.GetUtxoAsync(outpoint);
        if (info is null) return false;
        return info.TxOut.Value == expectedTxOut.Value && info.TxOut.ScriptPubKey == expectedTxOut.ScriptPubKey;
    }

    public async Task MarkScriptsExposed(IEnumerable<Script> scripts)
    {
        var scriptBytesList = scripts.Select(s => s.ToBytes()).ToList();

        var addresses = await _db.Addresses
            .Include(a => a.Account)
            .Where(a => a.Account.Wallet.Id == WalletId)
            .ToListAsync();

        foreach (var addr in addresses)
        {
            if (scriptBytesList.Any(sb => sb.SequenceEqual(addr.ScriptPubKey)))
                addr.IsExposed = true;
        }

        await _db.SaveChangesAsync();
    }

    private async Task<Key> DeriveKeyForScriptAsync(Script scriptPubKey)
    {
        var scriptBytes = scriptPubKey.ToBytes();
        var address = await _db.Addresses
            .Include(a => a.Account)
            .Where(a => a.Account.Wallet.Id == WalletId)
            .ToListAsync();

        var match = address.First(a => a.ScriptPubKey.SequenceEqual(scriptBytes));

        var coinType = _network == Network.Main ? 0 : 1;
        var fullPath = new KeyPath($"m/{match.Account.Purpose}'/{coinType}'/{match.Account.AccountIndex}'/{match.KeyPath}");
        return _masterKey!.Derive(fullPath).PrivateKey;
    }
}
