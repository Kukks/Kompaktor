namespace Kompaktor.Models;

public record IntRange
{
    public IntRange(int Min, int Max)
    {
        if (Min > Max)
            throw new ArgumentException("Min must be less than or equal to Max.");
        this.Min = Min;
        this.Max = Max;
    }

    public bool Contains(int value) => Min <= value && value <= Max;
    public int Min { get; init; }
    public int Max { get; init; }
}