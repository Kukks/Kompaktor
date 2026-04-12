using NBitcoin;
using NBitcoin.BIP322;

namespace Kompaktor.Contracts;

public interface IKompaktorWalletInterface
{
    public Task<Coin[]> GetCoins();
    public Task<BIP322Signature.Full> GenerateOwnershipProof(string message,Coin[] coins);
    public Task<WitScript> GenerateWitness(Coin coin, Transaction tx, IEnumerable<Coin> txCoins);

    /// <summary>
    /// Verifies that a coin exists in the UTXO set with the claimed scriptPubKey and amount.
    /// Clients with full node access should implement this to detect fabricated inputs
    /// from a malicious coordinator. Returns null if UTXO verification is not available
    /// (SPV/light client), true if verified, false if the UTXO doesn't match.
    /// Default implementation returns null (no verification capability).
    /// </summary>
    Task<bool?> VerifyUtxo(OutPoint outpoint, TxOut expectedTxOut) => Task.FromResult<bool?>(null);
}