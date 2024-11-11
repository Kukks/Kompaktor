using NBitcoin;

namespace Kompaktor.Models;

public record MoneyRange
{
    public MoneyRange(Money Min, Money Max)
    {
        if (Min > Max)
            throw new ArgumentException("Min must be less than or equal to Max.");
        this.Min = Min;
        this.Max = Max;
    }

    public bool Contains(Money value) => Min <= value && value <= Max;
    public Money Min { get; init; }
    public Money Max { get; init; }
}