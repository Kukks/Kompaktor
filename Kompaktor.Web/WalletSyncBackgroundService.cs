using Kompaktor.Blockchain;
using Kompaktor.Wallet;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;

namespace Kompaktor.Web;

/// <summary>
/// Background service that runs wallet UTXO sync on startup and monitors
/// for new transactions in real time. Publishes SSE events when UTXOs change.
/// </summary>
public class WalletSyncBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IBlockchainBackend _blockchain;
    private readonly Network _network;
    private readonly DashboardEventBus _eventBus;
    private readonly ILogger<WalletSyncBackgroundService> _logger;

    private WalletSyncService? _syncService;
    private IServiceScope? _scope;

    public bool IsSyncing { get; private set; }
    public bool IsMonitoring { get; private set; }
    public DateTimeOffset? LastSyncTime { get; private set; }
    public int LastSyncUtxoCount { get; private set; }

    private readonly SemaphoreSlim _resyncSignal = new(0, 1);

    /// <summary>
    /// Signals the background service to perform an immediate resync.
    /// </summary>
    public void TriggerResync()
    {
        _resyncSignal.Release();
    }

    public WalletSyncBackgroundService(
        IServiceProvider services,
        IBlockchainBackend blockchain,
        Network network,
        DashboardEventBus eventBus,
        ILogger<WalletSyncBackgroundService> logger)
    {
        _services = services;
        _blockchain = blockchain;
        _network = network;
        _eventBus = eventBus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait briefly for app startup to complete
        await Task.Delay(1000, stoppingToken);

        _scope = _services.CreateScope();
        var db = _scope.ServiceProvider.GetRequiredService<WalletDbContext>();

        var wallet = await db.Wallets.FirstOrDefaultAsync(stoppingToken);
        if (wallet is null)
        {
            _logger.LogInformation("No wallet found — sync service will wait for wallet creation");
            // Poll quickly so a just-created wallet picks up monitoring within
            // a second rather than waiting a full 5s polling interval. The DB
            // round-trip is cheap (SQLite, local file) so polling at 500ms is
            // inexpensive even when the wallet is never created.
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(500, stoppingToken);
                wallet = await db.Wallets.FirstOrDefaultAsync(stoppingToken);
                if (wallet is not null) break;
            }
            if (wallet is null) return;
        }

        _syncService = new WalletSyncService(db, _blockchain, _network);

        _syncService.UtxosReceived += utxos =>
        {
            _logger.LogInformation("Sync discovered {Count} new UTXOs", utxos.Length);
            _eventBus.Publish("utxos");
        };

        _syncService.UtxosSpent += utxos =>
        {
            _logger.LogInformation("Sync detected {Count} spent UTXOs", utxos.Length);
            _eventBus.Publish("utxos");
        };

        // Initial full sync
        try
        {
            IsSyncing = true;
            _logger.LogInformation("Starting full wallet sync for {WalletId}", wallet.Id);
            await _syncService.FullSyncAsync(wallet.Id, stoppingToken);
            LastSyncTime = DateTimeOffset.UtcNow;
            LastSyncUtxoCount = _syncService.LastSyncUtxoCount;
            IsSyncing = false;
            _logger.LogInformation("Full sync complete: {Count} UTXOs discovered", LastSyncUtxoCount);
            _eventBus.Publish("utxos");
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            IsSyncing = false;
            _logger.LogError(ex, "Full sync failed");
        }

        // Start real-time monitoring
        try
        {
            await _syncService.StartMonitoringAsync(wallet.Id, stoppingToken);
            IsMonitoring = true;
            _logger.LogInformation("Real-time UTXO monitoring started");
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to start real-time monitoring");
        }

        // Periodic re-sync every 60 seconds, or immediately on manual trigger
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for either 60s timeout or manual resync trigger
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var delayTask = Task.Delay(TimeSpan.FromSeconds(60), delayCts.Token);
                var signalTask = _resyncSignal.WaitAsync(delayCts.Token);
                await Task.WhenAny(delayTask, signalTask);
                delayCts.Cancel(); // Cancel whichever didn't finish

                if (stoppingToken.IsCancellationRequested) break;

                IsSyncing = true;
                await _syncService.FullSyncAsync(wallet.Id, stoppingToken);
                LastSyncTime = DateTimeOffset.UtcNow;
                LastSyncUtxoCount = _syncService.LastSyncUtxoCount;
                IsSyncing = false;

                if (LastSyncUtxoCount > 0)
                    _eventBus.Publish("utxos");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                IsSyncing = false;
                _logger.LogWarning(ex, "Periodic sync failed — will retry next cycle");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // base.StopAsync signals stoppingToken and awaits ExecuteAsync — we
        // must do that FIRST so any in-flight FullSyncAsync on the shared
        // scope/DbContext has a chance to observe cancellation and unwind.
        // Previously we disposed the scope before awaiting ExecuteAsync,
        // which let SQLite see active statements at close time and surface
        // the "unable to delete/modify user-function due to active
        // statements" error on CI runs with slow schedulers.
        await base.StopAsync(cancellationToken);

        if (_syncService is not null)
        {
            await _syncService.DisposeAsync();
            _syncService = null;
        }
        IsMonitoring = false;

        _scope?.Dispose();
        _scope = null;
    }
}
