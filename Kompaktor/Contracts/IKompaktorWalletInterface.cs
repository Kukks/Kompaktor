using NBitcoin;
using NBitcoin.BIP322;

namespace Kompaktor.Contracts;

public interface IKompaktorWalletInterface
{
    public Task<Coin[]> GetCoins();
    public Task<BIP322Signature.Full> GenerateOwnershipProof(string message,Coin[] coins);  
    public Task<WitScript> GenerateWitness(Coin coin, Transaction tx, IEnumerable<Coin> txCoins);
}