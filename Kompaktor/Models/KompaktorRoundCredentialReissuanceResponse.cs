// using WabiSabi.CredentialRequesting;
// using WalletWasabi.WabiSabi.Client.CredentialDependencies;
//
// namespace WalletWasabi.Crypto;
//

using Kompaktor.Credentials;
using WabiSabi.CredentialRequesting;

namespace Kompaktor.Models;

public record KompaktorRoundCredentialReissuanceResponse(Dictionary<CredentialType, CredentialsResponse> Credentials)
{
}