using WabiSabi.CredentialRequesting;

namespace Kompaktor.Credentials;

public record CredentialReissuanceRequest(Dictionary<CredentialType, ICredentialsRequest> CredentialsRequest)
{
}