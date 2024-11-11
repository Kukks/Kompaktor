using WabiSabi.CredentialRequesting;

namespace Kompaktor.Contracts;

public record RegisterInputRequest(string Secret, ICredentialsRequest CredentialsRequest);