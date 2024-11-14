using System.Collections;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Kompaktor.Contracts;
using Kompaktor.Credentials;
using Kompaktor.Mapper;
using Kompaktor.Models;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace Kompaktor;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;


public class ConcurrentHashSet<T> : ICollection<T>, IReadOnlyCollection<T>
{
    // This class wraps our keys and serves only to provide support for the special case
    // where the item is null which is supported in a HashSet<T> but not as a key in a dictionary
    private class Item
    {
        public Item(T value)
        {
            Value = value;
        }

        public T Value { get; }
    }

    // We also have to wrap the comparer since the generic types of the 
    // item and underlying dictionary are different
    private class ItemComparer : IEqualityComparer<Item>
    {
        private readonly IEqualityComparer<T> _comparer;

        public ItemComparer(IEqualityComparer<T> comparer)
        {
            _comparer = comparer;
        }

        public bool Equals(Item x, Item y)
        {
            return _comparer.Equals(x.Value, y.Value);
        }

        public int GetHashCode(Item obj)
        {
            return _comparer.GetHashCode(obj.Value);
        }
    }

    private readonly ConcurrentDictionary<Item, byte> _dictionary;

    public ConcurrentHashSet()
    {
        _dictionary = new ConcurrentDictionary<Item, byte>(new ItemComparer(EqualityComparer<T>.Default));
    }

    public ConcurrentHashSet(IEnumerable<T> collection)
    {
        _dictionary = new ConcurrentDictionary<Item, byte>(
            collection.Select(x => new KeyValuePair<Item, byte>(new Item(x), Byte.MinValue)),
            new ItemComparer(EqualityComparer<T>.Default));
    }

    public ConcurrentHashSet(IEnumerable<T> collection, IEqualityComparer<T> comparer)
    {
        _dictionary = new ConcurrentDictionary<Item, byte>(
            collection.Select(x => new KeyValuePair<Item, byte>(new Item(x), Byte.MinValue)),
            new ItemComparer(comparer));
    }

    public ConcurrentHashSet(IEqualityComparer<T> comparer)
    {
        _dictionary = new ConcurrentDictionary<Item, byte>(new ItemComparer(comparer));
    }

    public bool Add(T item) => _dictionary.TryAdd(new Item(item), Byte.MinValue);

    // IEnumerable, IEnumerable<T>

    public IEnumerator<T> GetEnumerator() => _dictionary.Keys.Select(x => x.Value).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // IReadOnlyCollection<T>

    public int Count => _dictionary.Count;

    // ICollection<T>

    void ICollection<T>.Add(T item) => ((IDictionary<Item, byte>) _dictionary).Add(new Item(item), Byte.MinValue);

    public void Clear() => _dictionary.Clear();

    public bool Contains(T item) => _dictionary.ContainsKey(new Item(item));

    public void CopyTo(T[] array, int arrayIndex) => 
        _dictionary.Keys.Select(x => x.Value).ToArray().CopyTo(array, arrayIndex);

    public bool Remove(T item)
    {
        byte value;
        return _dictionary.TryRemove(new Item(item), out value);
    }

    public bool IsReadOnly => false;
}

public class KompaktorMessagingApi : IKompaktorPeerCommunicationApi
{
    private readonly ILogger _logger;
    private readonly KompaktorRound _round;
    private readonly IKompaktorRoundApi _roundApi;

    // Centralized buffer to store all messages
    private readonly ConcurrentHashSet<byte[]> _seenMessages = new();
    private readonly CancellationTokenSource _cts;

    public KompaktorMessagingApi(ILogger logger, KompaktorRound round, IKompaktorRoundApi roundApi)
    {
        _logger = logger;
        _round = round;
        _roundApi = roundApi;

        _cts = new CancellationTokenSource();
        // Start a single subscription to populate the message buffer
        round.NewEvent += RoundOnNewEvent;
        foreach (var roundMessage in round.Messages)
        {
            _seenMessages.Add(roundMessage);
        }
    }

    private Task RoundOnNewEvent(object sender, KompaktorRoundEvent args)
    {
        if (args is KompaktorRoundEventMessage eventMessage)
        {
            var msg = eventMessage.Request.Message;
            _seenMessages.Add(msg);
        }

        return Task.CompletedTask;
    }



    public async Task<byte[]> WaitForMessage(byte[] prefix, CancellationToken cancellationToken, string reasonToWaitForLog, TaskCompletionSource tcs2)
    {
        var tcs = new TaskCompletionSource<byte[]>();
        _round.NewEvent += RoundOnNewEvent2;
tcs2.SetResult();
        Task RoundOnNewEvent2(object sender, KompaktorRoundEvent args)
        {
            if (args is KompaktorRoundEventMessage eventMessage)
            {
                var msg = eventMessage.Request.Message;
                if (msg.Length >= prefix.Length && msg.Take(prefix.Length).SequenceEqual(prefix))
                {
                    tcs.SetResult(msg);
                }
            }

            return Task.CompletedTask;
        }

        _logger.LogDebug($"Waiting for message with prefix {Convert.ToHexString(prefix)} for {reasonToWaitForLog}");
        try
        {
            var match = _seenMessages.FirstOrDefault(msg => msg.Length >= prefix.Length && msg.Take(prefix.Length).SequenceEqual(prefix));
            if (match != null)
            {
                tcs.SetResult(match);
            }
            return await tcs.Task.WithCancellation(cancellationToken);
        }
        finally
        {
            _round.NewEvent -= RoundOnNewEvent2;

        }
    }

    public async Task SendMessageAsync(byte[] message, string reasonToWaitForLog = "")
    {
        _logger.LogDebug($"Sending message {Convert.ToHexString(message)} for {reasonToWaitForLog}");
        await _roundApi.SendMessage(new MessageRequest(message));
    }

    public async IAsyncEnumerable<byte[]> Messages(bool fromStart, CancellationToken cancellationToken, TaskCompletionSource tcs)
    {
        var channel = Channel.CreateUnbounded<byte[]>();
        _round.NewEvent += RoundOnNewEvent2;
        tcs.SetResult();
        async Task RoundOnNewEvent2(object sender, KompaktorRoundEvent args)
        {
            if (args is KompaktorRoundEventMessage eventMessage)
            {
                var msg = eventMessage.Request.Message;
                await channel.Writer.WriteAsync(msg, cancellationToken);
            }

        }

        try
        {
            if (fromStart)
            {
                foreach (var message in _seenMessages)
                {
                    await channel.Writer.WriteAsync(message, cancellationToken);
                }
            }
            await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return message;
            }
        }
        finally
        {
            _round.NewEvent -= RoundOnNewEvent2;

        }
    }


    public void Dispose()
    {
        _cts.Cancel();
        
    }
}


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