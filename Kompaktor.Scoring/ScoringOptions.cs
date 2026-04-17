namespace Kompaktor.Scoring;

public class ScoringOptions
{
    public int MaxAncestorDepth { get; set; } = 10;
    public int MaxRawAnonSet { get; set; } = 10_000;
    public double AmountBucketTolerance { get; set; } = 0.01;
    public double MinEffectiveScoreForCoinjoin { get; set; } = 5.0;
    public double ExternalLabelPenalty { get; set; } = 0.3;
    public double InternalLabelPenalty { get; set; } = 0.8;
    public double UniqueAmountPenalty { get; set; } = 0.1;
    public double RareAmountPenalty { get; set; } = 0.5;
    public int RareAmountThreshold { get; set; } = 3;
    public double AddressReusePenalty { get; set; } = 0.5;
}
