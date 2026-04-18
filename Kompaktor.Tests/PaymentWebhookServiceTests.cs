using System.Net;
using System.Text.Json;
using Kompaktor.Wallet;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kompaktor.Tests;

public class PaymentWebhookServiceTests : IDisposable
{
    private readonly WalletDbContext _db;
    private readonly string _walletId;

    public PaymentWebhookServiceTests()
    {
        var options = new DbContextOptionsBuilder<WalletDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _db = new WalletDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        var wallet = new WalletEntity { Name = "WebhookTest", Network = "RegTest" };
        _db.Wallets.Add(wallet);
        _db.SaveChanges();
        _walletId = wallet.Id;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void ComputeSignature_ProducesConsistentHmac()
    {
        var sig1 = PaymentWebhookService.ComputeSignature("test payload", "secret");
        var sig2 = PaymentWebhookService.ComputeSignature("test payload", "secret");

        Assert.StartsWith("sha256=", sig1);
        Assert.Equal(sig1, sig2);
    }

    [Fact]
    public void ComputeSignature_DifferentSecrets_ProduceDifferentSignatures()
    {
        var sig1 = PaymentWebhookService.ComputeSignature("payload", "secret1");
        var sig2 = PaymentWebhookService.ComputeSignature("payload", "secret2");

        Assert.NotEqual(sig1, sig2);
    }

    [Fact]
    public async Task DeliverAsync_SkipsInactiveWebhooks()
    {
        _db.PaymentWebhooks.Add(new PaymentWebhookEntity
        {
            WalletId = _walletId,
            Url = "http://localhost:9999/hook",
            Secret = "s",
            IsActive = false
        });
        await _db.SaveChangesAsync();

        var payment = new PendingPaymentEntity
        {
            WalletId = _walletId,
            Direction = "Outbound",
            AmountSat = 50000,
            Destination = "bcrt1test"
        };

        var svc = new PaymentWebhookService(_db, _walletId);
        await svc.DeliverAsync(payment, "Completed");

        // No deliveries should be recorded
        var deliveries = await _db.WebhookDeliveries.ToListAsync();
        Assert.Empty(deliveries);
    }

    [Fact]
    public async Task DeliverAsync_RespectsEventFilter()
    {
        _db.PaymentWebhooks.Add(new PaymentWebhookEntity
        {
            WalletId = _walletId,
            Url = "http://localhost:9999/hook",
            Secret = "s",
            IsActive = true,
            EventFilter = "Failed" // Only fires for Failed events
        });
        await _db.SaveChangesAsync();

        var payment = new PendingPaymentEntity
        {
            WalletId = _walletId,
            Direction = "Outbound",
            AmountSat = 50000,
            Destination = "bcrt1test"
        };

        var svc = new PaymentWebhookService(_db, _walletId);
        await svc.DeliverAsync(payment, "Completed");

        // Should not have delivered (Completed doesn't match "Failed" filter)
        var deliveries = await _db.WebhookDeliveries.ToListAsync();
        Assert.Empty(deliveries);
    }

    [Fact]
    public async Task DeliverAsync_WildcardFilter_MatchesAll()
    {
        _db.PaymentWebhooks.Add(new PaymentWebhookEntity
        {
            WalletId = _walletId,
            Url = "http://localhost:9999/hook", // Will fail to connect
            Secret = "s",
            IsActive = true,
            EventFilter = "*"
        });
        await _db.SaveChangesAsync();

        var payment = new PendingPaymentEntity
        {
            WalletId = _walletId,
            Direction = "Inbound",
            AmountSat = 100000,
            Destination = "bcrt1test"
        };

        var svc = new PaymentWebhookService(_db, _walletId);
        await svc.DeliverAsync(payment, "Completed");

        // Delivery should be recorded (even though it fails to connect)
        var deliveries = await _db.WebhookDeliveries.ToListAsync();
        Assert.Single(deliveries);
        Assert.Equal("Completed", deliveries[0].EventType);
        Assert.False(deliveries[0].Success); // Connection refused
        Assert.NotNull(deliveries[0].ErrorMessage);
    }

    [Fact]
    public async Task DeliverAsync_RecordsDeliveryAttempt()
    {
        var webhook = new PaymentWebhookEntity
        {
            WalletId = _walletId,
            Url = "http://localhost:9999/hook", // Will fail
            Secret = "test-secret",
            IsActive = true,
            EventFilter = "*"
        };
        _db.PaymentWebhooks.Add(webhook);
        await _db.SaveChangesAsync();

        var payment = new PendingPaymentEntity
        {
            WalletId = _walletId,
            Direction = "Outbound",
            AmountSat = 75000,
            Destination = "bcrt1test",
            Status = "Completed",
            CompletedTxId = "abc123"
        };

        var svc = new PaymentWebhookService(_db, _walletId);
        await svc.DeliverAsync(payment, "Completed");

        var delivery = await _db.WebhookDeliveries.SingleAsync();
        Assert.Equal(webhook.Id, delivery.WebhookId);
        Assert.Equal(payment.Id, delivery.PaymentId);
        Assert.Equal("Completed", delivery.EventType);
    }

    [Fact]
    public async Task DeliverAsync_IgnoresOtherWalletWebhooks()
    {
        _db.PaymentWebhooks.Add(new PaymentWebhookEntity
        {
            WalletId = "other-wallet",
            Url = "http://localhost:9999/hook",
            Secret = "s",
            IsActive = true,
            EventFilter = "*"
        });
        await _db.SaveChangesAsync();

        var payment = new PendingPaymentEntity
        {
            WalletId = _walletId,
            Direction = "Outbound",
            AmountSat = 50000,
            Destination = "bcrt1test"
        };

        var svc = new PaymentWebhookService(_db, _walletId);
        await svc.DeliverAsync(payment, "Completed");

        Assert.Empty(await _db.WebhookDeliveries.ToListAsync());
    }

    [Fact]
    public async Task DeliverAsync_WithMockHttp_SuccessfulDelivery()
    {
        _db.PaymentWebhooks.Add(new PaymentWebhookEntity
        {
            WalletId = _walletId,
            Url = "http://test/hook",
            Secret = "my-secret",
            IsActive = true,
            EventFilter = "*"
        });
        await _db.SaveChangesAsync();

        var payment = new PendingPaymentEntity
        {
            WalletId = _walletId,
            Direction = "Outbound",
            AmountSat = 50000,
            Destination = "bcrt1test",
            Status = "Completed"
        };

        // Use a mock HTTP handler that returns 200
        var handler = new MockHttpHandler(HttpStatusCode.OK);
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        var svc = new PaymentWebhookService(_db, _walletId, http);
        await svc.DeliverAsync(payment, "Completed");

        var delivery = await _db.WebhookDeliveries.SingleAsync();
        Assert.True(delivery.Success);
        Assert.Equal(200, delivery.HttpStatusCode);

        // Verify the request had the signature header
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest.Headers.Contains("X-Kompaktor-Signature"));
        Assert.True(handler.LastRequest.Headers.Contains("X-Kompaktor-Event"));

        var eventHeader = handler.LastRequest.Headers.GetValues("X-Kompaktor-Event").First();
        Assert.Equal("Completed", eventHeader);
    }

    [Fact]
    public async Task DeliverAsync_ExpiredEventType_Delivered()
    {
        _db.PaymentWebhooks.Add(new PaymentWebhookEntity
        {
            WalletId = _walletId,
            Url = "http://test/hook",
            Secret = "s",
            IsActive = true,
            EventFilter = "*"
        });
        await _db.SaveChangesAsync();

        var payment = new PendingPaymentEntity
        {
            WalletId = _walletId,
            Direction = "Outbound",
            AmountSat = 50000,
            Destination = "bcrt1test",
            Status = "Failed" // Already marked failed by expiry
        };

        var handler = new MockHttpHandler(HttpStatusCode.OK);
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        var svc = new PaymentWebhookService(_db, _walletId, http);
        await svc.DeliverAsync(payment, "Expired");

        var delivery = await _db.WebhookDeliveries.SingleAsync();
        Assert.True(delivery.Success);
        Assert.Equal("Expired", delivery.EventType);

        var eventHeader = handler.LastRequest!.Headers.GetValues("X-Kompaktor-Event").First();
        Assert.Equal("Expired", eventHeader);
    }

    private class MockHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        public HttpRequestMessage? LastRequest { get; private set; }

        public MockHttpHandler(HttpStatusCode statusCode) => _statusCode = statusCode;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_statusCode));
        }
    }
}
