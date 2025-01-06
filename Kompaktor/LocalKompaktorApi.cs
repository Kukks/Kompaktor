using Kompaktor.Contracts;
using Kompaktor.Credentials;
using Kompaktor.Models;

namespace Kompaktor;

using System.Threading.Tasks;

//
// public class KompaktorMessagingApi : IKompaktorPeerCommunicationApi
// {
//     private readonly ILogger _logger;
//     private readonly KompaktorRound _round;
//     private readonly IKompaktorRoundApi _roundApi;
//
//     public KompaktorMessagingApi(ILogger logger, KompaktorRound round, IKompaktorRoundApi roundApi)
//     {
//         _logger = logger;
//         _round = round;
//         _roundApi = roundApi;
//     }
//
//
//     public void Dispose()
//     {
//     }
//
//     public async Task SendMessageAsync(byte[] message, string reasonToWaitFOrLog = "")
//     {
//         _logger.LogDebug($"Sending message {Convert.ToHexString(message)} for {reasonToWaitFOrLog}" );
//         await _roundApi.SendMessage(new MessageRequest(message));
//     }
//     //
//     // public async Task<byte[]> WaitForMessage(byte[] prefix, CancellationToken cancellationToken, string reasonToWaitForLog = "")
//     // {
//     //     var seenMessages = new HashSet<string>(); // To track seen messages
//     //     var prefixHex = Convert.ToHexString(prefix);
//     //
//     //     _logger.LogDebug($"Waiting for message with prefix {prefixHex} for {reasonToWaitForLog}");
//     //     try
//     //     {
//     //
//     //         while (!cancellationToken.IsCancellationRequested)
//     //         {
//     //             var messages = _round.Messages.ToArray(); // Safely copy the messages for iteration
//     //             if (messages.Length != seenMessages.Count)
//     //             {
//     //                 foreach (var message in messages)
//     //                 {
//     //                     var messageHex = Convert.ToHexString(message);
//     //
//     //                     // Log new messages only
//     //                     if (seenMessages.Add(messageHex))
//     //                     {
//     //                         if (message.Length >= prefix.Length && message.Take(prefix.Length).SequenceEqual(prefix))
//     //                         {
//     //                             return message;
//     //                         }
//     //                     }
//     //
//     //
//     //                 }
//     //             }
//     //
//     //             await Task.Delay(100, cancellationToken); // Poll every 100ms
//     //         }
//     //
//     //     }
//     //     catch (OperationCanceledException e)
//     //     {
//     //     }
//     //
//     //     _logger.LogError($"Message not found with prefix {prefixHex}. Seen {seenMessages.Count} messages.");
//     //     
//     //     throw new OperationCanceledException(
//     //         $"Message not found with prefix {prefixHex}. Seen {seenMessages.Count} messages.");
//     // }
//     
//     
//     public async Task<byte[]> WaitForMessage(byte[] prefix, CancellationToken cancellationToken,
//         string reasonToWaitFOrLog, TaskCompletionSource source)
//     {
//         if (prefix == null) throw new ArgumentNullException(nameof(prefix));
//         if (reasonToWaitFOrLog == null) throw new ArgumentNullException(nameof(reasonToWaitFOrLog));
//         try
//         {
//             _logger.LogDebug($"Waiting for message with prefix {Convert.ToHexString(prefix)} for {reasonToWaitFOrLog}" );
//
//             await foreach (var message in Messages(true, cancellationToken, source))
//             {
//                 
//                 _logger.LogDebug($"Received message {Convert.ToHexString(message)} for {reasonToWaitFOrLog}");
//                 if (message.Length >= prefix.Length && message.Take(prefix.Length).SequenceEqual(prefix))
//                 {
//                     return message;
//                 }
//             }
//         }
//         catch (OperationCanceledException e)
//         {
//             _logger.LogError($"Message not found {Convert.ToHexString(prefix)}", e);
//             throw new OperationCanceledException($"Message not found {Convert.ToHexString(prefix)}", e);
//         }
//         catch (Exception e)
//         {
//             _logger.LogError($"Message not found {Convert.ToHexString(prefix)}", e);
//             throw new InvalidOperationException($"Message not found {Convert.ToHexString(prefix)}", e);
//         }
//
//         throw new InvalidOperationException("Message not found");
//     }
//
//     public async IAsyncEnumerable<byte[]> Messages(bool fromStart,
//         [EnumeratorCancellation] CancellationToken cancellationToken, TaskCompletionSource source)
//     {
//         var channel = Channel.CreateUnbounded<byte[]>();
//         var reader = _round.Subscribe(cancellationToken);
//         Console.WriteLine("Subscribed to NewMessage event");
//         source.SetResult();
//         // If fromStart is true, iterate through existing messages
//         if (fromStart)
//         {
//             foreach (var message in _round.Messages)
//             {
//                 channel.Writer.TryWrite(message);
//             }
//         }
//
//         _ = Task.Run(async () =>
//         {
//             await foreach (var message in reader.ReadAllAsync(cancellationToken))
//             {
//                 if(message is KompaktorRoundEventMessage eventMessage)
//                     await channel.Writer.WriteAsync(eventMessage.Request.Message, cancellationToken);
//             }
//         }, cancellationToken);
//
//         // Subscribe to the NewMessage event
//
//
//         while (await channel.Reader.WaitToReadAsync(cancellationToken))
//         {
//             while (channel.Reader.TryRead(out var item))
//             {
//                 yield return item;
//             }
//         }
//     }
// }

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