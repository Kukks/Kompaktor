using Kompaktor.Behaviors;
using Kompaktor.Behaviors.InteractivePayments;
using Kompaktor.Client;
using Kompaktor.Scoring;
using Kompaktor.Wallet;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;
using WabiSabi.Crypto.Randomness;

namespace Kompaktor.Web;

/// <summary>
/// Manages the lifecycle of the KompaktorService (auto-mixing client).
/// Provides start/stop/status for the dashboard toggle.
/// </summary>
public class MixingManager : IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly Network _network;
    private readonly ILogger<MixingManager> _logger;
    private readonly DashboardEventBus _eventBus;

    private KompaktorService? _service;
    private readonly Lock _lock = new();

    public bool IsRunning => _service?.IsRunning == true;
    public int CompletedRounds => _service?.CompletedRounds ?? 0;
    public int FailedRounds => _service?.FailedRounds ?? 0;
    public Uri? CoordinatorUri { get; private set; }
    public bool TorEnabled { get; private set; }
    public string? LastRoundStatus { get; private set; }
    public string? LastRoundFailureReason { get; private set; }
    public bool AllowUnconfirmedCoinjoinReuse { get; private set; }

    /// <summary>Named preset active for the currently running mixing session (or the last one to run).</summary>
    public string? ActiveProfile { get; private set; }

    public string? ActiveRoundPhase
    {
        get
        {
            var round = _service?.ActiveRound;
            return round?.Round?.Status.ToString();
        }
    }

    public int? ActiveRoundInputCount
    {
        get
        {
            var round = _service?.ActiveRound;
            return round?.Round?.Inputs.Count;
        }
    }

    public string[]? ActiveMixingOutpoints
    {
        get
        {
            var round = _service?.ActiveRound;
            var coins = round?.RegisteredCoins;
            return coins?.Select(c => $"{c.Outpoint.Hash}:{c.Outpoint.N}").ToArray();
        }
    }

    public MixingManager(
        IServiceProvider services,
        Network network,
        ILogger<MixingManager> logger,
        DashboardEventBus eventBus)
    {
        _services = services;
        _network = network;
        _logger = logger;
        _eventBus = eventBus;
    }

    public async Task<string> StartAsync(string passphrase, Uri coordinatorUri, TorOptions? torOptions = null, bool allowUnconfirmedCoinjoinReuse = false, string? profileOverride = null)
    {
        lock (_lock)
        {
            if (_service?.IsRunning == true)
                return "Already running";
        }

        // Open the wallet with the passphrase
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
        var walletEntity = await db.Wallets.FirstOrDefaultAsync();
        if (walletEntity is null)
            throw new InvalidOperationException("No wallet found");

        // Resolve profile: request override wins, otherwise use the persisted
        // preference. Falls back to Balanced for legacy wallets saved before
        // the column existed (empty string defaulted via the entity).
        var profile = !string.IsNullOrWhiteSpace(profileOverride)
            ? profileOverride!
            : string.IsNullOrWhiteSpace(walletEntity.MixingProfile) ? "Balanced" : walletEntity.MixingProfile;

        var wallet = await KompaktorHdWallet.OpenAsync(db, walletEntity.Id, _network, passphrase);
        wallet.AllowUnconfirmedCoinjoinReuse = allowUnconfirmedCoinjoinReuse;
        var coinSelector = new WalletCoinSelector(db);
        var scoringWallet = new ScoringWalletAdapter(wallet, coinSelector, wallet.WalletId,
            includeUnconfirmedCoinjoinOutputs: allowUnconfirmedCoinjoinReuse);
        var recorder = new CoinJoinRecorder(db, wallet.WalletId);

        // Load persistent intersection attack tracking
        var roundHistoryTracker = new PersistentRoundHistoryTracker(db, wallet.WalletId);
        await roundHistoryTracker.LoadAsync();

        var random = _network == Network.RegTest
            ? new InsecureRandom()
            : (WasabiRandom)SecureRandom.Instance;

        var circuitFactory = torOptions is not null
            ? new TorCircuitFactory(torOptions)
            : null;

        var options = new KompaktorServiceOptions
        {
            CoordinatorUri = coordinatorUri,
            Network = _network,
            Random = random,
            CircuitFactory = circuitFactory
        };

        var paymentManager = new WalletPaymentManager(db, wallet.WalletId, _network);

        var service = new KompaktorService(options, scoringWallet, _logger, roundHistoryTracker);

        service.BehaviorFactory = (round, factory) =>
        {
            var api = factory.Create();
            var messagingApi = new KompaktorMessagingApi(_logger, round, api);
            return BuildTraitsForProfile(profile, wallet, paymentManager, messagingApi);
        };

        service.RoundCompleted += result =>
        {
            if (result.Success && result.Transaction is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await recorder.RecordRoundAsync(
                            result.RoundId, result.Transaction,
                            result.OurInputOutpoints!, result.OurOutputScripts!,
                            result.TotalParticipantInputs);

                        // Record privacy snapshot after successful round
                        await RecordPrivacySnapshotAsync(db, coinSelector, wallet.WalletId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to record completed round {RoundId}", result.RoundId);
                    }
                });
            }

            LastRoundStatus = result.Success ? "Completed" : "Failed";
            LastRoundFailureReason = result.FailureReason == RoundFailureReason.None
                ? null
                : result.FailureReason.ToString();

            _eventBus.Publish("mixing");
            _eventBus.Publish("utxos");
            _eventBus.Publish("payments");
            _eventBus.Publish("privacy");
        };

        await service.StartAsync();

        lock (_lock)
        {
            _service = service;
        }

        CoordinatorUri = coordinatorUri;
        TorEnabled = torOptions is not null;
        AllowUnconfirmedCoinjoinReuse = allowUnconfirmedCoinjoinReuse;
        ActiveProfile = profile;
        _eventBus.Publish("mixing");
        _logger.LogInformation("Auto-mixing started for wallet {WalletId} with profile {Profile}", wallet.WalletId, profile);
        return "Started";
    }

    /// <summary>
    /// Maps a named mixing preset onto the concrete list of behavior traits
    /// the coinjoin client runs. Reads spec values from
    /// <see cref="MixingProfileCatalog"/> so parameters stay authoritative
    /// in one place.
    /// </summary>
    private static List<KompaktorClientBaseBehaviorTrait> BuildTraitsForProfile(
        string profile,
        KompaktorHdWallet wallet,
        WalletPaymentManager paymentManager,
        KompaktorMessagingApi messagingApi)
    {
        var spec = MixingProfileCatalog.Get(profile);

        var traits = new List<KompaktorClientBaseBehaviorTrait>
        {
            new ConsolidationBehaviorTrait(spec.ConsolidationThreshold),
            new SelfSendChangeBehaviorTrait(wallet.GetChangeScript, TimeSpan.FromSeconds(spec.SelfSendDelaySeconds)),
        };

        if (spec.InteractivePaymentsEnabled)
        {
            traits.Add(new InteractivePaymentSenderBehaviorTrait(paymentManager, messagingApi));
            traits.Add(new InteractivePaymentReceiverBehaviorTrait(paymentManager, messagingApi));
        }

        return traits;
    }

    public async Task<string> StopAsync()
    {
        KompaktorService? service;
        lock (_lock)
        {
            service = _service;
            _service = null;
        }

        if (service is null)
            return "Not running";

        await service.StopAsync();
        await service.DisposeAsync();

        _eventBus.Publish("mixing");
        _logger.LogInformation("Auto-mixing stopped");
        return "Stopped";
    }

    private async Task RecordPrivacySnapshotAsync(WalletDbContext db, WalletCoinSelector coinSelector, string walletId)
    {
        try
        {
            var summary = await coinSelector.GetPrivacySummaryAsync(walletId);
            var roundCount = await db.CoinJoinRecords
                .CountAsync(r => r.Status == "Completed");

            db.PrivacySnapshots.Add(new Wallet.Data.PrivacySnapshotEntity
            {
                WalletId = walletId,
                TotalUtxos = summary.TotalUtxos,
                TotalAmountSat = summary.TotalAmountSat,
                AverageAnonScore = summary.AverageEffectiveScore,
                MinAnonScore = summary.MinEffectiveScore,
                MaxAnonScore = summary.MaxEffectiveScore,
                MixedUtxoCount = summary.MixedUtxoCount,
                UnmixedUtxoCount = summary.NeedsMixingCount,
                CoinJoinRoundNumber = roundCount
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record privacy snapshot");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.DisposeAsync();
            _service = null;
        }
    }
}

/// <summary>
/// Structured description of a named mixing preset. Surfaced verbatim over
/// the HTTP API so clients (dashboard UI, API consumers) don't need to
/// duplicate the values. Adding a new knob means adding a property here
/// and consuming it in <see cref="MixingManager"/>.BuildTraitsForProfile.
/// </summary>
public sealed record MixingProfileSpec(
    string Name,
    int ConsolidationThreshold,
    int SelfSendDelaySeconds,
    bool InteractivePaymentsEnabled,
    string Description);

/// <summary>
/// Canonical list of mixing presets. The single source of truth for both
/// trait construction and the /api/mixing/profiles endpoint.
/// </summary>
public static class MixingProfileCatalog
{
    public const string Default = "Balanced";

    public static readonly IReadOnlyList<MixingProfileSpec> All = new MixingProfileSpec[]
    {
        new(
            "Balanced",
            ConsolidationThreshold: 10,
            SelfSendDelaySeconds: 30,
            InteractivePaymentsEnabled: true,
            Description: "Default: 10 inputs/round, 30s self-send change delay, interactive payments on."),

        // Privacy-focused: smaller consolidation target (fewer inputs per
        // round = more rounds = more unlinkability), longer self-send delay
        // so change doesn't immediately reattach.
        new(
            "PrivacyFocused",
            ConsolidationThreshold: 5,
            SelfSendDelaySeconds: 60,
            InteractivePaymentsEnabled: true,
            Description: "Smaller batches (5/round) and longer self-send delay so change does not reattach; interactive payments on."),

        // Consolidator: aggressively pull inputs into one round so the
        // wallet ends up with fewer, larger UTXOs. Interactive payments
        // off since they compete for slots with consolidation goals.
        new(
            "Consolidator",
            ConsolidationThreshold: 25,
            SelfSendDelaySeconds: 30,
            InteractivePaymentsEnabled: false,
            Description: "Aggressively pulls inputs (25/round) into fewer larger UTXOs; interactive payments off so they do not compete for slots."),

        // Payments: interactive-payment heavy workflow. Consolidation
        // kept conservative so coin-selection doesn't steal slots from
        // outgoing payments queued by the user.
        new(
            "Payments",
            ConsolidationThreshold: 5,
            SelfSendDelaySeconds: 30,
            InteractivePaymentsEnabled: true,
            Description: "5 inputs/round with interactive payment slots prioritised for outgoing peer-to-peer transfers."),
    };

    public static readonly IReadOnlyList<string> Names = All.Select(p => p.Name).ToArray();

    public static bool IsValid(string name) => All.Any(p => p.Name == name);

    /// <summary>
    /// Returns the spec for a named preset, falling back to the default when
    /// the name is unknown or empty. Never throws — keeps the start path
    /// resilient to wallet-setting drift.
    /// </summary>
    public static MixingProfileSpec Get(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            foreach (var spec in All)
            {
                if (spec.Name == name) return spec;
            }
        }
        return All.First(p => p.Name == Default);
    }
}
