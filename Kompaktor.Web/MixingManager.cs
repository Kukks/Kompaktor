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
    /// the coinjoin client runs. Keeping this out-of-line makes it easy to
    /// add a new preset without touching the start flow.
    /// </summary>
    private static List<KompaktorClientBaseBehaviorTrait> BuildTraitsForProfile(
        string profile,
        KompaktorHdWallet wallet,
        WalletPaymentManager paymentManager,
        KompaktorMessagingApi messagingApi)
    {
        // These knobs deliberately diverge per profile so users see the
        // preset *doing something* rather than being cosmetic.
        return profile switch
        {
            // Privacy-focused: smaller consolidation target (fewer inputs per
            // round = more rounds = more unlinkability), longer self-send
            // delay so change doesn't immediately reattach.
            "PrivacyFocused" =>
            [
                new ConsolidationBehaviorTrait(5),
                new SelfSendChangeBehaviorTrait(wallet.GetChangeScript, TimeSpan.FromSeconds(60)),
                new InteractivePaymentSenderBehaviorTrait(paymentManager, messagingApi),
                new InteractivePaymentReceiverBehaviorTrait(paymentManager, messagingApi)
            ],

            // Consolidator: aggressively pull inputs into one round so the
            // wallet ends up with fewer, larger UTXOs. Interactive payments
            // off since they compete for slots with consolidation goals.
            "Consolidator" =>
            [
                new ConsolidationBehaviorTrait(25),
                new SelfSendChangeBehaviorTrait(wallet.GetChangeScript, TimeSpan.FromSeconds(30)),
            ],

            // Payments: interactive-payment heavy workflow. Consolidation
            // kept conservative so coin-selection doesn't steal slots from
            // outgoing payments queued by the user.
            "Payments" =>
            [
                new ConsolidationBehaviorTrait(5),
                new SelfSendChangeBehaviorTrait(wallet.GetChangeScript, TimeSpan.FromSeconds(30)),
                new InteractivePaymentSenderBehaviorTrait(paymentManager, messagingApi),
                new InteractivePaymentReceiverBehaviorTrait(paymentManager, messagingApi)
            ],

            // Balanced (default) and any unknown string fall through to the
            // historical trait bundle — safe for rollback.
            _ =>
            [
                new ConsolidationBehaviorTrait(10),
                new SelfSendChangeBehaviorTrait(wallet.GetChangeScript, TimeSpan.FromSeconds(30)),
                new InteractivePaymentSenderBehaviorTrait(paymentManager, messagingApi),
                new InteractivePaymentReceiverBehaviorTrait(paymentManager, messagingApi)
            ],
        };
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
