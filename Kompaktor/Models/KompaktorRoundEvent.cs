namespace Kompaktor.Models;

public abstract record KompaktorRoundEvent
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public override string ToString()
    {
        return GetType().Name;
    }
}