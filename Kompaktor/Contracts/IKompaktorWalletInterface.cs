using NBitcoin;
using NBitcoin.BIP322;

namespace Kompaktor.Contracts;

public interface IKompaktorWalletInterface
{
    public Task<Coin[]> GetCoins();
    public Task<BIP322Signature.Full> GenerateOwnershipProof(string message,Coin[] coins);
    public Task<WitScript> GenerateWitness(Coin coin, Transaction tx, IEnumerable<Coin> txCoins);

    /// <summary>
    /// Called when a round fails after output addresses were disclosed to the coordinator.
    /// The wallet should mark these scripts as burned and never reuse them, to prevent
    /// cross-round address linking that enables intersection attacks.
    /// Default implementation is a no-op for backwards compatibility.
    /// </summary>
    Task MarkScriptsExposed(IEnumerable<Script> scripts) => Task.CompletedTask;
}