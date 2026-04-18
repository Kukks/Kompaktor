using Kompaktor.Wallet;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;

namespace Kompaktor.Scoring;

/// <summary>
/// Builds privacy-aware Bitcoin transactions using scored coin selection.
/// Selects coins based on anonymity scores and creates change outputs
/// using fresh wallet addresses.
/// </summary>
public class WalletTransactionBuilder
{
    private readonly WalletDbContext _db;
    private readonly WalletCoinSelector _coinSelector;
    private readonly Network _network;

    public WalletTransactionBuilder(WalletDbContext db, Network network, ScoringOptions? options = null)
    {
        _db = db;
        _coinSelector = new WalletCoinSelector(db, options);
        _network = network;
    }

    /// <summary>
    /// Creates a transaction sending to the specified destination.
    /// Uses privacy-first coin selection by default.
    /// Returns the unsigned PSBT and selected coins for signing.
    /// </summary>
    public async Task<TransactionPlan> PlanTransactionAsync(
        string walletId,
        Script destination,
        Money amount,
        FeeRate feeRate,
        CoinSelectionStrategy strategy = CoinSelectionStrategy.PrivacyFirst,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();

        // Select coins using privacy-aware scoring
        var selection = await _coinSelector.SelectForSpendAsync(
            walletId, amount.Satoshi, strategy, ct);

        if (selection.Warnings.Length > 0)
            warnings.AddRange(selection.Warnings);

        if (selection.TotalAmountSat < amount.Satoshi)
            throw new InvalidOperationException(
                $"Insufficient funds: need {amount.Satoshi} sat, have {selection.TotalAmountSat} sat");

        // Convert scored UTXOs to NBitcoin Coins
        var coins = selection.Selected.Select(s => new Coin(
            new OutPoint(uint256.Parse(s.Utxo.TxId), s.Utxo.OutputIndex),
            new TxOut(Money.Satoshis(s.Utxo.AmountSat), new Script(s.Utxo.ScriptPubKey))
        )).ToArray();

        // Get fresh change address (prefer Taproot)
        var changeAddress = await GetFreshChangeAddressAsync(walletId, ct);

        // Build the transaction with RBF opt-in (BIP 125)
        var builder = _network.CreateTransactionBuilder();
        builder.AddCoins(coins);
        builder.Send(destination, amount);
        builder.SetChange(changeAddress);
        builder.SendEstimatedFees(feeRate);
        builder.OptInRBF = true;

        var tx = builder.BuildTransaction(sign: false);
        var fee = builder.EstimateFees(tx, feeRate);

        // Check if change output exists and warn about dust
        var changeOutput = tx.Outputs
            .FirstOrDefault(o => o.ScriptPubKey == changeAddress);
        if (changeOutput is null && selection.TotalAmountSat > amount.Satoshi + fee.Satoshi)
            warnings.Add("Change amount below dust threshold — absorbed into fee");

        // Privacy warning: check if all selected coins have similar anonymity levels
        var scores = selection.Selected.Select(s => s.Score.EffectiveScore).ToList();
        if (scores.Count > 1 && scores.Max() / Math.Max(scores.Min(), 0.01) > 10)
            warnings.Add("Selected coins have very different anonymity levels — consider mixing first");

        // Privacy warning: spending unmixed coins
        var unmixedInputs = selection.Selected.Count(s => s.Score.CoinJoinCount == 0);
        if (unmixedInputs > 0)
            warnings.Add($"{unmixedInputs} of {selection.Selected.Count} inputs have never been mixed — this transaction may be traceable");

        // Privacy warning: address reuse on inputs
        var reusedInputs = selection.Selected.Count(s => s.Score.ReusePenalty < 1.0);
        if (reusedInputs > 0)
            warnings.Add($"{reusedInputs} input(s) come from reused addresses — address reuse degrades privacy");

        // Privacy warning: script type mismatch between inputs and destination
        if (coins.Length > 0)
        {
            var inputIsP2tr = coins[0].TxOut.ScriptPubKey.IsScriptType(ScriptType.Taproot);
            var destIsP2tr = destination.IsScriptType(ScriptType.Taproot);
            if (inputIsP2tr != destIsP2tr)
                warnings.Add("Script type mismatch between inputs and destination — change output may be identifiable by type");
        }

        return new TransactionPlan(
            tx,
            coins,
            fee,
            changeAddress,
            selection.Selected,
            warnings.ToArray());
    }

    private async Task<Script> GetFreshChangeAddressAsync(string walletId, CancellationToken ct)
    {
        var address = await _db.Addresses
            .Include(a => a.Account)
            .Where(a => a.Account.WalletId == walletId)
            .Where(a => !a.IsUsed && !a.IsExposed && a.IsChange)
            .OrderByDescending(a => a.Account.Purpose) // Prefer P2TR
            .ThenBy(a => a.Id)
            .FirstOrDefaultAsync(ct);

        if (address is null)
        {
            // Auto-extend change addresses from stored xpub
            var accounts = await _db.Accounts
                .Include(a => a.Addresses)
                .Where(a => a.WalletId == walletId && a.AccountXPub != null)
                .ToListAsync(ct);

            foreach (var acct in accounts)
            {
                var changeAddrs = acct.Addresses.Where(a => a.IsChange).ToList();
                var maxIdx = changeAddrs.Count > 0
                    ? changeAddrs.Max(a => int.Parse(a.KeyPath.Split('/')[1]))
                    : -1;
                var newAddrs = KompaktorHdWallet.DeriveAddressesFromXPub(
                    acct.AccountXPub!, _network, acct.Purpose, 1, maxIdx + 1, 20);
                foreach (var a in newAddrs)
                {
                    a.AccountId = acct.Id;
                    _db.Addresses.Add(a);
                }
            }
            await _db.SaveChangesAsync(ct);

            address = await _db.Addresses
                .Include(a => a.Account)
                .Where(a => a.Account.WalletId == walletId)
                .Where(a => !a.IsUsed && !a.IsExposed && a.IsChange)
                .OrderByDescending(a => a.Account.Purpose)
                .ThenBy(a => a.Id)
                .FirstOrDefaultAsync(ct);

            if (address is null)
                throw new InvalidOperationException("No fresh change addresses available");
        }

        return new Script(address.ScriptPubKey);
    }
}

/// <summary>
/// A planned but unsigned transaction with all metadata for review before signing.
/// </summary>
public record TransactionPlan(
    Transaction Transaction,
    Coin[] InputCoins,
    Money EstimatedFee,
    Script ChangeScript,
    IReadOnlyList<ScoredUtxo> SelectedUtxos,
    string[] Warnings);
