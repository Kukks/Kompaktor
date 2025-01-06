using WabiSabi.CredentialRequesting;

namespace Kompaktor.Contracts;

public record InputRegistrationQuoteResponse(string Secret, CredentialsResponse CredentialsResponse, long CredentialAmount);