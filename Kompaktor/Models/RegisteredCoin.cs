using NBitcoin;
using WabiSabi.Crypto.ZeroKnowledge;

namespace Kompaktor.Models;

public class RegisteredCoin
{
    private Coin Coin { get; }
    private KompaktorIdentity Identity { get; }
    public Credential IssuedCredentials { get; set; }
}