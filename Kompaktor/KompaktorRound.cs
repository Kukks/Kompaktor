using Kompaktor.Models;
using Kompaktor.Utils;
using NBitcoin;

namespace Kompaktor;

public class KompaktorRound : IDisposable
{
    private readonly LinkedList<KompaktorRoundEvent> _events = new();

    protected LinkedList<KompaktorRoundEvent> Events
    {
        get
        {
            lock (lockObj)
            {
                return new(_events);
            }
        }
    }


    public EventHandler<KompaktorRoundEvent>? NewEvent;
    public KompaktorStatus Status => Events.OfType<KompaktorRoundEventStatusUpdate>().Last().Status;
    // public KompaktorRoundEvent[] Events => _events.ToArray();

    public List<Coin> Inputs =>
        Events.OfType<KompaktorRoundEventInputRegistered>()
            .Select(x => x.Coin)
            .OrderByDescending(x => x.Amount)
            .ThenBy(x => x.Outpoint.ToBytes(), ByteArrayComparer.Comparer)
            .ToList();

    public List<TxOut> Outputs =>
        Events.OfType<KompaktorRoundEventOutputRegistered>().Select(x => x.Request.Output)
            .GroupBy(x => x.ScriptPubKey)
            .Select(x => new TxOut(x.Sum(y => y.Value), x.Key))
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.ScriptPubKey.ToBytes(true), ByteArrayComparer.Comparer)
            .ToList();

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

    public virtual void Dispose()
    {
        NewEvent = null;
    }


    public Transaction GetTransaction(Network network)
    {
        var transaction = Transaction.Create(network);
        foreach (var input in Inputs) transaction.Inputs.Add(input.Outpoint);

        foreach (var output in Outputs) transaction.Outputs.Add(output);

        // txBuilder.SendFees(change);
        // var psbt = txBuilder.BuildPSBT(true);

        foreach (var sigs in _events.OfType<KompaktorRoundEventSignaturePosted>())
        {
            transaction.Inputs.FindIndexedInput(sigs.Request.OutPoint).WitScript = sigs.Request.Witness;
        }

        return transaction;
    }

    private object lockObj = new object();

    protected virtual T AddEvent<T>(T @event) where T : KompaktorRoundEvent
    {
        lock (lockObj)
        {
            _events.AddLast(@event);
        }

        NewEvent?.Invoke(this, @event);
        return @event;
    }

    public KompaktorRoundEventCreated RoundEventCreated
    {
        get { return Events.OfType<KompaktorRoundEventCreated>().Single(); }
    }
}