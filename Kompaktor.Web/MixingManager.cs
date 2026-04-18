using Kompaktor.Behaviors;
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

    public async Task<string> StartAsync(string passphrase, Uri coordinatorUri, TorOptions? torOptions = null)
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

        var wallet = await KompaktorHdWallet.OpenAsync(db, walletEntity.Id, _network, passphrase);
        var coinSelector = new WalletCoinSelector(db);
        var scoringWallet = new ScoringWalletAdapter(wallet, coinSelector, wallet.WalletId);
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

        var service = new KompaktorService(options, scoringWallet, _logger, roundHistoryTracker);

        service.BehaviorFactory = (round, factory) =>
        [
            new ConsolidationBehaviorTrait(10),
            new SelfSendChangeBehaviorTrait(wallet.GetChangeScript, TimeSpan.FromSeconds(30))
        ];

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
        };

        await service.StartAsync();

        lock (_lock)
        {
            _service = service;
        }

        CoordinatorUri = coordinatorUri;
        TorEnabled = torOptions is not null;
        _eventBus.Publish("mixing");
        _logger.LogInformation("Auto-mixing started for wallet {WalletId}", wallet.WalletId);
        return "Started";
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

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.DisposeAsync();
            _service = null;
        }
    }
}
