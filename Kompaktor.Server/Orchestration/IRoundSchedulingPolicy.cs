namespace Kompaktor.Server.Orchestration;

/// <summary>
/// Snapshot of the orchestrator's state, passed to the scheduling policy each tick.
/// </summary>
public record RoundOrchestrationContext
{
    /// <summary>Number of rounds currently in InputRegistration.</summary>
    public int RoundsInRegistration { get; init; }

    /// <summary>Number of rounds currently past InputRegistration (active but not accepting new inputs).</summary>
    public int RoundsInProgress { get; init; }

    /// <summary>Total active rounds (all statuses except Completed/Failed).</summary>
    public int TotalActiveRounds { get; init; }

    /// <summary>Maximum concurrent rounds allowed.</summary>
    public int MaxConcurrentRounds { get; init; }

    /// <summary>Average input fill rate of recently completed rounds (0.0 to 1.0).</summary>
    public double RecentFillRate { get; init; }

    /// <summary>Number of rounds completed successfully in the last window.</summary>
    public int RecentCompletedRounds { get; init; }

    /// <summary>Number of rounds that failed in the last window.</summary>
    public int RecentFailedRounds { get; init; }

    /// <summary>Time since the last round was created.</summary>
    public TimeSpan TimeSinceLastRoundCreated { get; init; }

    /// <summary>Number of rounds in InputRegistration that are nearly full (>80% of max inputs).</summary>
    public int NearlyFullRounds { get; init; }

    /// <summary>Configured round creation interval.</summary>
    public TimeSpan RoundInterval { get; init; }
}

/// <summary>
/// Pluggable policy that decides when to create new rounds.
/// The orchestrator calls this on every tick (~2s).
/// </summary>
public interface IRoundSchedulingPolicy
{
    /// <summary>
    /// Given the current orchestrator state, returns how many rounds should be created.
    /// Return 0 to skip, >0 to create that many rounds.
    /// </summary>
    int EvaluateRoundCreation(RoundOrchestrationContext context);
}
