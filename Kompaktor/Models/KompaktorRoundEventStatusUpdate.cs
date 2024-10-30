namespace Kompaktor.Models;

public record KompaktorRoundEventStatusUpdate(KompaktorStatus Status ) : KompaktorRoundEvent
{
    public override string ToString() => $"Status Update: {Status}";
}