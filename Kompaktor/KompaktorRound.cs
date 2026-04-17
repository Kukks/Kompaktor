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

            var inputs = Events.OfType<KompaktorRoundEventInputRegistered>()
                .Select(x => x.Coin)
                .OrderByDescending(x => x.Amount)
                .ThenBy(x => x.Outpoint.ToBytes(), ByteArrayComparer.Comparer)
                .ToList();
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
            _cachedOutputs = outputs;
            return outputs;
        }
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

    /// <summary>
    /// Computes a fee breakdown for the round's transaction, allowing clients to verify
    /// that no surplus goes to the coordinator (all fees are mining fees).
    /// Returns (totalInputs, totalOutputs, actualFee, expectedMiningFee, surplus).
    /// Surplus should be zero or very small (rounding) — a large surplus indicates
    /// the coordinator is extracting value.
    /// </summary>
    public FeeBreakdown GetFeeBreakdown()
    {
        var feeRate = RoundEventCreated.FeeRate;
        var totalInputs = Inputs.Sum(c => c.Amount);
        var totalOutputs = Outputs.Sum(o => o.Value);
        var actualFee = totalInputs - totalOutputs;

        // Expected fee = sum of per-input fees + sum of per-output fees + tx overhead
        var inputFees = Inputs.Sum(c => c.ScriptPubKey.EstimateFee(feeRate));
        var outputFees = Outputs.Sum(o => feeRate.GetFee(o.GetSerializedSize()));
        // Transaction overhead: version(4) + locktime(4) + segwit marker/flag(0.5) + CompactSize counts
        var txOverhead = 4 + 4 + 1 + CompactSizeSize(Inputs.Count) + CompactSizeSize(Outputs.Count);
        var overheadFee = feeRate.GetFee(txOverhead);
        var expectedMiningFee = inputFees + outputFees + overheadFee;
        var surplus = actualFee - expectedMiningFee;

        return new FeeBreakdown(totalInputs, totalOutputs, actualFee, expectedMiningFee, surplus);
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

    private static int CompactSizeSize(int count) => count switch
    {
        < 253 => 1,
        <= 0xFFFF => 3,
        <= 0xFFFFFF => 5,
        _ => 9
    };
}
