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

    public async Task<KompaktorRoundCredentialReissuanceResponse> ReissueCredentials(CredentialReissuanceRequest request)
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
}