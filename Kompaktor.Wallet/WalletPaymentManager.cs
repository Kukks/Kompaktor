using System.Text.Json;
using Kompaktor.Behaviors.InteractivePayments;
using Kompaktor.Contracts;
using Kompaktor.Mapper;
using Kompaktor.Models;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using NBitcoin.Secp256k1;

using XPubKey = NBitcoin.Secp256k1.ECXOnlyPubKey;
using PrivKey = NBitcoin.Secp256k1.ECPrivKey;

namespace Kompaktor.Wallet;

/// <summary>
/// Wallet-backed implementation of both outbound and inbound payment managers.
/// Persists pending payments to the wallet database and maps them to/from
/// the protocol's PendingPayment records used by behavior traits.
/// </summary>
public class WalletPaymentManager : IOutboundPaymentManager, IInboundPaymentManager
{
    private readonly WalletDbContext _db;
    private readonly string _walletId;
    private readonly Network _network;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public WalletPaymentManager(WalletDbContext db, string walletId, Network network)
    {
        _db = db;
        _walletId = walletId;
        _network = network;
    }

    public async Task<PendingPayment[]> GetOutboundPendingPayments(bool includeReserved)
    {
        await _lock.WaitAsync();
        try
        {
            await ExpireStalePaymentsAsync();

            var query = _db.PendingPayments
                .Where(p => p.WalletId == _walletId && p.Direction == "Outbound");

            if (!includeReserved)
                query = query.Where(p => p.Status == "Pending");
            else
                query = query.Where(p => p.Status == "Pending" || p.Status == "Reserved");

            var entities = await query.ToListAsync();
            return entities.Select(ToProtocolPayment).ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<PendingPayment[]> GetInboundPendingPayments(bool includeReserved)
    {
        await _lock.WaitAsync();
        try
        {
            await ExpireStalePaymentsAsync();

            var query = _db.PendingPayments
                .Where(p => p.WalletId == _walletId && p.Direction == "Inbound");

            if (!includeReserved)
                query = query.Where(p => p.Status == "Pending");
            else
                query = query.Where(p => p.Status == "Pending" || p.Status == "Reserved");

            var entities = await query.ToListAsync();
            return entities.Select(ToProtocolPayment).ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> Commit(string pendingPaymentId)
    {
        await _lock.WaitAsync();
        try
        {
            var entity = await _db.PendingPayments.FindAsync(pendingPaymentId);
            if (entity is null || entity.WalletId != _walletId)
                return false;

            if (entity.Status != "Pending" && entity.Status != "Reserved")
                return false;

            entity.Status = "Committed";
            await _db.SaveChangesAsync();
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task BreakCommitment(string pendingPaymentId)
    {
        await _lock.WaitAsync();
        try
        {
            var entity = await _db.PendingPayments.FindAsync(pendingPaymentId);
            if (entity is null || entity.WalletId != _walletId) return;

            // Reset to pending so it can be retried in the next round
            entity.Status = "Pending";
            entity.RetryCount++;
            await _db.SaveChangesAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddProof(string pendingPaymentId, KompaktorOffchainPaymentProof proof)
    {
        await _lock.WaitAsync();
        PendingPaymentEntity? entity;
        try
        {
            entity = await _db.PendingPayments.FindAsync(pendingPaymentId);
            if (entity is null || entity.WalletId != _walletId) return;

            entity.Status = "Completed";
            entity.CompletedAt = DateTimeOffset.UtcNow;
            entity.CompletedTxId = proof.TxId.ToString();
            entity.ProofJson = JsonSerializer.Serialize(new
            {
                txId = proof.TxId.ToString(),
                paymentAmount = proof.PaymentAmount,
                kompaktorKey = Convert.ToHexString(proof.KompaktorKey.Key.ToBytes()),
                verified = proof.Verify()
            });
            await _db.SaveChangesAsync();
        }
        finally
        {
            _lock.Release();
        }

        // Fire webhook for completed payment (outside lock to avoid blocking)
        try
        {
            var webhookSvc = new PaymentWebhookService(_db, _walletId);
            await webhookSvc.DeliverAsync(entity, "Completed");
        }
        catch
        {
            // Webhook delivery failure should never block payment completion
        }
    }

    /// <summary>
    /// Creates a new outbound payment (send to someone via CoinJoin).
    /// </summary>
    public async Task<PendingPaymentEntity> CreateOutboundPaymentAsync(
        string destination, long amountSat, bool interactive = true, bool urgent = false,
        string? label = null, TimeSpan? expiry = null)
    {
        if (amountSat < 546)
            throw new ArgumentException("Amount below dust limit (546 sats)");

        var address = BitcoinAddress.Create(destination, _network);

        var entity = new PendingPaymentEntity
        {
            WalletId = _walletId,
            Direction = "Outbound",
            AmountSat = amountSat,
            Destination = address.ToString(),
            IsInteractive = interactive,
            IsUrgent = urgent,
            Label = label,
            Status = "Pending",
            ExpiresAt = expiry.HasValue ? DateTimeOffset.UtcNow + expiry.Value : null
        };

        // For interactive outbound: generate a protocol key for the sender
        if (interactive)
        {
            var key = ECPrivKey.Create(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            entity.KompaktorKeyHex = Convert.ToHexString(key.CreateXOnlyPubKey().ToBytes());
        }

        _db.PendingPayments.Add(entity);
        await _db.SaveChangesAsync();
        return entity;
    }

    /// <summary>
    /// Creates a new inbound payment request (receive from someone via CoinJoin).
    /// Returns the entity including the generated Kompaktor key for the BIP21 URI.
    /// </summary>
    public async Task<PendingPaymentEntity> CreateInboundPaymentAsync(
        long amountSat, string? label = null, TimeSpan? expiry = null)
    {
        if (amountSat < 546)
            throw new ArgumentException("Amount below dust limit (546 sats)");

        // Generate a protocol key pair for the receiver
        var privKeyBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var ecPrivKey = ECPrivKey.Create(privKeyBytes);

        // Get a fresh receive address, auto-extending gap if needed
        var address = await GetOrExtendFreshAddressAsync(isChange: false);
        if (address is null)
            throw new InvalidOperationException("No fresh addresses available");

        var script = new Script(address.ScriptPubKey);
        var btcAddress = script.GetDestinationAddress(_network);

        var entity = new PendingPaymentEntity
        {
            WalletId = _walletId,
            Direction = "Inbound",
            AmountSat = amountSat,
            Destination = btcAddress!.ToString(),
            IsInteractive = true,
            KompaktorKeyHex = Convert.ToHexString(privKeyBytes),
            Label = label,
            Status = "Pending",
            ExpiresAt = expiry.HasValue ? DateTimeOffset.UtcNow + expiry.Value : null
        };

        _db.PendingPayments.Add(entity);
        await _db.SaveChangesAsync();
        return entity;
    }

    private async Task ExpireStalePaymentsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        // Fetch all active payments with expiry and check client-side
        // (nullable DateTimeOffset comparisons vary across EF providers)
        var candidates = await _db.PendingPayments
            .Where(p => p.WalletId == _walletId)
            .Where(p => p.Status == "Pending" || p.Status == "Reserved")
            .Where(p => p.ExpiresAt != null)
            .ToListAsync();

        var expired = candidates.Where(p => p.ExpiresAt <= now).ToList();

        if (expired.Count > 0)
        {
            foreach (var p in expired)
                p.Status = "Failed";
            await _db.SaveChangesAsync();

            // Fire webhooks for expired payments (outside main flow, best-effort)
            try
            {
                var webhookSvc = new PaymentWebhookService(_db, _walletId);
                foreach (var p in expired)
                    await webhookSvc.DeliverAsync(p, "Expired");
            }
            catch
            {
                // Webhook delivery failure should never block payment processing
            }
        }
    }

    /// <summary>Cancels a pending payment.</summary>
    public async Task<bool> CancelPaymentAsync(string paymentId)
    {
        var entity = await _db.PendingPayments.FindAsync(paymentId);
        if (entity is null || entity.WalletId != _walletId) return false;
        if (entity.Status is "Completed" or "Committed") return false;

        entity.Status = "Failed";
        await _db.SaveChangesAsync();
        return true;
    }

    private async Task<AddressEntity?> GetOrExtendFreshAddressAsync(bool isChange)
    {
        var chain = isChange ? 1 : 0;
        var address = await _db.Addresses
            .Include(a => a.Account)
            .Where(a => a.Account.WalletId == _walletId)
            .Where(a => !a.IsUsed && !a.IsExposed && a.IsChange == isChange)
            .OrderByDescending(a => a.Account.Purpose)
            .ThenBy(a => a.Id)
            .FirstOrDefaultAsync();

        if (address is not null) return address;

        // Auto-extend from stored xpub
        var accounts = await _db.Accounts
            .Include(a => a.Addresses)
            .Where(a => a.WalletId == _walletId && a.AccountXPub != null)
            .ToListAsync();

        foreach (var acct in accounts)
        {
            var chainAddrs = acct.Addresses.Where(a => a.KeyPath.StartsWith($"{chain}/")).ToList();
            var maxIdx = chainAddrs.Count > 0
                ? chainAddrs.Max(a => int.Parse(a.KeyPath.Split('/')[1]))
                : -1;
            var newAddrs = KompaktorHdWallet.DeriveAddressesFromXPub(
                acct.AccountXPub!, _network, acct.Purpose, chain, maxIdx + 1, 20);
            foreach (var a in newAddrs)
            {
                a.AccountId = acct.Id;
                _db.Addresses.Add(a);
            }
        }

        if (accounts.Count > 0) await _db.SaveChangesAsync();

        return await _db.Addresses
            .Include(a => a.Account)
            .Where(a => a.Account.WalletId == _walletId)
            .Where(a => !a.IsUsed && !a.IsExposed && a.IsChange == isChange)
            .OrderByDescending(a => a.Account.Purpose)
            .ThenBy(a => a.Id)
            .FirstOrDefaultAsync();
    }

    private PendingPayment ToProtocolPayment(PendingPaymentEntity entity)
    {
        var address = BitcoinAddress.Create(entity.Destination, _network);
        var amount = Money.Satoshis(entity.AmountSat);
        var reserved = entity.Status is "Reserved" or "Committed";

        if (entity.IsInteractive && entity.Direction == "Outbound")
        {
            XPubKey? pubKey = null;
            if (entity.KompaktorKeyHex is not null)
            {
                var bytes = Convert.FromHexString(entity.KompaktorKeyHex);
                if (ECXOnlyPubKey.TryCreate(bytes, out var ecPubKey))
                    pubKey = ecPubKey;
            }

            return new InteractivePendingPayment(
                entity.Id, amount, address, reserved, pubKey, entity.IsUrgent);
        }

        if (entity.IsInteractive && entity.Direction == "Inbound")
        {
            PrivKey? privKey = null;
            if (entity.KompaktorKeyHex is not null)
            {
                var bytes = Convert.FromHexString(entity.KompaktorKeyHex);
                var ecPrivKey = ECPrivKey.Create(bytes);
                privKey = ecPrivKey;
            }

            return new InteractiveReceiverPendingPayment(
                entity.Id, amount, address, reserved, privKey);
        }

        return new PendingPayment(entity.Id, amount, address, reserved);
    }
}
