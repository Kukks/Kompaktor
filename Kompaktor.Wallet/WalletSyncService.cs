using Kompaktor.Blockchain;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;

namespace Kompaktor.Wallet;

/// <summary>
/// Synchronizes the wallet's UTXO database with the blockchain.
///
/// Two modes of operation:
/// 1. Full sync: queries all wallet addresses against the backend
/// 2. Real-time: subscribes to address notifications and handles incrementally
///
/// Handles gap limit extension: when used addresses approach the gap boundary,
/// new addresses are derived to maintain the gap limit.
///
/// Usage:
/// <code>
/// var sync = new WalletSyncService(walletDb, blockchain, network);
/// await sync.FullSyncAsync(walletId, masterKey, ct);
/// await sync.StartMonitoringAsync(walletId, ct); // Real-time updates
/// </code>
/// </summary>
public class WalletSyncService : IAsyncDisposable
{
    private readonly WalletDbContext _db;
    private readonly IBlockchainBackend _blockchain;
    private readonly Network _network;
    private readonly List<Script> _subscribedScripts = [];
    private CancellationTokenSource? _monitoringCts;

    // Tracks in-flight address-notification handlers so StopMonitoringAsync
    // can drain them before the caller disposes the scoped DbContext. Without
    // this, an async-void handler can still be executing a query against
    // _db when the connection pool tries to reclaim the SQLite connection.
    private readonly HashSet<Task> _pendingHandlers = [];
    private readonly Lock _pendingHandlersLock = new();

    private const int GapLimit = 20;

    public WalletSyncService(WalletDbContext db, IBlockchainBackend blockchain, Network network)
    {
        _db = db;
        _blockchain = blockchain;
        _network = network;
    }

    /// <summary>
    /// Fired when new UTXOs are detected for the wallet.
    /// </summary>
    public event Action<UtxoEntity[]>? UtxosReceived;

    /// <summary>
    /// Fired when UTXOs are detected as spent.
    /// </summary>
    public event Action<UtxoEntity[]>? UtxosSpent;

    /// <summary>
    /// Number of UTXOs discovered in the last sync.
    /// </summary>
    public int LastSyncUtxoCount { get; private set; }

    /// <summary>
    /// Performs a full synchronization of all wallet addresses against the blockchain backend.
    /// Discovers new UTXOs and marks spent ones.
    /// </summary>
    public async Task FullSyncAsync(string walletId, CancellationToken ct = default)
    {
        var currentHeight = await _blockchain.GetBlockHeightAsync(ct);

        var addresses = await _db.Addresses
            .Include(a => a.Account)
            .Include(a => a.Utxos)
            .Where(a => a.Account.WalletId == walletId)
            .ToListAsync(ct);

        var newUtxos = new List<UtxoEntity>();
        var spentUtxos = new List<UtxoEntity>();
        var accountsNeedingExtension = new HashSet<int>();

        foreach (var addr in addresses)
        {
            var script = new Script(addr.ScriptPubKey);
            var backendUtxos = await _blockchain.GetUtxosForScriptAsync(script, ct);

            // Track which outpoints the backend reports
            var backendOutpoints = new HashSet<string>(
                backendUtxos.Select(u => $"{u.OutPoint.Hash}:{u.OutPoint.N}"));

            // Detect new UTXOs
            foreach (var utxoInfo in backendUtxos)
            {
                var txId = utxoInfo.OutPoint.Hash.ToString();
                var outputIndex = (int)utxoInfo.OutPoint.N;

                var existing = addr.Utxos.FirstOrDefault(u =>
                    u.TxId == txId && u.OutputIndex == outputIndex);

                if (existing is null)
                {
                    var entity = new UtxoEntity
                    {
                        TxId = txId,
                        OutputIndex = outputIndex,
                        AddressId = addr.Id,
                        AmountSat = utxoInfo.TxOut.Value.Satoshi,
                        ScriptPubKey = utxoInfo.TxOut.ScriptPubKey.ToBytes(),
                        ConfirmedHeight = utxoInfo.Confirmations > 0
                            ? currentHeight - utxoInfo.Confirmations + 1
                            : null,
                        IsCoinBase = utxoInfo.IsCoinBase
                    };
                    _db.Utxos.Add(entity);
                    newUtxos.Add(entity);

                    // Mark address as used — may trigger gap extension
                    if (!addr.IsUsed)
                    {
                        addr.IsUsed = true;
                        accountsNeedingExtension.Add(addr.AccountId);
                    }
                }
                else if (existing.ConfirmedHeight is null && utxoInfo.Confirmations > 0)
                {
                    // Previously unconfirmed, now confirmed
                    existing.ConfirmedHeight = currentHeight - utxoInfo.Confirmations + 1;
                }
            }

            // Detect spent UTXOs: any unspent UTXO in our DB that isn't in the backend set
            foreach (var utxo in addr.Utxos.Where(u => u.SpentByTxId is null))
            {
                var key = $"{utxo.TxId}:{utxo.OutputIndex}";
                if (!backendOutpoints.Contains(key))
                {
                    // UTXO was spent — we don't know the spending tx from the UTXO set alone,
                    // but we know it's gone. Mark with placeholder.
                    utxo.SpentByTxId = "detected-spent";
                    spentUtxos.Add(utxo);
                }
            }
        }

        // Extend gap limit for accounts where used addresses approach the boundary
        foreach (var accountId in accountsNeedingExtension)
        {
            await ExtendGapLimitIfNeededAsync(walletId, accountId, ct);
        }

        await _db.SaveChangesAsync(ct);

        LastSyncUtxoCount = newUtxos.Count;

        if (newUtxos.Count > 0)
            UtxosReceived?.Invoke(newUtxos.ToArray());
        if (spentUtxos.Count > 0)
            UtxosSpent?.Invoke(spentUtxos.ToArray());
    }

    /// <summary>
    /// Starts real-time monitoring by subscribing to all wallet addresses.
    /// Address notifications trigger incremental UTXO updates.
    /// </summary>
    public async Task StartMonitoringAsync(string walletId, CancellationToken ct = default)
    {
        _monitoringCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var addresses = await _db.Addresses
            .Include(a => a.Account)
            .Where(a => a.Account.WalletId == walletId)
            .ToListAsync(ct);

        _blockchain.AddressNotified += OnAddressNotified;

        foreach (var addr in addresses)
        {
            var script = new Script(addr.ScriptPubKey);
            await _blockchain.SubscribeAddressAsync(script, ct);
            lock (_subscribedScripts) _subscribedScripts.Add(script);
        }
    }

    /// <summary>
    /// Stops real-time monitoring and unsubscribes from all addresses.
    /// </summary>
    public async Task StopMonitoringAsync()
    {
        _blockchain.AddressNotified -= OnAddressNotified;

        // Snapshot under a copy to tolerate concurrent Add during shutdown —
        // `StartMonitoringAsync` can still be appending when the host cancels.
        Script[] toUnsubscribe;
        lock (_subscribedScripts)
        {
            toUnsubscribe = _subscribedScripts.ToArray();
            _subscribedScripts.Clear();
        }

        foreach (var script in toUnsubscribe)
        {
            try { await _blockchain.UnsubscribeAddressAsync(script); }
            catch { /* Best effort unsubscribe */ }
        }

        // Atomically claim the CTS so we can't race with a second StopMonitoringAsync
        // or with the framework disposing the underlying stoppingToken.
        var cts = Interlocked.Exchange(ref _monitoringCts, null);
        if (cts is not null)
        {
            try { await cts.CancelAsync(); }
            catch (ObjectDisposedException) { /* already disposed — cancellation no-op */ }
            cts.Dispose();
        }

        // Drain in-flight notification handlers so the caller can safely
        // dispose the scoped DbContext. Unsubscribing from AddressNotified
        // above prevents new handlers from starting, but ones already running
        // still hold an EF Core query open on the SQLite connection.
        Task[] pending;
        lock (_pendingHandlersLock) pending = [.. _pendingHandlers];
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending); }
            catch { /* handlers swallow errors internally; drain is best-effort */ }
        }
    }

    private void OnAddressNotified(object? sender, AddressNotification notification)
    {
        if (_monitoringCts?.IsCancellationRequested == true) return;

        var task = HandleAddressNotifiedAsync(notification);
        lock (_pendingHandlersLock) _pendingHandlers.Add(task);
        _ = task.ContinueWith(
            t => { lock (_pendingHandlersLock) _pendingHandlers.Remove(t); },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleAddressNotifiedAsync(AddressNotification notification)
    {
        try
        {
            var scriptBytes = notification.ScriptPubKey.ToBytes();
            var address = await _db.Addresses
                .Include(a => a.Account)
                .Include(a => a.Utxos)
                .FirstOrDefaultAsync(a => a.ScriptPubKey == scriptBytes);

            if (address is null) return;

            // Re-query this address's UTXOs from the backend
            var backendUtxos = await _blockchain.GetUtxosForScriptAsync(notification.ScriptPubKey);
            var newUtxos = new List<UtxoEntity>();

            var currentHeight = notification.Height
                ?? await _blockchain.GetBlockHeightAsync();

            foreach (var utxoInfo in backendUtxos)
            {
                var txId = utxoInfo.OutPoint.Hash.ToString();
                var outputIndex = (int)utxoInfo.OutPoint.N;

                if (address.Utxos.Any(u => u.TxId == txId && u.OutputIndex == outputIndex))
                    continue;

                var entity = new UtxoEntity
                {
                    TxId = txId,
                    OutputIndex = outputIndex,
                    AddressId = address.Id,
                    AmountSat = utxoInfo.TxOut.Value.Satoshi,
                    ScriptPubKey = utxoInfo.TxOut.ScriptPubKey.ToBytes(),
                    ConfirmedHeight = utxoInfo.Confirmations > 0
                        ? currentHeight - utxoInfo.Confirmations + 1
                        : null,
                    IsCoinBase = utxoInfo.IsCoinBase
                };
                _db.Utxos.Add(entity);
                newUtxos.Add(entity);
            }

            if (!address.IsUsed && newUtxos.Count > 0)
            {
                address.IsUsed = true;
                await ExtendGapLimitIfNeededAsync(
                    address.Account.WalletId, address.AccountId);
            }

            // Detect spent UTXOs
            var backendOutpoints = new HashSet<string>(
                backendUtxos.Select(u => $"{u.OutPoint.Hash}:{u.OutPoint.N}"));

            var spentUtxos = new List<UtxoEntity>();
            foreach (var utxo in address.Utxos.Where(u => u.SpentByTxId is null))
            {
                if (!backendOutpoints.Contains($"{utxo.TxId}:{utxo.OutputIndex}"))
                {
                    utxo.SpentByTxId = notification.TxId.ToString();
                    spentUtxos.Add(utxo);
                }
            }

            await _db.SaveChangesAsync();

            if (newUtxos.Count > 0)
                UtxosReceived?.Invoke(newUtxos.ToArray());
            if (spentUtxos.Count > 0)
                UtxosSpent?.Invoke(spentUtxos.ToArray());
        }
        catch
        {
            // Notification handler — swallow errors to keep monitoring alive
        }
    }

    /// <summary>
    /// Checks if the unused address gap for an account has fallen below the limit.
    /// If so, derives new addresses to maintain the gap.
    /// </summary>
    private async Task ExtendGapLimitIfNeededAsync(
        string walletId, int accountId, CancellationToken ct = default)
    {
        var account = await _db.Accounts
            .Include(a => a.Addresses)
            .FirstOrDefaultAsync(a => a.Id == accountId && a.WalletId == walletId, ct);

        if (account?.AccountXPub is null) return;

        var needsSave = false;
        foreach (var chain in new[] { 0, 1 })
        {
            var chainAddresses = account.Addresses
                .Where(a => a.KeyPath.StartsWith($"{chain}/"))
                .OrderBy(a => int.Parse(a.KeyPath.Split('/')[1]))
                .ToList();

            // Count consecutive unused addresses at the end
            var unusedTail = 0;
            for (var i = chainAddresses.Count - 1; i >= 0; i--)
            {
                if (!chainAddresses[i].IsUsed) unusedTail++;
                else break;
            }

            if (unusedTail >= GapLimit) continue;

            // Derive enough addresses to restore the gap
            var maxIndex = chainAddresses.Count > 0
                ? chainAddresses.Max(a => int.Parse(a.KeyPath.Split('/')[1]))
                : -1;
            var toDerive = GapLimit - unusedTail;

            var newAddresses = KompaktorHdWallet.DeriveAddressesFromXPub(
                account.AccountXPub, _network, account.Purpose, chain,
                maxIndex + 1, toDerive);

            foreach (var addr in newAddresses)
            {
                addr.AccountId = account.Id;
                _db.Addresses.Add(addr);
                account.Addresses.Add(addr);
            }
            needsSave = true;
        }

        if (needsSave) await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Gets the current balance (confirmed + unconfirmed) for a wallet.
    /// </summary>
    public async Task<(long Confirmed, long Unconfirmed)> GetBalanceAsync(
        string walletId, CancellationToken ct = default)
    {
        var utxos = await _db.Utxos
            .Include(u => u.Address)
            .ThenInclude(a => a.Account)
            .Where(u => u.SpentByTxId == null && u.Address.Account.WalletId == walletId)
            .ToListAsync(ct);

        var confirmed = utxos.Where(u => u.ConfirmedHeight != null).Sum(u => u.AmountSat);
        var unconfirmed = utxos.Where(u => u.ConfirmedHeight == null).Sum(u => u.AmountSat);

        return (confirmed, unconfirmed);
    }

    public async ValueTask DisposeAsync()
    {
        await StopMonitoringAsync();
    }
}
