using System.Threading.Channels;

namespace Kompaktor.Web;

/// <summary>
/// Lightweight in-memory broadcast bus for SSE dashboard updates.
/// State-changing operations publish event types (e.g. "utxos", "rounds"),
/// and each connected SSE client receives them via a dedicated Channel reader.
/// </summary>
public class DashboardEventBus
{
    private readonly List<Channel<string>> _subscribers = new();
    private readonly Lock _lock = new();

    /// <summary>
    /// Publish an event type to all connected SSE clients.
    /// Non-blocking: drops the message for slow consumers.
    /// </summary>
    public void Publish(string eventType)
    {
        lock (_lock)
        {
            for (var i = _subscribers.Count - 1; i >= 0; i--)
            {
                if (!_subscribers[i].Writer.TryWrite(eventType))
                {
                    // Slow consumer — complete and remove
                    _subscribers[i].Writer.TryComplete();
                    _subscribers.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// Subscribe a new SSE client. Returns the reader side of a bounded channel.
    /// The caller should read from this until cancellation and then call Unsubscribe.
    /// </summary>
    public ChannelReader<string> Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        lock (_lock)
        {
            _subscribers.Add(channel);
        }

        return channel.Reader;
    }

    /// <summary>
    /// Remove a subscriber (called when SSE connection closes).
    /// </summary>
    public void Unsubscribe(ChannelReader<string> reader)
    {
        lock (_lock)
        {
            var idx = _subscribers.FindIndex(c => c.Reader == reader);
            if (idx >= 0)
            {
                _subscribers[idx].Writer.TryComplete();
                _subscribers.RemoveAt(idx);
            }
        }
    }

    public int SubscriberCount
    {
        get { lock (_lock) { return _subscribers.Count; } }
    }
}
