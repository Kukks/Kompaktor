using Kompaktor.Contracts;
using Kompaktor.Wallet.Data;
using NBitcoin;
using NBitcoin.BIP322;

namespace Kompaktor.Scoring;

/// <summary>
/// Decorates an IKompaktorWalletInterface to provide privacy-aware coin selection.
/// GetCoins() returns only UTXOs that the scorer identifies as needing more mixing,
/// ordered by lowest anonymity score first (most in need of privacy improvement).
///
/// This creates the auto-mixing feedback loop:
/// low-privacy coins → coinjoin → recorder → rescored → excluded from candidates
/// </summary>
public class ScoringWalletAdapter : IKompaktorWalletInterface
{
    private readonly IKompaktorWalletInterface _inner;
    private readonly WalletCoinSelector _selector;
    private readonly string _walletId;
    private readonly double? _minEffectiveScore;

    public ScoringWalletAdapter(
        IKompaktorWalletInterface inner,
        WalletCoinSelector selector,
        string walletId,
        double? minEffectiveScore = null)
    {
        _inner = inner;
        _selector = selector;
        _walletId = walletId;
        _minEffectiveScore = minEffectiveScore;
    }

    /// <summary>
    /// Returns only coins that need more coinjoin mixing, ordered by lowest anonymity first.
    /// Falls back to the inner wallet's GetCoins() if scoring fails.
    /// </summary>
    public async Task<Coin[]> GetCoins()
    {
        try
        {
            var candidates = await _selector.GetCoinjoinCandidatesAsync(
                _walletId, _minEffectiveScore);

            if (candidates.Count == 0)
                return [];

            return candidates.Select(c => new Coin(
                new OutPoint(uint256.Parse(c.Utxo.TxId), c.Utxo.OutputIndex),
                new TxOut(Money.Satoshis(c.Utxo.AmountSat), new Script(c.Utxo.ScriptPubKey))
            )).ToArray();
        }
        catch
        {
            // Fall back to unscored selection if scoring fails
            return await _inner.GetCoins();
        }
    }

    public Task<BIP322Signature.Full> GenerateOwnershipProof(string message, Coin[] coins)
        => _inner.GenerateOwnershipProof(message, coins);

    public Task<WitScript> GenerateWitness(Coin coin, Transaction tx, IEnumerable<Coin> txCoins)
        => _inner.GenerateWitness(coin, tx, txCoins);

    public Task<bool?> VerifyUtxo(OutPoint outpoint, TxOut expectedTxOut)
        => _inner.VerifyUtxo(outpoint, expectedTxOut);

    public Task MarkScriptsExposed(IEnumerable<Script> scripts)
        => _inner.MarkScriptsExposed(scripts);
}
