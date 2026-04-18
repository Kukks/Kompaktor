using Kompaktor.Credentials;
using Kompaktor.Models;

namespace Kompaktor.Contracts;

public interface IKompaktorRoundApi
{
    public Task<KompaktorRoundEvent> GetEvents(string lastEventId);
    Task<RoundInfoResponse> GetRoundInfo();
    Task<KompaktorRoundEventMessage> SendMessage(MessageRequest request);
    Task<InputRegistrationQuoteResponse> PreRegisterInput(RegisterInputQuoteRequest quoteRequest);
    Task<KompaktorRoundEventInputRegistered> RegisterInput(RegisterInputRequest quoteRequest);
    bool Connect(string secret);
    void Disconnect(string secret);
    Task<KompaktorRoundCredentialReissuanceResponse> ReissueCredentials(CredentialReissuanceRequest request);
    Task<KompaktorRoundEventOutputRegistered> RegisterOutput(RegisterOutputRequest request);
    Task<KompaktorRoundEventSignaturePosted> Sign(SignRequest request);
    Task ReadyToSign(ReadyToSignRequest request);

    // Batch operations — reduce round-trips for clients with multiple inputs
    Task<BatchResponse<InputRegistrationQuoteResponse>> BatchPreRegisterInput(BatchPreRegisterInputRequest request);
    Task<BatchResponse<KompaktorRoundEventInputRegistered>> BatchRegisterInput(BatchRegisterInputRequest request);
    Task<BatchResponse<KompaktorRoundEventSignaturePosted>> BatchSign(BatchSignRequest request);
    Task BatchReadyToSign(BatchReadyToSignRequest request);
}