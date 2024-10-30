using Kompaktor.Credentials;
using Kompaktor.Models;
using WabiSabi.CredentialRequesting;

namespace Kompaktor.Contracts;

public interface IKompaktorRoundApi
{
    Task<KompaktorRoundEventMessage> SendMessage(MessageRequest request);
    Task<InputRegistrationQuoteResponse> PreRegisterInput(RegisterInputQuoteRequest quoteRequest);
    Task<KompaktorRoundEventInputRegistered> RegisterInput(RegisterInputRequest quoteRequest);
    Task<KompaktorRoundCredentialReissuanceResponse> ReissueCredentials(CredentialReissuanceRequest request);
    Task<KompaktorRoundEventOutputRegistered> RegisterOutput(RegisterOutputRequest request);
    Task<KompaktorRoundEventSignaturePosted> Sign(SignRequest request);
    Task ReadyToSign(ReadyToSignRequest request);
}

public record RegisterInputRequest(string Secret, ICredentialsRequest CredentialsRequest);
public record ReadyToSignRequest(string Secret);

public record InputRegistrationQuoteResponse(string Secret, CredentialsResponse CredentialsResponse, long CredentialAmount);