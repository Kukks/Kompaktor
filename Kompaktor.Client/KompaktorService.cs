using Kompaktor.Behaviors;
using Kompaktor.Contracts;
using Kompaktor.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using WabiSabi.Crypto.Randomness;

namespace Kompaktor.Client;

/// <summary>
/// Configuration for the KompaktorService.
/// </summary>
public class KompaktorServiceOptions
{
    /// <summary>
    /// Coordinator server URI (e.g. http://localhost:5000 or onion address via Tor).
    /// </summary>
    public required Uri CoordinatorUri { get; init; }

    /// <summary>
    /// Bitcoin network (Main, TestNet, RegTest).
    /// </summary>
    public required Network Network { get; init; }

    /// <summary>
    /// Circuit factory for Tor stream isolation. Defaults to direct connections.
    /// </summary>
    public ICircuitFactory? CircuitFactory { get; init; }

    /// <summary>
    /// Random source. Use SecureRandom for production, InsecureRandom for tests.
    /// </summary>
    public required WasabiRandom Random { get; init; }

    /// <summary>
    /// How often to poll the coordinator for new rounds when idle.
    /// </summary>
    public TimeSpan RoundDiscoveryInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often the RemoteKompaktorRound polls for events during active participation.
    /// </summary>
    public TimeSpan EventPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Delay between completed/failed rounds before joining the next one.
    /// Prevents rapid-fire round joining that could fingerprint the wallet.
    /// </summary>
    public TimeSpan RoundCooldown { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum consecutive round failures before the service pauses.
    /// Prevents hammering a potentially hostile coordinator.
    /// </summary>
    public int MaxConsecutiveFailures { get; init; } = 5;

    /// <summary>
    /// Pause duration after hitting MaxConsecutiveFailures.
    /// </summary>
    public TimeSpan FailurePauseDuration { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Factory for creating behavior traits that will be attached to each round.
/// Called at the start of each round so traits are fresh (no state leakage across rounds).
/// </summary>
/// <param name="round">The round being joined.</param>
/// <param name="apiFactory">Factory for creating per-circuit API instances.</param>
public delegate List<KompaktorClientBaseBehaviorTrait> BehaviorTraitFactory(
    KompaktorRound round,
    IKompaktorRoundApiFactory apiFactory);

/// <summary>
/// Predicate for deciding whether to join a discovered round based on its parameters.
/// Return true to join, false to skip.
/// </summary>
public delegate bool RoundFilter(RoundInfoResponse roundInfo);

/// <summary>
/// High-level service that manages continuous coinjoin participation.
///
/// Handles the complete lifecycle:
/// 1. Discover active rounds on the coordinator
/// 2. Evaluate round parameters (fee rate, input range, etc.)
/// 3. Join the best matching round with configured behavior traits
/// 4. Track round progress via event polling
/// 5. Handle round completion/failure and rejoin automatically
///
/// This is the primary integration point for wallet developers.
///
/// Usage:
/// <code>
/// var service = new KompaktorService(options, wallet, logger);
/// service.BehaviorFactory = (round, factory) => [
///     new ConsolidationBehaviorTrait(10),
///     new SelfSendChangeBehaviorTrait(wallet.GetChangeScript, TimeSpan.FromSeconds(30))
/// ];
/// await service.StartAsync(cts.Token);
/// </code>
/// </summary>
public class KompaktorService : IAsyncDisposable
{
    private readonly KompaktorServiceOptions _options;
    private readonly IKompaktorWalletInterface _wallet;
    private readonly ILogger _logger;
    private readonly KompaktorCoordinatorClient _coordinator;
    private readonly IRoundHistoryTracker? _roundHistoryTracker;

    private CancellationTokenSource? _cts;
    private Task? _runLoop;
    private int _consecutiveFailures;

    /// <summary>
    /// Factory for creating behavior traits per round. Must be set before calling StartAsync.
    /// </summary>
    public BehaviorTraitFactory? BehaviorFactory { get; set; }

    /// <summary>
    /// Optional filter for round selection. If not set, joins any round whose
    /// parameters are compatible with the wallet's available coins.
    /// </summary>
    public RoundFilter? RoundFilter { get; set; }

    /// <summary>
    /// The currently active round client, if participating. Null when idle.
    /// </summary>
    public KompaktorRoundClient? ActiveRound { get; private set; }

    /// <summary>
    /// Whether the service is currently running.
    /// </summary>
    public bool IsRunning => _runLoop is { IsCompleted: false };

    /// <summary>
    /// Total number of rounds completed successfully since service started.
    /// </summary>
    public int CompletedRounds { get; private set; }

    /// <summary>
    /// Total number of rounds that failed since service started.
    /// </summary>
    public int FailedRounds { get; private set; }

    /// <summary>
    /// Fired when a round completes (success or failure).
    /// </summary>
    public event Action<KompaktorRoundResult>? RoundCompleted;

    public KompaktorService(
        KompaktorServiceOptions options,
        IKompaktorWalletInterface wallet,
        ILogger? logger = null,
        IRoundHistoryTracker? roundHistoryTracker = null)
    {
        _options = options;
        _wallet = wallet;
        _logger = logger ?? NullLogger.Instance;
        _roundHistoryTracker = roundHistoryTracker;
        _coordinator = new KompaktorCoordinatorClient(
            options.CoordinatorUri,
            options.CircuitFactory,
            _logger);
    }

    /// <summary>
    /// Starts the continuous coinjoin participation loop.
    /// The service will discover rounds, join them, and rejoin automatically.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("Service is already running");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _consecutiveFailures = 0;
        _runLoop = RunLoopAsync(_cts.Token);
        _logger.LogInformation("KompaktorService started — coordinator: {Uri}, network: {Network}",
            _options.CoordinatorUri, _options.Network.Name);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the service gracefully, waiting for the current round to reach a terminal state.
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts is null) return;

        _logger.LogInformation("KompaktorService stopping...");
        await _cts.CancelAsync();

        if (_runLoop is not null)
        {
            try { await _runLoop; }
            catch (OperationCanceledException) { }
        }

        _logger.LogInformation("KompaktorService stopped. Completed: {Completed}, Failed: {Failed}",
            CompletedRounds, FailedRounds);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Check if we should pause due to consecutive failures
                if (_consecutiveFailures >= _options.MaxConsecutiveFailures)
                {
                    _logger.LogWarning(
                        "Pausing for {Duration} after {Failures} consecutive failures",
                        _options.FailurePauseDuration, _consecutiveFailures);
                    await Task.Delay(_options.FailurePauseDuration, ct);
                    _consecutiveFailures = 0;
                }

                // Discover and select a round
                var roundInfo = await DiscoverRoundAsync(ct);
                if (roundInfo is null)
                {
                    await Task.Delay(_options.RoundDiscoveryInterval, ct);
                    continue;
                }

                // Participate in the round
                var result = await ParticipateInRoundAsync(roundInfo, ct);

                // Update counters
                if (result.Success)
                {
                    CompletedRounds++;
                    _consecutiveFailures = 0;
                }
                else
                {
                    FailedRounds++;
                    _consecutiveFailures++;
                }

                RoundCompleted?.Invoke(result);

                // Cooldown between rounds
                if (!ct.IsCancellationRequested)
                    await Task.Delay(_options.RoundCooldown, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex, "Unexpected error in run loop (failure #{Failures})", _consecutiveFailures);
                await Task.Delay(_options.RoundDiscoveryInterval, ct);
            }
        }
    }

    private async Task<RoundInfoResponse?> DiscoverRoundAsync(CancellationToken ct)
    {
        var roundIds = await _coordinator.GetActiveRoundsAsync(ct);

        if (roundIds.Length == 0)
        {
            _logger.LogDebug("No active rounds found");
            return null;
        }

        // Evaluate each round's parameters and find a suitable one
        foreach (var roundId in roundIds)
        {
            try
            {
                var info = await _coordinator.GetRoundInfoAsync(roundId, ct);

                if (RoundFilter is not null && !RoundFilter(info))
                {
                    _logger.LogDebug("Round {RoundId} filtered out", roundId);
                    continue;
                }

                // Check if the round is in input registration phase
                var status = await _coordinator.GetRoundStatusAsync(roundId, ct);
                if (status.Status != nameof(KompaktorStatus.InputRegistration))
                {
                    _logger.LogDebug("Round {RoundId} not in input registration ({Status})", roundId, status.Status);
                    continue;
                }

                _logger.LogInformation("Selected round {RoundId} (fee rate: {FeeRate} sat/kvB)",
                    roundId, info.FeeRateSatPerK);
                return info;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to evaluate round {RoundId}", roundId);
            }
        }

        return null;
    }

    private async Task<KompaktorRoundResult> ParticipateInRoundAsync(
        RoundInfoResponse roundInfo, CancellationToken ct)
    {
        var roundId = roundInfo.RoundId;
        _logger.LogInformation("Joining round {RoundId}", roundId);

        // Create per-round API factory with circuit isolation
        var apiFactory = _coordinator.CreateRoundApiFactory(roundId);

        // Create a polling round that stays in sync with the coordinator
        var pollApi = new HttpKompaktorRoundApi(
            new HttpClient(
                (_options.CircuitFactory ?? new DefaultCircuitFactory())
                    .Create($"poll-{roundId}")
                    .CreateHandler())
            { BaseAddress = _options.CoordinatorUri, Timeout = TimeSpan.FromSeconds(30) },
            roundId);

        var round = new RemoteKompaktorRound(pollApi, _options.EventPollInterval, _logger);

        try
        {
            await round.StartPollingAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start polling for round {RoundId}", roundId);
            round.Dispose();
            pollApi.Dispose();
            return new KompaktorRoundResult(roundId, false, ex.Message);
        }

        // Create behavior traits for this round
        var traits = BehaviorFactory?.Invoke(round, apiFactory) ?? [];

        // Create the round client
        using var client = new KompaktorRoundClient(
            _options.Random,
            _options.Network,
            round,
            apiFactory,
            traits,
            _wallet,
            _logger,
            _roundHistoryTracker);

        ActiveRound = client;

        try
        {
            // Wait for the round to reach a terminal state
            await client.PhasesTask.WaitAsync(ct);

            var success = round.Status == KompaktorStatus.Completed;
            _logger.LogInformation("Round {RoundId} ended: {Status}", roundId, round.Status);

            return new KompaktorRoundResult(
                roundId,
                success,
                round.Status.ToString(),
                client.RegisteredInputs.Length,
                client.RegisteredOutputs.Length);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Round {RoundId} cancelled", roundId);
            return new KompaktorRoundResult(roundId, false, "Cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Round {RoundId} failed with exception", roundId);
            return new KompaktorRoundResult(roundId, false, ex.Message);
        }
        finally
        {
            ActiveRound = null;
            round.Dispose();
            pollApi.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _coordinator.Dispose();
        _cts?.Dispose();
    }
}

/// <summary>
/// Result of a single round participation attempt.
/// </summary>
public record KompaktorRoundResult(
    string RoundId,
    bool Success,
    string StatusMessage,
    int InputsRegistered = 0,
    int OutputsRegistered = 0);
