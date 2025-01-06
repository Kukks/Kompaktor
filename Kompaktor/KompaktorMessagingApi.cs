using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Kompaktor.Contracts;
using Kompaktor.Models;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace Kompaktor;

public class KompaktorMessagingApi : IKompaktorPeerCommunicationApi
{
    private readonly ILogger _logger;
    private readonly KompaktorRound _round;
    private readonly IKompaktorRoundApi _roundApi;

    // Centralized buffer to store all messages
    private readonly ConcurrentHashSet<byte[]> _seenMessages = new();
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _lock = new(1, 1);

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

    private event EventHandler<byte[]>? NewMessage;

    private async Task RoundOnNewEvent(object sender, KompaktorRoundEvent args)
    {
        await _lock.WaitAsync();
        try
        {
            if (args is KompaktorRoundEventMessage eventMessage)
            {
                var msg = eventMessage.Request.Message;
                _seenMessages.Add(msg);
                NewMessage?.Invoke(this, msg);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
    
    private async Task LockWaitRunExecute(Func<Task> action)
    {
        Stopwatch sw = new();
        sw.Start();
        await _lock.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            _lock.Release();
            sw.Stop();
        }
    }


    public async Task<byte[]> WaitForMessage(byte[] prefix, CancellationToken cancellationToken,
        string reasonToWaitForLog)
    {
        
        var tcs = new TaskCompletionSource<byte[]>();
        NewMessage += RoundOnNewEvent2;

        void RoundOnNewEvent2(object? sender, byte[] msg)
        {
            if (msg.Length >= prefix.Length && msg.Take(prefix.Length).SequenceEqual(prefix))
            {
                if (!tcs.TrySetResult(msg))
                {
                    _logger.LogDebug($"Failed to set result for message {Convert.ToHexString(msg)}");
                }
            }
            // else
            // {
            //     _logger.LogDebug($"Received message {Convert.ToHexString(msg)} but it does not match prefix {Convert.ToHexString(prefix)}");
            //     
            // }
        }

        byte[]? match;
        await LockWaitRunExecute(() =>
        {
            match = _seenMessages.FirstOrDefault(msg =>
                msg.Length >= prefix.Length && msg.Take(prefix.Length).SequenceEqual(prefix));
            if (match != null)
            {
                tcs.TrySetResult(match);
            }
            return Task.CompletedTask;
        });

        _logger.LogDebug($"Waiting for message for {reasonToWaitForLog} with prefix {Convert.ToHexString(prefix)}");
        try
        {
            return await tcs.Task.WithCancellation(cancellationToken);
        }
        finally
        {
            NewMessage -= RoundOnNewEvent2;
        }
    }

    public async Task SendMessageAsync(byte[] message, string reasonToWaitForLog = "")
    {
        _logger.LogDebug($"Sending message for {reasonToWaitForLog} {Convert.ToHexString(message)}");
        await _roundApi.SendMessage(new MessageRequest(message));
    }

    public async IAsyncEnumerable<byte[]> Messages(bool fromStart,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<byte[]>();

        async Task RoundOnNewEvent2(object sender, KompaktorRoundEvent args)
        {
            if (args is KompaktorRoundEventMessage eventMessage)
            {
                var msg = eventMessage.Request.Message;
                await channel.Writer.WriteAsync(msg, cancellationToken);
            }
        }

        byte[][]? copied = null;
        await LockWaitRunExecute(() =>
        {
            _round.NewEvent += RoundOnNewEvent2;
            copied = _seenMessages.ToArray();
            return Task.CompletedTask;
        });
        try
        {
            if (fromStart)
            {
                foreach (var message in copied)
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