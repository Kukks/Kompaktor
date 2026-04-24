using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;

namespace Kompaktor.Wallet;

/// <summary>
/// Delivers webhook notifications when payment status changes.
/// Computes HMAC-SHA256 signatures so receivers can verify authenticity.
/// </summary>
public class PaymentWebhookService
{
    private readonly WalletDbContext _db;
    private readonly HttpClient _http;
    private readonly string _walletId;

    public PaymentWebhookService(WalletDbContext db, string walletId, HttpClient? http = null)
    {
        _db = db;
        _walletId = walletId;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// Delivers webhooks for a payment status change. Called after AddProof, CancelPayment, etc.
    /// </summary>
    public async Task DeliverAsync(PendingPaymentEntity payment, string eventType)
    {
        var webhooks = await _db.PaymentWebhooks
            .Where(w => w.WalletId == _walletId && w.IsActive)
            .ToListAsync();

        foreach (var webhook in webhooks)
        {
            if (!MatchesFilter(webhook.EventFilter, eventType))
                continue;

            await DeliverToWebhookAsync(webhook, payment, eventType);
        }
    }

    /// <summary>
    /// Force a single delivery attempt to a specific webhook, bypassing the active/event-filter
    /// checks. Used by the manual redeliver endpoint — the user is explicitly asking to resend,
    /// so we honour that even if the webhook was paused or the filter changed since the original.
    /// The payload reflects the payment's CURRENT state with the ORIGINAL event type (we don't
    /// store the original JSON body).
    /// </summary>
    public async Task<WebhookDeliveryEntity> RedeliverAsync(
        PaymentWebhookEntity webhook, PendingPaymentEntity payment, string eventType)
    {
        await DeliverToWebhookAsync(webhook, payment, eventType);
        return await _db.WebhookDeliveries
            .Where(d => d.WebhookId == webhook.Id && d.PaymentId == payment.Id)
            .OrderByDescending(d => d.Id)
            .FirstAsync();
    }

    /// <summary>
    /// Sends a synthetic "Test" event so the user can verify their receiver
    /// end-to-end (URL reachable, signature verification passes, their handler
    /// parses JSON) without waiting for a real payment. Bypasses active/filter
    /// checks — if the user hit Test, they want the ping even if the hook is
    /// paused. Receivers should branch on eventType=="Test" and skip business
    /// logic. Returns the delivery record so callers can surface status.
    /// </summary>
    public async Task<WebhookDeliveryEntity> SendTestAsync(PaymentWebhookEntity webhook)
    {
        var testPayment = new PendingPaymentEntity
        {
            Id = $"test-ping-{Guid.NewGuid():N}",
            WalletId = webhook.WalletId,
            Direction = "Test",
            AmountSat = 0,
            Destination = "",
            Status = "Test",
            Label = "Kompaktor webhook test delivery"
        };
        await DeliverToWebhookAsync(webhook, testPayment, "Test");
        return await _db.WebhookDeliveries
            .Where(d => d.WebhookId == webhook.Id && d.PaymentId == testPayment.Id)
            .OrderByDescending(d => d.Id)
            .FirstAsync();
    }

    private async Task DeliverToWebhookAsync(
        PaymentWebhookEntity webhook, PendingPaymentEntity payment, string eventType)
    {
        var payload = new
        {
            eventType,
            paymentId = payment.Id,
            direction = payment.Direction,
            amountSat = payment.AmountSat,
            destination = payment.Destination,
            status = payment.Status,
            label = payment.Label,
            completedTxId = payment.CompletedTxId,
            createdAt = payment.CreatedAt,
            completedAt = payment.CompletedAt,
            timestamp = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(payload);
        var signature = ComputeSignature(json, webhook.Secret);

        var delivery = new WebhookDeliveryEntity
        {
            WebhookId = webhook.Id,
            PaymentId = payment.Id,
            EventType = eventType
        };

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, webhook.Url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Kompaktor-Signature", signature);
            request.Headers.Add("X-Kompaktor-Event", eventType);

            var response = await _http.SendAsync(request);
            delivery.HttpStatusCode = (int)response.StatusCode;
            delivery.Success = response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            delivery.HttpStatusCode = 0;
            delivery.Success = false;
            delivery.ErrorMessage = ex.Message;
        }

        _db.WebhookDeliveries.Add(delivery);
        await _db.SaveChangesAsync();
    }

    private static bool MatchesFilter(string filter, string eventType)
    {
        if (filter == "*") return true;
        return filter.Split(',', StringSplitOptions.TrimEntries)
            .Any(f => f.Equals(eventType, StringComparison.OrdinalIgnoreCase));
    }

    public static string ComputeSignature(string payload, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(key, data);
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
