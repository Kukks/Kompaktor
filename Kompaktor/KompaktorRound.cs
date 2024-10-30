using Kompaktor.Models;
using Kompaktor.Utils;
using NBitcoin;

namespace Kompaktor;

public class KompaktorRound : IDisposable
{
    protected readonly LinkedList<KompaktorRoundEvent> _events = new();

    public EventHandler<KompaktorRoundEvent>? NewEvent;
    public KompaktorStatus Status => _events.OfType<KompaktorRoundEventStatusUpdate>().Last().Status;
    // public KompaktorRoundEvent[] Events => _events.ToArray();

    public List<Coin> Inputs =>
        _events.OfType<KompaktorRoundEventInputRegistered>()
            .Select(x => x.Coin)
            .OrderByDescending(x => x.Amount)
            .ThenBy(x => x.Outpoint.ToBytes(), ByteArrayComparer.Comparer)
            .ToList();

    public List<TxOut> Outputs =>
        _events.OfType<KompaktorRoundEventOutputRegistered>().Select(x => x.Request.Output)
            .GroupBy(x => x.ScriptPubKey)
            .Select(x => new TxOut(x.Sum(y => y.Value), x.Key))
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.ScriptPubKey.ToBytes(true), ByteArrayComparer.Comparer)
            .ToList();
    public DateTimeOffset InputPhaseEnd=>
        _events.OfType<KompaktorRoundEventStatusUpdate>()
            .FirstOrDefault(e => e.Status > KompaktorStatus.InputRegistration)?.Timestamp ??
        RoundEventCreated.Timestamp + RoundEventCreated.InputTimeout;

    public DateTimeOffset OutputPhaseEnd=>
        _events.OfType<KompaktorRoundEventStatusUpdate>()
            .FirstOrDefault(e => e.Status > KompaktorStatus.OutputRegistration)?.Timestamp ?? InputPhaseEnd +
        RoundEventCreated.OutputTimeout;

    public DateTimeOffset SigningPhaseEnd =>
        _events.OfType<KompaktorRoundEventStatusUpdate>().FirstOrDefault(e => e.Status > KompaktorStatus.Signing)
            ?.Timestamp ?? OutputPhaseEnd +  RoundEventCreated.SigningTimeout;

    public virtual void Dispose()
    {
        NewEvent = null;
    }


    public Transaction GetTransaction(Network network)
    {
        var transaction = Transaction.Create(network);
        foreach (var input in Inputs) transaction.Inputs.Add(input.Outpoint);

        foreach (var output in Outputs) transaction.Outputs.Add(output);
        
        var change = Money.Satoshis(Inputs.Sum(x => (Money) x.Amount) - Outputs.Sum(x => x.Value));
        // txBuilder.SendFees(change);
        // var psbt = txBuilder.BuildPSBT(true);

        foreach (var sigs in _events.OfType<KompaktorRoundEventSignaturePosted>())
        {
            transaction.Inputs.FindIndexedInput(sigs.Request.OutPoint).WitScript = sigs.Request.Witness;
        }

        return transaction;
    }


    protected virtual T AddEvent<T>(T @event) where T : KompaktorRoundEvent
    {
            _events.AddLast(@event);
            NewEvent?.Invoke(this, @event);
            return @event;
        
       
    }

    public KompaktorRoundEventCreated RoundEventCreated
    {
        get { return _events.OfType<KompaktorRoundEventCreated>().Single(); }
    }
}