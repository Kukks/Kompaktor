namespace Kompaktor.Server.Orchestration;

/// <summary>
/// Simple interval-based scheduling: creates one round every RoundInterval
/// as long as there's capacity. Good baseline for low-traffic deployments.
/// </summary>
public class IntervalSchedulingPolicy : IRoundSchedulingPolicy
{
    public int EvaluateRoundCreation(RoundOrchestrationContext context)
    {
        if (context.RoundsInRegistration > 0)
            return 0;

        if (context.TimeSinceLastRoundCreated < context.RoundInterval)
            return 0;

        return 1;
    }
}
