using Kompaktor.Credentials;
using NBitcoin;
using WabiSabi.CredentialRequesting;

namespace Kompaktor.Models;

public record RegisterOutputRequest(Dictionary<CredentialType, ICredentialsRequest> CredentialsRequest, TxOut Output):CredentialReissuanceRequest(CredentialsRequest)
{
}