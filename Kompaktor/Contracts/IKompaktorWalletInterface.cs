using NBitcoin;
using NBitcoin.BIP322;

namespace Kompaktor.Contracts;

public interface IKompaktorWalletInterface
{
    public Task<Coin[]> GetCoins(CancellationToken ct = default);
    public Task<BIP322Signature.Full> GenerateOwnershipProof(string message, Coin[] coins, CancellationToken ct = default);
    public Task<WitScript> GenerateWitness(Coin coin, Transaction tx, IEnumerable<Coin> txCoins, CancellationToken ct = default);

    /// <summary>
    /// Verifies that a coin exists in the UTXO set with the claimed scriptPubKey and amount.
    /// Clients with full node access should implement this to detect fabricated inputs
    /// from a malicious coordinator. Returns null if UTXO verification is not available
    /// (SPV/light client), true if verified, false if the UTXO doesn't match.
    /// Default implementation returns null (no verification capability).
    /// </summary>
    Task<bool?> VerifyUtxo(OutPoint outpoint, TxOut expectedTxOut, CancellationToken ct = default) => Task.FromResult<bool?>(null);

    /// <summary>
    /// Called when a round reaches a terminal state (Completed or Failed) after output addresses
    /// were disclosed to the coordinator. The wallet should mark these scripts as burned and never
    /// reuse them, to prevent cross-round address linking that enables intersection attacks.
    /// Default implementation is a no-op for backwards compatibility.
    /// </summary>
    Task MarkScriptsExposed(IEnumerable<Script> scripts, CancellationToken ct = default) => Task.CompletedTask;
}