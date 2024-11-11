using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Kompaktor.Contracts;
using Kompaktor.Credentials;
using Kompaktor.Models;

namespace Kompaktor;

public class KompaktorMessagingApi : IKompaktorPeerCommunicationApi
{
    private readonly KompaktorRound _round;
    private readonly IKompaktorRoundApi _roundApi;

    public KompaktorMessagingApi(KompaktorRound round, IKompaktorRoundApi roundApi)
    {
        _round = round;
        _roundApi = roundApi;
    }
    

    public void Dispose()
    {
    }

    public async Task SendMessageAsync(byte[] message)
    {
        await _roundApi.SendMessage(new MessageRequest(message));
    }

    public async IAsyncEnumerable<byte[]> Messages([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Create a channel to hold incoming messages
        var channel = Channel.CreateUnbounded<byte[]>();

        // Event handler to write new messages to the channel
        void NewEvent(object? sender, KompaktorRoundEvent e)
        {
            if (e is KompaktorRoundEventMessage messageEvent)
            {
                // Write the new message to the channel
                channel.Writer.TryWrite(messageEvent.Request.Message);
            }
        }

        // Subscribe to the NewEvent
        _round.NewEvent += NewEvent;

        try
        {
            // Yield existing messages
            foreach (var messageEvent in _round.Messages)
            {
                yield return messageEvent;
            }

            // Continuously read from the channel and yield new messages
            while (await channel.Reader.WaitToReadAsync(cts.Token))
            {
                while (channel.Reader.TryRead(out var message))
                {
                    yield return message;
                }
            }
        }
        finally
        {
            // Unsubscribe from the event and complete the channel
            _round.NewEvent -= NewEvent;
            channel.Writer.TryComplete();
        }
    }
}

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
}