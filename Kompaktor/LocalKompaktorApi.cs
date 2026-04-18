using Kompaktor.Contracts;
using Kompaktor.Credentials;
using Kompaktor.Models;

namespace Kompaktor;

public class LocalKompaktorApi : IKompaktorRoundApi
{
    private readonly KompaktorRoundOperator _roundOperator;

    public LocalKompaktorApi(KompaktorRoundOperator roundOperator)
    {
        _roundOperator = roundOperator;
    }

    public async Task<KompaktorRoundEvent> GetEvents(string lastEventId)
    {
        return await _roundOperator.GetEvents(lastEventId);
    }

    public Task<RoundInfoResponse> GetRoundInfo()
    {
        return Task.FromResult(RoundInfoResponse.FromCreatedEvent(_roundOperator.RoundEventCreated));
    }

    public async Task<KompaktorRoundEventMessage> SendMessage(MessageRequest request)
    {
        return await _roundOperator.SendMessage(request);
    }

    public async Task<InputRegistrationQuoteResponse> PreRegisterInput(RegisterInputQuoteRequest quoteRequest)
    {
        return await _roundOperator.PreRegisterInput(quoteRequest);
    }

    public async Task<KompaktorRoundEventInputRegistered> RegisterInput(RegisterInputRequest quoteRequest)
    {
        return await _roundOperator.RegisterInput(quoteRequest);
    }

    public bool Connect(string secret) => _roundOperator.Connect(secret);
    public void Disconnect(string secret) => _roundOperator.Disconnect(secret);

    public async Task<KompaktorRoundCredentialReissuanceResponse> ReissueCredentials(
        CredentialReissuanceRequest request)
    {
        return await _roundOperator.ReissueCredentials(request);
    }

    public async Task<KompaktorRoundEventOutputRegistered> RegisterOutput(RegisterOutputRequest request)
    {
        return await _roundOperator.RegisterOutput(request);
    }

    public async Task<KompaktorRoundEventSignaturePosted> Sign(SignRequest request)
    {
        return await _roundOperator.Sign(request);
    }

    public async Task ReadyToSign(ReadyToSignRequest request)
    {
        await _roundOperator.ReadyToSign(request);
    }

    public async Task<BatchResponse<InputRegistrationQuoteResponse>> BatchPreRegisterInput(
        BatchPreRegisterInputRequest request)
    {
        return await _roundOperator.BatchPreRegisterInput(request);
    }

    public async Task<BatchResponse<KompaktorRoundEventInputRegistered>> BatchRegisterInput(
        BatchRegisterInputRequest request)
    {
        return await _roundOperator.BatchRegisterInput(request);
    }

    public async Task<BatchResponse<KompaktorRoundEventSignaturePosted>> BatchSign(BatchSignRequest request)
    {
        return await _roundOperator.BatchSign(request);
    }

    public async Task BatchReadyToSign(BatchReadyToSignRequest request)
    {
        await _roundOperator.BatchReadyToSign(request);
    }
}