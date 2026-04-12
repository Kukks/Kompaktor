using System.Collections.Concurrent;
using Kompaktor.Models;
using Kompaktor.Utils;
using NBitcoin;

namespace Kompaktor;

public class KompaktorRound : IDisposable
{
    private readonly ConcurrentQueue<KompaktorRoundEvent> _events = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Cached derived state — invalidated on each new event
    private volatile KompaktorRoundEvent[]? _cachedEvents;
    private volatile List<Coin>? _cachedInputs;
    private volatile List<TxOut>? _cachedOutputs;
    private volatile int _cachedStatus = -1; // -1 = not cached
    private volatile KompaktorRoundEventCreated? _cachedCreated;
    private volatile int _cachedSignatureCount;

    protected IEnumerable<KompaktorRoundEvent> Events
    {
        get
        {
            var cached = _cachedEvents;
            if (cached is not null) return cached;

            _lock.Wait();
            try
            {
                cached = _cachedEvents;
                if (cached is not null) return cached;

                cached = _events.ToArray();
                _cachedEvents = cached;
                return cached;
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    public event AsyncEventHandler<KompaktorRoundEvent>? NewEvent;

    public KompaktorStatus Status
    {
        get
        {
            var cached = _cachedStatus;
            if (cached >= 0) return (KompaktorStatus)cached;

            var status = Events.OfType<KompaktorRoundEventStatusUpdate>().Last().Status;
            _cachedStatus = (int)status;
            return status;
        }
    }

    public List<Coin> Inputs
    {
        get
        {
            var cached = _cachedInputs;
            if (cached is not null) return cached;

            // Canonical sort first, then apply transcript-seeded shuffle if in signing phase.
            // The canonical sort ensures all clients start from the same base ordering
            // before the deterministic shuffle is applied.
            var inputs = Events.OfType<KompaktorRoundEventInputRegistered>()
                .Select(x => x.Coin)
                .OrderByDescending(x => x.Amount)
                .ThenBy(x => x.Outpoint.ToBytes(), ByteArrayComparer.Comparer)
                .ToList();

            // Apply transcript-seeded shuffle in signing phase for anti-equivocation
            if (Status >= KompaktorStatus.Signing)
            {
                var seed = GetTranscriptSeed();
                if (seed is not null)
                    TranscriptShuffler.Shuffle(inputs, seed);
            }

            _cachedInputs = inputs;
            return inputs;
        }
    }

    public List<TxOut> Outputs
    {
        get
        {
            var cached = _cachedOutputs;
            if (cached is not null) return cached;

            var outputs = Events.OfType<KompaktorRoundEventOutputRegistered>().Select(x => x.Request.Output)
                .GroupBy(x => x.ScriptPubKey)
                .Select(x => new TxOut(x.Sum(y => y.Value), x.Key))
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.ScriptPubKey.ToBytes(true), ByteArrayComparer.Comparer)
                .ToList();

            // Apply transcript-seeded shuffle in signing phase for anti-equivocation
            if (Status >= KompaktorStatus.Signing)
            {
                var seed = GetTranscriptSeed();
                if (seed is not null)
                    TranscriptShuffler.Shuffle(outputs, seed);
            }

            _cachedOutputs = outputs;
            return outputs;
        }
    }

    /// <summary>
    /// Computes the transcript seed from all input and output registration event IDs.
    /// Returns null if the round hasn't reached the signing phase yet.
    /// </summary>
    private byte[]? GetTranscriptSeed()
    {
        var registrationEventIds = Events
            .Where(e => e is KompaktorRoundEventInputRegistered or KompaktorRoundEventOutputRegistered)
            .Select(e => e.Id)
            .ToList();

        return registrationEventIds.Count > 0
            ? TranscriptShuffler.ComputeTranscriptSeed(registrationEventIds)
            : null;
    }

    public int SignatureCount => _cachedSignatureCount;

    public DateTimeOffset InputPhaseEnd =>
        Events.OfType<KompaktorRoundEventStatusUpdate>()
            .FirstOrDefault(e => e.Status > KompaktorStatus.InputRegistration)?.Timestamp ??
        RoundEventCreated.Timestamp + RoundEventCreated.InputTimeout;

    public DateTimeOffset OutputPhaseEnd =>
        Events.OfType<KompaktorRoundEventStatusUpdate>()
            .FirstOrDefault(e => e.Status > KompaktorStatus.OutputRegistration)?.Timestamp ?? InputPhaseEnd +
        RoundEventCreated.OutputTimeout;

    public DateTimeOffset SigningPhaseEnd =>
        Events.OfType<KompaktorRoundEventStatusUpdate>().FirstOrDefault(e => e.Status > KompaktorStatus.Signing)
            ?.Timestamp ?? OutputPhaseEnd + RoundEventCreated.SigningTimeout;

    public byte[][] Messages =>
        Events.OfType<KompaktorRoundEventMessage>().Select(x => x.Request.Message).ToArray();

    /// <summary>
    /// Returns events since the given checkpoint ID (exclusive).
    /// If checkpointId is null, returns all events.
    /// </summary>
    public IReadOnlyList<KompaktorRoundEvent> GetEventsSince(string? checkpointId)
    {
        var allEvents = Events.ToArray();
        if (checkpointId is null)
            return allEvents;

        var found = false;
        var result = new List<KompaktorRoundEvent>();
        foreach (var evt in allEvents)
        {
            if (found)
                result.Add(evt);
            else if (evt.Id == checkpointId)
                found = true;
        }

        // If checkpoint not found, return all events (client may have stale checkpoint)
        return found ? result : allEvents;
    }

    public virtual void Dispose()
    {
        NewEvent = null;
    }

    public Transaction GetTransaction(Network network)
    {
        var transaction = Transaction.Create(network);
        foreach (var input in Inputs) transaction.Inputs.Add(input.Outpoint);
        foreach (var output in Outputs) transaction.Outputs.Add(output);

        foreach (var sigs in _events.OfType<KompaktorRoundEventSignaturePosted>())
        {
            transaction.Inputs.FindIndexedInput(sigs.Request.OutPoint).WitScript = sigs.Request.Witness;
        }

        return transaction;
    }

    private void InvalidateCache(KompaktorRoundEvent @event)
    {
        _cachedEvents = null;

        if (@event is KompaktorRoundEventStatusUpdate)
            _cachedStatus = -1;
        else if (@event is KompaktorRoundEventInputRegistered)
            _cachedInputs = null;
        else if (@event is KompaktorRoundEventOutputRegistered)
            _cachedOutputs = null;
        else if (@event is KompaktorRoundEventSignaturePosted)
            Interlocked.Increment(ref _cachedSignatureCount);
    }

    protected virtual async Task<T> AddEvent<T>(T @event) where T : KompaktorRoundEvent
    {
        await _lock.WaitAsync();
        try
        {
            _events.Enqueue(@event);
            InvalidateCache(@event);
        }
        finally
        {
            _lock.Release();
        }

        await NewEvent.InvokeIfNotNullAsync(this, @event);
        return @event;
    }

    public KompaktorRoundEventCreated RoundEventCreated
    {
        get
        {
            var cached = _cachedCreated;
            if (cached is not null) return cached;
            cached = Events.OfType<KompaktorRoundEventCreated>().Single();
            _cachedCreated = cached;
            return cached;
        }
    }
}
