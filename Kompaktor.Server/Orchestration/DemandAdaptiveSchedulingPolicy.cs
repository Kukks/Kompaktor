namespace Kompaktor.Server.Orchestration;

/// <summary>
/// Demand-adaptive scheduling that scales round creation based on fill rates
/// and registration pressure. Uses hysteresis: scales up eagerly when rounds
/// fill quickly, scales down conservatively when demand drops.
/// </summary>
public class DemandAdaptiveSchedulingPolicy : IRoundSchedulingPolicy
{
    private readonly double _highDemandFillThreshold;
    private readonly double _lowDemandFillThreshold;

    public DemandAdaptiveSchedulingPolicy(
        double highDemandFillThreshold = 0.7,
        double lowDemandFillThreshold = 0.3)
    {
        _highDemandFillThreshold = highDemandFillThreshold;
        _lowDemandFillThreshold = lowDemandFillThreshold;
    }

    public int EvaluateRoundCreation(RoundOrchestrationContext context)
    {
        // Always ensure at least one registration round exists
        if (context.RoundsInRegistration == 0)
            return 1;

        // If all registration rounds are nearly full, spin up another immediately
        if (context.NearlyFullRounds >= context.RoundsInRegistration)
            return 1;

        // High demand: recent rounds filled well → keep a buffer of open rounds
        if (context.RecentFillRate >= _highDemandFillThreshold &&
            context.RoundsInRegistration < 2)
            return 1;

        // Time-based fallback: if we haven't created a round in a while
        // and there's only one registration round, create another
        if (context.TimeSinceLastRoundCreated > context.RoundInterval * 2 &&
            context.RoundsInRegistration <= 1)
            return 1;

        // Low demand: don't create more if rounds aren't filling
        if (context.RecentFillRate < _lowDemandFillThreshold &&
            context.RecentCompletedRounds > 2)
            return 0;

        return 0;
    }
}
