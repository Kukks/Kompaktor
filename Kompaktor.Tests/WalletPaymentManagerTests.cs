using Kompaktor.Wallet;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Xunit;

namespace Kompaktor.Tests;

public class WalletPaymentManagerTests : IDisposable
{
    private readonly WalletDbContext _db;
    private readonly WalletEntity _wallet;
    private readonly WalletPaymentManager _manager;
    private readonly Network _network = Network.RegTest;

    public WalletPaymentManagerTests()
    {
        var options = new DbContextOptionsBuilder<WalletDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _db = new WalletDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _wallet = new WalletEntity { Name = "PaymentTest", Network = "RegTest" };
        var key1 = new Key();
        var key2 = new Key();
        var account = new AccountEntity
        {
            Purpose = 86, AccountIndex = 0,
            Addresses =
            {
                new AddressEntity
                {
                    KeyPath = "0/0",
                    ScriptPubKey = key1.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86).ToBytes(),
                    IsChange = false, IsUsed = false, IsExposed = false
                },
                new AddressEntity
                {
                    KeyPath = "0/1",
                    ScriptPubKey = key2.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86).ToBytes(),
                    IsChange = false, IsUsed = false, IsExposed = false
                }
            }
        };
        _wallet.Accounts.Add(account);
        _db.Wallets.Add(_wallet);
        _db.SaveChanges();

        _manager = new WalletPaymentManager(_db, _wallet.Id, _network);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreateOutboundPayment_Interactive_GeneratesKey()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        var entity = await _manager.CreateOutboundPaymentAsync(addr, 50_000, interactive: true);

        Assert.Equal("Outbound", entity.Direction);
        Assert.Equal(50_000, entity.AmountSat);
        Assert.Equal("Pending", entity.Status);
        Assert.True(entity.IsInteractive);
        Assert.NotNull(entity.KompaktorKeyHex);
        Assert.True(entity.KompaktorKeyHex.Length > 0);
    }

    [Fact]
    public async Task CreateOutboundPayment_NonInteractive_NoKey()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        var entity = await _manager.CreateOutboundPaymentAsync(addr, 25_000, interactive: false);

        Assert.Equal("Outbound", entity.Direction);
        Assert.False(entity.IsInteractive);
        Assert.Null(entity.KompaktorKeyHex);
    }

    [Fact]
    public async Task CreateInboundPayment_GeneratesPrivateKey_AndAddress()
    {
        var entity = await _manager.CreateInboundPaymentAsync(100_000, "Test invoice");

        Assert.Equal("Inbound", entity.Direction);
        Assert.Equal(100_000, entity.AmountSat);
        Assert.Equal("Pending", entity.Status);
        Assert.True(entity.IsInteractive);
        Assert.NotNull(entity.KompaktorKeyHex);
        Assert.Equal("Test invoice", entity.Label);
        Assert.NotEmpty(entity.Destination);
    }

    [Fact]
    public async Task GetOutboundPendingPayments_FiltersCorrectly()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        await _manager.CreateOutboundPaymentAsync(addr, 10_000);
        await _manager.CreateOutboundPaymentAsync(addr, 20_000);

        var pending = await _manager.GetOutboundPendingPayments(includeReserved: false);
        Assert.Equal(2, pending.Length);

        // Reserve one via Commit
        var all = await _db.PendingPayments
            .Where(p => p.WalletId == _wallet.Id && p.Direction == "Outbound")
            .ToListAsync();
        all[0].Status = "Reserved";
        await _db.SaveChangesAsync();

        var pendingOnly = await _manager.GetOutboundPendingPayments(includeReserved: false);
        Assert.Single(pendingOnly);

        var withReserved = await _manager.GetOutboundPendingPayments(includeReserved: true);
        Assert.Equal(2, withReserved.Length);
    }

    [Fact]
    public async Task GetInboundPendingPayments_ExcludesOutbound()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        await _manager.CreateOutboundPaymentAsync(addr, 10_000);
        await _manager.CreateInboundPaymentAsync(20_000);

        var inbound = await _manager.GetInboundPendingPayments(includeReserved: false);
        Assert.Single(inbound);
        Assert.Equal(Money.Satoshis(20_000), inbound[0].Amount);
    }

    [Fact]
    public async Task Commit_ReturnsTrue_ForPendingPayment()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        var entity = await _manager.CreateOutboundPaymentAsync(addr, 50_000);

        var result = await _manager.Commit(entity.Id);
        Assert.True(result);

        var updated = await _db.PendingPayments.FindAsync(entity.Id);
        Assert.Equal("Committed", updated!.Status);
    }

    [Fact]
    public async Task Commit_ReturnsFalse_ForCompletedPayment()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        var entity = await _manager.CreateOutboundPaymentAsync(addr, 50_000);
        entity.Status = "Completed";
        await _db.SaveChangesAsync();

        var result = await _manager.Commit(entity.Id);
        Assert.False(result);
    }

    [Fact]
    public async Task BreakCommitment_ResetsTooPending()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        var entity = await _manager.CreateOutboundPaymentAsync(addr, 50_000);
        await _manager.Commit(entity.Id);

        await _manager.BreakCommitment(entity.Id);

        var updated = await _db.PendingPayments.FindAsync(entity.Id);
        Assert.Equal("Pending", updated!.Status);
    }

    [Fact]
    public async Task BreakCommitment_IncrementsRetryCount()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        var entity = await _manager.CreateOutboundPaymentAsync(addr, 50_000);
        Assert.Equal(0, entity.RetryCount);

        await _manager.Commit(entity.Id);
        await _manager.BreakCommitment(entity.Id);

        var updated = await _db.PendingPayments.FindAsync(entity.Id);
        Assert.Equal(1, updated!.RetryCount);

        // Second retry
        await _manager.Commit(entity.Id);
        await _manager.BreakCommitment(entity.Id);

        updated = await _db.PendingPayments.FindAsync(entity.Id);
        Assert.Equal(2, updated!.RetryCount);
    }

    [Fact]
    public async Task CancelPayment_SetsFailed_ForPendingPayment()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        var entity = await _manager.CreateOutboundPaymentAsync(addr, 50_000);

        var result = await _manager.CancelPaymentAsync(entity.Id);
        Assert.True(result);

        var updated = await _db.PendingPayments.FindAsync(entity.Id);
        Assert.Equal("Failed", updated!.Status);
    }

    [Fact]
    public async Task CancelPayment_ReturnsFalse_ForCompleted()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        var entity = await _manager.CreateOutboundPaymentAsync(addr, 50_000);
        entity.Status = "Completed";
        await _db.SaveChangesAsync();

        var result = await _manager.CancelPaymentAsync(entity.Id);
        Assert.False(result);
    }

    [Fact]
    public async Task CancelPayment_ReturnsFalse_ForWrongWallet()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        var entity = await _manager.CreateOutboundPaymentAsync(addr, 50_000);

        // Create a manager for a different wallet
        var otherManager = new WalletPaymentManager(_db, "other-wallet-id", _network);
        var result = await otherManager.CancelPaymentAsync(entity.Id);
        Assert.False(result);
    }

    [Fact]
    public async Task ToProtocolPayment_InteractiveOutbound_ReturnsInteractivePendingPayment()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        await _manager.CreateOutboundPaymentAsync(addr, 75_000, interactive: true);

        var pending = await _manager.GetOutboundPendingPayments(includeReserved: false);
        Assert.Single(pending);
        Assert.IsType<Kompaktor.Behaviors.InteractivePayments.InteractivePendingPayment>(pending[0]);
    }

    [Fact]
    public async Task ToProtocolPayment_InteractiveInbound_ReturnsInteractiveReceiverPendingPayment()
    {
        await _manager.CreateInboundPaymentAsync(75_000);

        var pending = await _manager.GetInboundPendingPayments(includeReserved: false);
        Assert.Single(pending);
        Assert.IsType<Kompaktor.Behaviors.InteractivePayments.InteractiveReceiverPendingPayment>(pending[0]);
    }

    [Fact]
    public async Task CreateInboundPayment_ThrowsWhenNoAddresses()
    {
        // Use up both available addresses
        var addrs = await _db.Addresses
            .Where(a => a.Account.WalletId == _wallet.Id && !a.IsUsed && !a.IsExposed && !a.IsChange)
            .ToListAsync();
        foreach (var a in addrs)
        {
            a.IsUsed = true;
        }
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.CreateInboundPaymentAsync(50_000));
    }

    [Fact]
    public async Task CreateOutboundPayment_WithExpiry_SetsExpiresAt()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        var entity = await _manager.CreateOutboundPaymentAsync(
            addr, 50_000, expiry: TimeSpan.FromMinutes(30));

        Assert.NotNull(entity.ExpiresAt);
        Assert.True(entity.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.True(entity.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(31));
    }

    [Fact]
    public async Task ExpiredPayments_AreAutoCancelled()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        // Create a payment that already expired
        var entity = await _manager.CreateOutboundPaymentAsync(addr, 50_000);
        entity.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        // Fetching pending payments should auto-expire it
        var pending = await _manager.GetOutboundPendingPayments(includeReserved: false);
        Assert.Empty(pending);

        var updated = await _db.PendingPayments.FindAsync(entity.Id);
        Assert.Equal("Failed", updated!.Status);
    }

    [Fact]
    public async Task NonExpiredPayments_AreNotCancelled()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        var entity = await _manager.CreateOutboundPaymentAsync(
            addr, 50_000, expiry: TimeSpan.FromMinutes(60));

        var pending = await _manager.GetOutboundPendingPayments(includeReserved: false);
        Assert.Single(pending);
    }

    [Fact]
    public async Task CreateOutboundPayment_RejectsDustAmount()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        await Assert.ThrowsAsync<ArgumentException>(
            () => _manager.CreateOutboundPaymentAsync(addr, 100));
    }

    [Fact]
    public async Task CreateInboundPayment_RejectsDustAmount()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _manager.CreateInboundPaymentAsync(500));
    }

    [Fact]
    public async Task ExpireStalePayments_FiresWebhookForExpiredPayments()
    {
        // Create a webhook for this wallet
        _db.PaymentWebhooks.Add(new PaymentWebhookEntity
        {
            WalletId = _wallet.Id,
            Url = "http://localhost:9999/hook", // Will fail to connect
            Secret = "s",
            IsActive = true,
            EventFilter = "*"
        });

        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        var entity = await _manager.CreateOutboundPaymentAsync(addr, 50_000, expiry: TimeSpan.FromMilliseconds(1));

        // Wait for expiry
        await Task.Delay(50);

        // Trigger expiry by fetching payments (which calls ExpireStalePaymentsAsync internally)
        var payments = await _manager.GetOutboundPendingPayments(false);

        // The payment should now be Failed
        var updated = await _db.PendingPayments.FindAsync(entity.Id);
        Assert.Equal("Failed", updated!.Status);

        // A webhook delivery should have been attempted for the "Expired" event
        var deliveries = await _db.WebhookDeliveries.ToListAsync();
        Assert.Single(deliveries);
        Assert.Equal("Expired", deliveries[0].EventType);
    }

    [Fact]
    public async Task MultipleOutboundPayments_AllQueuedAsPending()
    {
        var addr1 = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        var addr2 = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        var addr3 = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();

        var e1 = await _manager.CreateOutboundPaymentAsync(addr1, 10_000, label: "First");
        var e2 = await _manager.CreateOutboundPaymentAsync(addr2, 20_000, label: "Second");
        var e3 = await _manager.CreateOutboundPaymentAsync(addr3, 30_000, label: "Third");

        var pending = await _manager.GetOutboundPendingPayments(false);
        Assert.Equal(3, pending.Length);

        Assert.Equal("Pending", e1.Status);
        Assert.Equal("Pending", e2.Status);
        Assert.Equal("Pending", e3.Status);
    }

    [Fact]
    public async Task BreakCommitment_RetriesExhausted_FailsPayment()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        var entity = await _manager.CreateOutboundPaymentAsync(addr, 50_000);

        // Set a low retry limit for testing
        entity.MaxRetries = 3;
        await _db.SaveChangesAsync();

        // Simulate commit + break cycles
        await _manager.Commit(entity.Id);
        await _manager.BreakCommitment(entity.Id); // retry 1
        var updated = await _db.PendingPayments.FindAsync(entity.Id);
        Assert.Equal("Pending", updated!.Status);
        Assert.Equal(1, updated.RetryCount);

        await _manager.Commit(entity.Id);
        await _manager.BreakCommitment(entity.Id); // retry 2
        updated = await _db.PendingPayments.FindAsync(entity.Id);
        Assert.Equal("Pending", updated!.Status);

        await _manager.Commit(entity.Id);
        await _manager.BreakCommitment(entity.Id); // retry 3 = max → Failed
        updated = await _db.PendingPayments.FindAsync(entity.Id);
        Assert.Equal("Failed", updated!.Status);
        Assert.Equal(3, updated.RetryCount);
    }

    [Fact]
    public async Task BreakCommitment_UnlimitedRetries_NeverFails()
    {
        var addr = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network).ToString();
        var entity = await _manager.CreateOutboundPaymentAsync(addr, 50_000);

        // Set unlimited retries
        entity.MaxRetries = 0;
        await _db.SaveChangesAsync();

        // Run 20 retry cycles — should never auto-fail
        for (int i = 0; i < 20; i++)
        {
            await _manager.Commit(entity.Id);
            await _manager.BreakCommitment(entity.Id);
        }

        var updated = await _db.PendingPayments.FindAsync(entity.Id);
        Assert.Equal("Pending", updated!.Status);
        Assert.Equal(20, updated.RetryCount);
    }
}
