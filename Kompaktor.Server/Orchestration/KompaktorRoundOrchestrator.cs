using Kompaktor.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kompaktor.Server.Orchestration;

/// <summary>
/// Background service that manages round lifecycle on a periodic tick.
/// Responsibilities:
/// - Creates rounds based on the scheduling policy
/// - Tracks demand metrics from completed rounds
/// - Integrates with MaxSuggestedAmountProvider for tiered rounds
///
/// Runs every 2 seconds (like Wasabi's Arena).
/// </summary>
public class KompaktorRoundOrchestrator : BackgroundService
{
    private readonly KompaktorRoundManager _roundManager;
    private readonly IRoundSchedulingPolicy _policy;
    private readonly KompaktorCoordinatorOptions _options;
    private readonly MaxSuggestedAmountProvider? _amountProvider;
    private readonly DemandTracker _demandTracker;
    private readonly ILogger<KompaktorRoundOrchestrator> _logger;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    private DateTimeOffset _lastRoundCreated = DateTimeOffset.MinValue;

    // Track rounds we're observing for completion
    private readonly HashSet<string> _observedRounds = new();

    public KompaktorRoundOrchestrator(
        KompaktorRoundManager roundManager,
        IRoundSchedulingPolicy policy,
        KompaktorCoordinatorOptions options,
        ILogger<KompaktorRoundOrchestrator> logger,
        MaxSuggestedAmountProvider? amountProvider = null,
        DemandTracker? demandTracker = null)
    {
        _roundManager = roundManager;
        _policy = policy;
        _options = options;
        _logger = logger;
        _amountProvider = amountProvider;
        _demandTracker = demandTracker ?? new DemandTracker();
    }

    public DemandTracker DemandTracker => _demandTracker;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Round orchestrator started (tick interval: {Interval}, policy: {Policy})",
            TickInterval, _policy.GetType().Name);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Tick(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Orchestrator tick failed");
            }

            await Task.Delay(TickInterval, stoppingToken);
        }

        _logger.LogInformation("Round orchestrator stopped");
    }

    private async Task Tick(CancellationToken ct)
    {
        // 1. Collect metrics from completed rounds
        UpdateDemandMetrics();

        // 2. Build context for the scheduling policy
        var context = BuildContext();

        // 3. Ask the policy how many rounds to create
        var roundsToCreate = _policy.EvaluateRoundCreation(context);

        // 4. Create rounds (respecting max concurrent limit)
        var available = _options.MaxConcurrentRounds - context.TotalActiveRounds;
        roundsToCreate = Math.Min(roundsToCreate, Math.Max(0, available));

        for (var i = 0; i < roundsToCreate; i++)
        {
            try
            {
                var roundId = await _roundManager.CreateRound();
                _lastRoundCreated = DateTimeOffset.UtcNow;
                _observedRounds.Add(roundId);
                _logger.LogInformation("Orchestrator created round {RoundId} ({Index}/{Total})",
                    roundId[..8], i + 1, roundsToCreate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create round {Index}/{Total}", i + 1, roundsToCreate);
                break; // Don't keep trying if creation is broken
            }
        }
    }

    private RoundOrchestrationContext BuildContext()
    {
        var rounds = _roundManager.GetActiveRoundOperators();
        var inRegistration = rounds.Count(r => r.Status == KompaktorStatus.InputRegistration);
        var inProgress = rounds.Count(r =>
            r.Status > KompaktorStatus.InputRegistration &&
            r.Status < KompaktorStatus.Completed);
        var nearlyFull = rounds.Count(r =>
            r.Status == KompaktorStatus.InputRegistration &&
            r.Inputs.Count >= r.RoundEventCreated.InputCount.Max * 0.8);

        return new RoundOrchestrationContext
        {
            RoundsInRegistration = inRegistration,
            RoundsInProgress = inProgress,
            TotalActiveRounds = inRegistration + inProgress,
            MaxConcurrentRounds = _options.MaxConcurrentRounds,
            RecentFillRate = _demandTracker.AverageFillRate,
            RecentCompletedRounds = _demandTracker.RecentCompletedCount,
            RecentFailedRounds = _demandTracker.RecentFailedCount,
            TimeSinceLastRoundCreated = _lastRoundCreated == DateTimeOffset.MinValue
                ? TimeSpan.MaxValue
                : DateTimeOffset.UtcNow - _lastRoundCreated,
            NearlyFullRounds = nearlyFull,
            RoundInterval = _options.RoundInterval
        };
    }

    private void UpdateDemandMetrics()
    {
        var toRemove = new List<string>();
        foreach (var roundId in _observedRounds)
        {
            var op = _roundManager.GetOperator(roundId);
            if (op is null)
            {
                toRemove.Add(roundId);
                continue;
            }

            if (op.Status is KompaktorStatus.Completed or KompaktorStatus.Failed)
            {
                _demandTracker.RecordCompletion(
                    roundId,
                    op.Inputs.Count,
                    op.RoundEventCreated.InputCount.Max,
                    op.Status == KompaktorStatus.Completed);
                toRemove.Add(roundId);
            }
        }

        foreach (var id in toRemove)
            _observedRounds.Remove(id);
    }
}
