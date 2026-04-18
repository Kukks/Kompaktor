namespace Kompaktor.Scoring;

public readonly record struct AnonymityScore(
    int RawAnonSet,
    double EffectiveScore,
    int CoinJoinCount,
    ConfidenceLevel Confidence,
    double AmountPenalty = 1.0,
    double ClusterPenalty = 1.0,
    double ReusePenalty = 1.0);

public enum ConfidenceLevel { High, Medium, Low }
