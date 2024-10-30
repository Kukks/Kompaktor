using NBitcoin.BIP322;
using WabiSabi.CredentialRequesting;

namespace Kompaktor.Models;

public record RegisterInputQuoteRequest
{
    public BIP322Signature.Full Signature { get; init; }
    public ICredentialsRequest CredentialsRequest { get; init; }
}