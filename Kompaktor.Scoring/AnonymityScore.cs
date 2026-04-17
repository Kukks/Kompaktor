namespace Kompaktor.Scoring;

public readonly record struct AnonymityScore(
    int RawAnonSet,
    double EffectiveScore,
    int CoinJoinCount,
    ConfidenceLevel Confidence);

public enum ConfidenceLevel { High, Medium, Low }
