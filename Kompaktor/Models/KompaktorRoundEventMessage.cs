namespace Kompaktor.Models;

public record KompaktorRoundEventMessage(MessageRequest Request) : KompaktorRoundEvent
{
}