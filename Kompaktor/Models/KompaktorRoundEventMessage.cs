namespace Kompaktor.Models;

public record KompaktorRoundEventMessage(MessageRequest Request) : KompaktorRoundEvent
{
    public override string ToString()
    {
        return null;
    }
}