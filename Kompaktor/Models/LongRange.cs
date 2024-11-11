namespace Kompaktor.Models;

public record LongRange
{
    public LongRange(long Min, long Max)
    {
        if (Min > Max)
            throw new ArgumentException("Min must be less than or equal to Max.");
        this.Min = Min;
        this.Max = Max;
    }

    public bool Contains(long value) => Min <= value && value <= Max;
    public long Min { get; init; }
    public long Max { get; init; }
}