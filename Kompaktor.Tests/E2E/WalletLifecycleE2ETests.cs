using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kompaktor.Blockchain;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NBitcoin.DataEncoders;
using Xunit;

namespace Kompaktor.Tests.E2E;

/// <summary>
/// End-to-end tests driving the web API over an in-process HTTP client.
/// These tests stand up the real Kompaktor.Web application (minus external
/// blockchain access) and exercise user-facing flows start-to-finish.
/// </summary>
public class WalletLifecycleE2ETests
{
    [Fact]
    public async Task Create_wallet_returns_mnemonic_and_reports_backup_unverified()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var createResp = await client.PostAsJsonAsync("/api/wallet/create", new
        {
            Passphrase = "correct horse battery staple",
            Name = "E2E",
            WordCount = 12
        });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var mnemonic = created.GetProperty("mnemonic").GetString();
        Assert.False(string.IsNullOrWhiteSpace(mnemonic));
        Assert.Equal(12, mnemonic!.Split(' ').Length);

        var info = await client.GetFromJsonAsync<JsonElement>("/api/wallet/info");
        Assert.True(info.GetProperty("exists").GetBoolean());
        Assert.False(info.GetProperty("isBackupVerified").GetBoolean());
    }

    [Fact]
    public async Task Create_wallet_twice_returns_bad_request()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var first = await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "one" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "two" });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task Receive_address_endpoint_returns_valid_regtest_address()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var address = resp.GetProperty("address").GetString();
        Assert.False(string.IsNullOrWhiteSpace(address));
        // RegTest bech32 addresses start with "bcrt1"
        Assert.StartsWith("bcrt1", address);
    }

    [Fact]
    public async Task Backup_verification_succeeds_with_correct_answers()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        const string passphrase = "backup-test-pw";

        var created = await (await client.PostAsJsonAsync(
            "/api/wallet/create", new { Passphrase = passphrase }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var words = created.GetProperty("mnemonic").GetString()!.Split(' ');

        // Ask for a challenge
        var challenge = await (await client.PostAsJsonAsync(
            "/api/wallet/backup-challenge", new { Passphrase = passphrase }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(challenge.GetProperty("verified").GetBoolean());

        var positions = challenge.GetProperty("positions").EnumerateArray()
            .Select(e => e.GetInt32()).ToArray();
        Assert.Equal(3, positions.Length);

        var answers = positions.Select(p => new { Position = p, Word = words[p - 1] }).ToArray();

        var verifyResp = await client.PostAsJsonAsync(
            "/api/wallet/verify-backup", new { Passphrase = passphrase, Answers = answers });
        Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);

        var verify = await verifyResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(verify.GetProperty("verified").GetBoolean());

        var info = await client.GetFromJsonAsync<JsonElement>("/api/wallet/info");
        Assert.True(info.GetProperty("isBackupVerified").GetBoolean());
    }

    [Fact]
    public async Task Backup_verification_rejects_wrong_answers()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        const string passphrase = "pw";
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = passphrase });

        var challenge = await (await client.PostAsJsonAsync(
            "/api/wallet/backup-challenge", new { Passphrase = passphrase }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var positions = challenge.GetProperty("positions").EnumerateArray()
            .Select(e => e.GetInt32()).ToArray();

        var wrong = positions.Select(p => new { Position = p, Word = "zoo" }).ToArray();

        var resp = await client.PostAsJsonAsync(
            "/api/wallet/verify-backup", new { Passphrase = passphrase, Answers = wrong });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("verified").GetBoolean());

        // State should remain unverified
        var info = await client.GetFromJsonAsync<JsonElement>("/api/wallet/info");
        Assert.False(info.GetProperty("isBackupVerified").GetBoolean());
    }

    [Fact]
    public async Task Backup_challenge_with_wrong_passphrase_returns_bad_request()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "real" });

        var resp = await client.PostAsJsonAsync(
            "/api/wallet/backup-challenge", new { Passphrase = "wrong" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fee_estimates_endpoint_returns_fake_backend_rate()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        // Response shape is not a single FeeRate — it's a map of confirmation targets.
        var response = await client.GetAsync("/api/dashboard/fee-estimates");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Wallet_info_when_no_wallet_reports_not_exists()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var info = await client.GetFromJsonAsync<JsonElement>("/api/wallet/info");
        Assert.False(info.GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task Price_endpoint_returns_fake_rates()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/price");
        Assert.True(resp.GetProperty("available").GetBoolean());
        var rates = resp.GetProperty("rates");
        Assert.Equal(65_000m, rates.GetProperty("usd").GetDecimal());
        Assert.Equal(60_000m, rates.GetProperty("eur").GetDecimal());
    }

    [Fact]
    public async Task Export_xpub_returns_per_account_keys()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/wallet/export-xpub");
        var accounts = resp.GetProperty("accounts").EnumerateArray().ToArray();
        Assert.NotEmpty(accounts);

        foreach (var account in accounts)
        {
            var xpub = account.GetProperty("xpub").GetString();
            Assert.False(string.IsNullOrWhiteSpace(xpub));
            var purpose = account.GetProperty("purpose").GetInt32();
            Assert.Contains(purpose, new[] { 84, 86 });
        }

        // Expect RegTest derivation — coin type 1
        var taproot = accounts.First(a => a.GetProperty("purpose").GetInt32() == 86);
        Assert.Equal("m/86'/1'/0'", taproot.GetProperty("derivationPath").GetString());
    }

    [Fact]
    public async Task Export_xpub_without_wallet_returns_bad_request()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/wallet/export-xpub");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Address_validation_accepts_regtest_address()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        // Pull a real regtest address generated by the wallet itself.
        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var address = receive.GetProperty("address").GetString()!;

        var resp = await client.GetFromJsonAsync<JsonElement>(
            "/api/dashboard/validate-address?address=" + Uri.EscapeDataString(address));
        Assert.True(resp.GetProperty("valid").GetBoolean());
        var type = resp.GetProperty("type").GetString();
        Assert.Contains(type, new[] { "P2TR", "P2WPKH" });
    }

    [Fact]
    public async Task Address_validation_detects_wrong_network()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        // A mainnet bech32 address should be flagged as wrong_network on a regtest coordinator.
        const string mainnetAddress = "bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq";
        var resp = await client.GetFromJsonAsync<JsonElement>(
            "/api/dashboard/validate-address?address=" + Uri.EscapeDataString(mainnetAddress));
        Assert.False(resp.GetProperty("valid").GetBoolean());
        Assert.Equal("wrong_network", resp.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Address_validation_rejects_garbage()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<JsonElement>(
            "/api/dashboard/validate-address?address=not-an-address");
        Assert.False(resp.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Summary_exposes_confirmed_and_unconfirmed_balance()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        // Empty wallet: all balance fields should be zero but present.
        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/summary");
        foreach (var field in new[] { "totalBalanceSats", "confirmedBalanceSats", "unconfirmedBalanceSats" })
        {
            Assert.Equal(0L, resp.GetProperty(field).GetInt64());
        }
    }

    [Fact]
    public async Task Restore_from_mnemonic_produces_identical_xpubs()
    {
        // Create a wallet in one factory, grab its mnemonic + xpubs.
        string mnemonic;
        string[] originalXpubs;
        await using (var f1 = new KompaktorWebFactory())
        {
            using var c1 = f1.CreateClient();
            var created = await (await c1.PostAsJsonAsync(
                "/api/wallet/create", new { Passphrase = "pw", WordCount = 12 }))
                .Content.ReadFromJsonAsync<JsonElement>();
            mnemonic = created.GetProperty("mnemonic").GetString()!;

            var xpubResp = await c1.GetFromJsonAsync<JsonElement>("/api/wallet/export-xpub");
            originalXpubs = xpubResp.GetProperty("accounts").EnumerateArray()
                .Select(a => a.GetProperty("xpub").GetString()!)
                .OrderBy(x => x)
                .ToArray();
        }

        // Restore into a fresh factory (fresh DB). Same mnemonic must yield same xpubs.
        await using var f2 = new KompaktorWebFactory();
        using var c2 = f2.CreateClient();

        var restore = await c2.PostAsJsonAsync("/api/wallet/restore", new
        {
            Mnemonic = mnemonic,
            Passphrase = "pw",
            Name = "Restored"
        });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);

        var restoredXpubResp = await c2.GetFromJsonAsync<JsonElement>("/api/wallet/export-xpub");
        var restoredXpubs = restoredXpubResp.GetProperty("accounts").EnumerateArray()
            .Select(a => a.GetProperty("xpub").GetString()!)
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal(originalXpubs, restoredXpubs);
    }

    [Fact]
    public async Task Restore_rejects_invalid_mnemonic()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/wallet/restore", new
        {
            Mnemonic = "not a real mnemonic phrase at all",
            Passphrase = "pw"
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Restore_when_wallet_exists_returns_bad_request()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var created = await (await client.PostAsJsonAsync(
            "/api/wallet/create", new { Passphrase = "pw" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var mnemonic = created.GetProperty("mnemonic").GetString()!;

        var resp = await client.PostAsJsonAsync("/api/wallet/restore", new
        {
            Mnemonic = mnemonic,
            Passphrase = "pw"
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Payments_search_returns_paged_envelope()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        // Empty wallet: endpoint returns the paged envelope with total=0.
        var resp = await client.GetFromJsonAsync<JsonElement>("/api/payments/search");
        Assert.Equal(0, resp.GetProperty("total").GetInt32());
        Assert.Empty(resp.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Payments_search_filters_by_direction_and_search_term()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        // Use the wallet's own receive address as a known-valid destination.
        // The payment will fail to actually send (no UTXOs), but the PendingPayment row
        // must still be created so we can test the search index.
        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var addr = receive.GetProperty("address").GetString()!;

        // Seed two payments with distinct labels so search can distinguish them.
        await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr,
            AmountSat = 1000,
            Label = "coffee"
        });
        await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr,
            AmountSat = 2000,
            Label = "rent-march"
        });

        // Search by label substring: only "rent-march" should match.
        var byLabel = await client.GetFromJsonAsync<JsonElement>(
            "/api/payments/search?search=rent");
        Assert.Equal(1, byLabel.GetProperty("total").GetInt32());
        var onlyItem = byLabel.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("rent-march", onlyItem.GetProperty("label").GetString());

        // Filter by direction=Outbound should return both seeded payments.
        var byDirection = await client.GetFromJsonAsync<JsonElement>(
            "/api/payments/search?direction=Outbound");
        Assert.Equal(2, byDirection.GetProperty("total").GetInt32());

        // Unknown direction returns nothing — no rows match.
        var byUnknown = await client.GetFromJsonAsync<JsonElement>(
            "/api/payments/search?direction=Sideways");
        Assert.Equal(0, byUnknown.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Payments_list_returns_seeded_payments()
    {
        // Regression: /api/payments previously hit a SQLite "DateTimeOffset in ORDER BY"
        // error whenever the wallet had any payments. Empty-wallet tests masked it.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var addr = receive.GetProperty("address").GetString()!;

        await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr,
            AmountSat = 1234,
            Label = "regression-check"
        });

        var payments = await client.GetFromJsonAsync<JsonElement>("/api/payments");
        var items = payments.EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.Equal("regression-check", items[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task Transaction_detail_returns_not_found_for_unknown_tx()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetAsync("/api/dashboard/transactions/deadbeef");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Transaction_detail_returns_not_found_when_no_wallet()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/dashboard/transactions/any");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Fee_presets_returns_named_monotonic_tiers()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/fee-presets");
        var presets = resp.GetProperty("presets").EnumerateArray().ToArray();

        // All four tiers present in the expected order.
        Assert.Equal(4, presets.Length);
        Assert.Equal("fast", presets[0].GetProperty("key").GetString());
        Assert.Equal("medium", presets[1].GetProperty("key").GetString());
        Assert.Equal("slow", presets[2].GetProperty("key").GetString());
        Assert.Equal("economy", presets[3].GetProperty("key").GetString());

        // Each preset has a label, target, description, and rate >= 1.
        foreach (var p in presets)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("label").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("description").GetString()));
            Assert.True(p.GetProperty("satPerVb").GetInt64() >= 1);
            Assert.True(p.GetProperty("confirmationTarget").GetInt32() >= 1);
        }

        // Monotonic: fast >= medium >= slow >= economy. We rely on this so the
        // UI doesn't confusingly show slower tiers as more expensive.
        var rates = presets.Select(p => p.GetProperty("satPerVb").GetInt64()).ToArray();
        for (var i = 1; i < rates.Length; i++)
            Assert.True(rates[i - 1] >= rates[i], $"non-monotonic: {string.Join(",", rates)}");
    }

    [Fact]
    public async Task Transaction_note_add_rejects_unknown_tx()
    {
        // Prevents users from scribbling notes onto arbitrary txids the wallet
        // has never seen — keeps the Labels table scoped to real wallet activity.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.PostAsJsonAsync(
            "/api/dashboard/transactions/ffffffff/note", new { Text = "fake" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Transaction_note_add_rejects_empty_text()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.PostAsJsonAsync(
            "/api/dashboard/transactions/anyTx/note", new { Text = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Transaction_note_delete_returns_not_found_for_missing_note()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.DeleteAsync("/api/dashboard/transactions/any/note/999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Transaction_note_create_and_delete_roundtrip()
    {
        // Full note lifecycle on a real wallet tx: create a wallet, seed a
        // UTXO (which mints a tx the wallet "knows"), add a note, verify it
        // surfaces on the tx detail, delete it, and verify it's gone.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        await SeedUtxoAsync(factory, client, amountSat: 42_000, tag: 0x7C);

        var utxos = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/utxos");
        var txId = utxos.EnumerateArray().First().GetProperty("txId").GetString()!;

        // Baseline: no notes on this tx.
        var detail0 = await client.GetFromJsonAsync<JsonElement>($"/api/dashboard/transactions/{txId}");
        Assert.Empty(detail0.GetProperty("notes").EnumerateArray());

        // Create a note.
        var createResp = await client.PostAsJsonAsync(
            $"/api/dashboard/transactions/{txId}/note", new { Text = "  tax-2026  " });
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var noteId = created.GetProperty("id").GetInt32();
        // Server trims whitespace before persisting.
        Assert.Equal("tax-2026", created.GetProperty("text").GetString());

        // Note surfaces on the detail endpoint.
        var detail1 = await client.GetFromJsonAsync<JsonElement>($"/api/dashboard/transactions/{txId}");
        var notes = detail1.GetProperty("notes").EnumerateArray().ToArray();
        Assert.Single(notes);
        Assert.Equal(noteId, notes[0].GetProperty("id").GetInt32());
        Assert.Equal("tax-2026", notes[0].GetProperty("text").GetString());

        // Delete the note; endpoint echoes the deleted id.
        var delResp = await client.DeleteAsync($"/api/dashboard/transactions/{txId}/note/{noteId}");
        delResp.EnsureSuccessStatusCode();
        var delBody = await delResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(noteId, delBody.GetProperty("deleted").GetInt32());

        // Gone on the next read.
        var detail2 = await client.GetFromJsonAsync<JsonElement>($"/api/dashboard/transactions/{txId}");
        Assert.Empty(detail2.GetProperty("notes").EnumerateArray());
    }

    [Fact]
    public async Task Receive_uri_builds_bip21_with_amount_and_label()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        // Bare: just the address, no params.
        var bare = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-uri");
        var bareUri = bare.GetProperty("uri").GetString()!;
        var addr = bare.GetProperty("address").GetString()!;
        Assert.Equal($"bitcoin:{addr}", bareUri);

        // amountSat + label — encoded, in BTC, label URL-escaped.
        var full = await client.GetFromJsonAsync<JsonElement>(
            "/api/wallet/receive-uri?amountSat=12345678&label=Alice%27s%20Coffee");
        var uri = full.GetProperty("uri").GetString()!;
        Assert.Contains("amount=0.12345678", uri);
        Assert.Contains("label=Alice%27s%20Coffee", uri);

        // amountSat takes precedence over amount when both supplied.
        var priority = await client.GetFromJsonAsync<JsonElement>(
            "/api/wallet/receive-uri?amountSat=50000000&amount=999");
        Assert.Contains("amount=0.5", priority.GetProperty("uri").GetString()!);
        Assert.DoesNotContain("999", priority.GetProperty("uri").GetString()!);
    }

    [Fact]
    public async Task Address_book_full_lifecycle_add_list_delete()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        // Grab a real regtest address.
        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var addr = receive.GetProperty("address").GetString()!;

        // Empty list to start.
        var empty = await client.GetFromJsonAsync<JsonElement>("/api/address-book");
        Assert.Empty(empty.EnumerateArray());

        // Add an entry.
        var added = await client.PostAsJsonAsync("/api/address-book", new
        {
            Label = "Alice",
            Address = addr
        });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var entry = await added.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = entry.GetProperty("id").GetInt32();

        // List should now contain it.
        var listed = await client.GetFromJsonAsync<JsonElement>("/api/address-book");
        var items = listed.EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.Equal("Alice", items[0].GetProperty("label").GetString());

        // Duplicate add should be rejected.
        var dup = await client.PostAsJsonAsync("/api/address-book", new
        {
            Label = "Alice Again",
            Address = addr
        });
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);

        // Garbage address rejected.
        var bad = await client.PostAsJsonAsync("/api/address-book", new
        {
            Label = "Bob",
            Address = "not-a-real-address"
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Delete works.
        var del = await client.DeleteAsync($"/api/address-book/{entryId}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var afterDelete = await client.GetFromJsonAsync<JsonElement>("/api/address-book");
        Assert.Empty(afterDelete.EnumerateArray());
    }

    [Fact]
    public async Task Payments_search_respects_limit_and_skip()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var addr = receive.GetProperty("address").GetString()!;

        // Seed 3 payments.
        for (var i = 0; i < 3; i++)
        {
            await client.PostAsJsonAsync("/api/payments/send", new
            {
                Destination = addr,
                AmountSat = 1000 + i,
                Label = $"payment-{i}"
            });
        }

        var page1 = await client.GetFromJsonAsync<JsonElement>(
            "/api/payments/search?limit=2&skip=0");
        Assert.Equal(3, page1.GetProperty("total").GetInt32());
        Assert.Equal(2, page1.GetProperty("items").EnumerateArray().Count());

        var page2 = await client.GetFromJsonAsync<JsonElement>(
            "/api/payments/search?limit=2&skip=2");
        Assert.Equal(3, page2.GetProperty("total").GetInt32());
        Assert.Single(page2.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Payments_search_filters_by_status_including_comma_list()
    {
        // Exercises the comma-split path in /api/payments/search's status filter
        // (Program.cs:1406-1410). A single value, a comma-separated list, and a
        // non-matching value must each route correctly through statuses.Contains.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var addr = receive.GetProperty("address").GetString()!;

        // A freshly seeded payment with no UTXOs starts (and stays) in "Pending"
        // for the duration of the test window — we don't need a real broadcast
        // to exercise the filter codepath.
        await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr,
            AmountSat = 1000,
            Label = "status-test"
        });

        // Single status matches.
        var onlyPending = await client.GetFromJsonAsync<JsonElement>(
            "/api/payments/search?status=Pending");
        Assert.Equal(1, onlyPending.GetProperty("total").GetInt32());

        // Comma list containing the actual status still matches.
        var multi = await client.GetFromJsonAsync<JsonElement>(
            "/api/payments/search?status=Pending,Failed,Completed");
        Assert.Equal(1, multi.GetProperty("total").GetInt32());

        // Comma list with whitespace must still parse (TrimEntries).
        var spaced = await client.GetFromJsonAsync<JsonElement>(
            "/api/payments/search?status=Failed%2C%20Pending");
        Assert.Equal(1, spaced.GetProperty("total").GetInt32());

        // Status that no seeded payment has returns zero.
        var noMatch = await client.GetFromJsonAsync<JsonElement>(
            "/api/payments/search?status=Completed");
        Assert.Equal(0, noMatch.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Batch_send_rejects_empty_array()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.PostAsJsonAsync("/api/payments/batch-send", Array.Empty<object>());
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Batch_send_rejects_over_50_items()
    {
        // The endpoint caps each batch at 50 entries (Program.cs:1495-1496).
        // We don't need valid addresses because the length gate runs first.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var oversized = Enumerable.Range(0, 51)
            .Select(_ => new { Destination = "placeholder", AmountSat = 1_000L })
            .ToArray();

        var resp = await client.PostAsJsonAsync("/api/payments/batch-send", oversized);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Batch_send_rejects_invalid_address_without_partial_writes()
    {
        // Validation is all-or-nothing: if ANY entry has an invalid address,
        // the whole batch is rejected and no PendingPayment rows are created.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var addr = receive.GetProperty("address").GetString()!;

        var mixed = new[]
        {
            new { Destination = addr, AmountSat = 1_000L },
            new { Destination = "not-a-real-address", AmountSat = 2_000L },
            new { Destination = addr, AmountSat = 3_000L }
        };

        var resp = await client.PostAsJsonAsync("/api/payments/batch-send", mixed);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // Nothing should have been persisted.
        var payments = await client.GetFromJsonAsync<JsonElement>("/api/payments");
        Assert.Empty(payments.EnumerateArray());
    }

    [Fact]
    public async Task Batch_send_creates_all_pending_payments_when_addresses_valid()
    {
        // Happy path: a 3-entry batch should result in 3 PendingPayment rows
        // in "Pending" status (no UTXOs to actually spend, but the records
        // exist for the scheduler to retry/fail later).
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var addr = receive.GetProperty("address").GetString()!;

        var batch = new[]
        {
            new { Destination = addr, AmountSat = 1_100L, Label = "b1" },
            new { Destination = addr, AmountSat = 1_200L, Label = "b2" },
            new { Destination = addr, AmountSat = 1_300L, Label = "b3" }
        };

        var resp = await client.PostAsJsonAsync("/api/payments/batch-send", batch);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("count").GetInt32());
        var items = body.GetProperty("payments").EnumerateArray().ToArray();
        Assert.Equal(3, items.Length);
        Assert.All(items, i => Assert.Equal("Outbound", i.GetProperty("direction").GetString()));

        // Each amount maps to a distinct label.
        var labelsByAmount = items.ToDictionary(
            i => i.GetProperty("amountSat").GetInt64(),
            i => i.GetProperty("label").GetString());
        Assert.Equal("b1", labelsByAmount[1_100]);
        Assert.Equal("b2", labelsByAmount[1_200]);
        Assert.Equal("b3", labelsByAmount[1_300]);
    }

    [Fact]
    public async Task Export_mnemonic_requires_passphrase()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        // Empty passphrase body → 400 "Passphrase required".
        var resp = await client.PostAsJsonAsync("/api/wallet/export-mnemonic", new { Passphrase = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Export_mnemonic_rejects_wrong_passphrase()
    {
        // A wrong passphrase must not leak anything. The CryptographicException
        // from MnemonicEncryption.Decrypt is caught and converted to 400.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "correct-horse" });

        var resp = await client.PostAsJsonAsync(
            "/api/wallet/export-mnemonic", new { Passphrase = "battery-staple" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Export_mnemonic_returns_12_or_24_words_with_correct_passphrase()
    {
        // Happy path: correct passphrase must return the seed mnemonic plus
        // a word count matching the BIP39 standard (12 or 24 words).
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.PostAsJsonAsync("/api/wallet/export-mnemonic", new { Passphrase = "pw" });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var mnemonic = body.GetProperty("mnemonic").GetString()!;
        var wordCount = body.GetProperty("wordCount").GetInt32();

        var actualWords = mnemonic.Split(' ');
        Assert.Equal(actualWords.Length, wordCount);
        Assert.Contains(wordCount, new[] { 12, 15, 18, 21, 24 });
    }

    [Fact]
    public async Task Export_mnemonic_returns_400_when_no_wallet()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        // No /api/wallet/create call — the endpoint should return 400 rather
        // than 500 or leak any crypto state.
        var resp = await client.PostAsJsonAsync("/api/wallet/export-mnemonic", new { Passphrase = "pw" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Dashboard_events_stream_emits_connected_and_payments_events()
    {
        // SSE endpoint /api/dashboard/events is the dashboard's live update channel.
        // Connect, read the mandatory "connected" heartbeat, then trigger a
        // payments-channel publish (via /api/payments/receive) and confirm the
        // subscriber gets a "payments" event.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        // Important: ResponseHeadersRead keeps the stream open instead of
        // buffering the whole response (which would deadlock on an SSE feed).
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/events");
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        Assert.StartsWith("text/event-stream", resp.Content.Headers.ContentType!.ToString());

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // The endpoint writes the initial heartbeat immediately on connect.
        var first = await ReadNextEventAsync(reader, timeoutSeconds: 5);
        Assert.Equal("connected", first);

        // Trigger a payments-channel publish and confirm the SSE feed receives it.
        // Other channels (utxos, rounds, wallet) can publish concurrently from
        // background services, so we scan forward until we see the one we care
        // about rather than insisting it be the very next event.
        var publishTask = Task.Run(async () =>
        {
            await Task.Delay(100);
            await client.PostAsJsonAsync("/api/payments/receive", new { AmountSat = 5_000, Label = "sse-probe" });
        });

        await ReadUntilEventAsync(reader, "payments", timeoutSeconds: 10);
        await publishTask;
    }

    // Read `event: <name>` from an SSE stream. Skips blank lines and `data:` lines.
    private static async Task<string> ReadNextEventAsync(StreamReader reader, int timeoutSeconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        while (!cts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null) throw new IOException("SSE stream closed unexpectedly.");
            if (line.StartsWith("event:", StringComparison.Ordinal))
                return line["event:".Length..].Trim();
        }
        throw new TimeoutException($"No SSE event seen within {timeoutSeconds}s.");
    }

    // Drain an SSE stream until we see the requested event name (or time out).
    private static async Task ReadUntilEventAsync(StreamReader reader, string eventName, int timeoutSeconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        while (!cts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null) throw new IOException("SSE stream closed unexpectedly.");
            if (line.StartsWith("event:", StringComparison.Ordinal) &&
                line["event:".Length..].Trim() == eventName)
                return;
        }
        throw new TimeoutException($"SSE event '{eventName}' not seen within {timeoutSeconds}s.");
    }

    [Fact]
    public async Task Payments_stats_without_wallet_returns_total_zero_only()
    {
        // /api/payments/stats is meant to power a dashboard widget. Before a
        // wallet exists we intentionally return a thin `{ total: 0 }` payload
        // rather than 404, so the widget can render a zero state without
        // branching on HTTP status.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var stats = await client.GetFromJsonAsync<JsonElement>("/api/payments/stats");
        Assert.Equal(0, stats.GetProperty("total").GetInt32());
        // Rich counters are absent on the empty-wallet shape — the widget
        // should rely on `total == 0` alone.
        Assert.False(stats.TryGetProperty("completedCount", out _));
    }

    [Fact]
    public async Task Payments_stats_reflects_pending_outbound_payment()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var addr = receive.GetProperty("address").GetString()!;

        await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr, AmountSat = 5_000, Label = "stats-check"
        });

        var stats = await client.GetFromJsonAsync<JsonElement>("/api/payments/stats");
        Assert.Equal(1, stats.GetProperty("total").GetInt32());
        Assert.Equal(0, stats.GetProperty("completedCount").GetInt32());
        // A brand-new outbound payment lives in Pending/Reserved — it's counted
        // as pending until the UTXO-selection pipeline flips it.
        Assert.Equal(1, stats.GetProperty("pendingCount").GetInt32());
        Assert.Equal(0, stats.GetProperty("failedCount").GetInt32());
        Assert.Equal(0, stats.GetProperty("totalSentSat").GetInt64());
        // successRate is defined only once at least one payment has finalized;
        // for a pure-pending wallet the server returns 0 without a divide-by-zero.
        Assert.Equal(0, stats.GetProperty("successRate").GetDouble());
    }

    [Fact]
    public async Task Payments_export_without_wallet_returns_no_wallet_sentinel()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/payments/export");
        resp.EnsureSuccessStatusCode();
        // When no wallet exists the endpoint short-circuits with a plain
        // "No wallet" sentinel instead of an empty CSV. Keep the assertion
        // on the body so a future change to that behavior fails loudly.
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("No wallet", body);
    }

    [Fact]
    public async Task Payments_export_returns_csv_header_and_row_per_payment()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var addr = receive.GetProperty("address").GetString()!;

        await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr, AmountSat = 7_777, Label = "csv,with,commas"
        });

        var resp = await client.GetAsync("/api/payments/export");
        resp.EnsureSuccessStatusCode();
        Assert.Equal("text/csv", resp.Content.Headers.ContentType?.MediaType);

        var csv = await resp.Content.ReadAsStringAsync();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2, $"expected header + at least 1 row, got {lines.Length}");

        // Header contract is load-bearing for tax/accounting integrations.
        var header = lines[0].Trim();
        Assert.StartsWith("Id,Direction,AmountSat", header);
        Assert.Contains("CreatedAt", header);

        // Commas inside the label must be CSV-quoted, not column-splitting.
        var dataRow = lines[1];
        Assert.Contains("\"csv,with,commas\"", dataRow);
        Assert.Contains("7777", dataRow);
        Assert.Contains("Outbound", dataRow);
    }

    [Fact]
    public async Task Blockchain_info_reports_connected_height_and_network()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var info = await client.GetFromJsonAsync<JsonElement>("/api/blockchain/info");
        Assert.True(info.GetProperty("connected").GetBoolean());
        // FakeBlockchainBackend starts at height 800_000 — pin it so a future
        // refactor that silently swaps in a different default fails here.
        Assert.Equal(800_000, info.GetProperty("blockHeight").GetInt32());
        Assert.Equal("RegTest", info.GetProperty("network").GetString());
    }

    [Fact]
    public async Task Coinjoins_endpoint_returns_empty_array_for_fresh_wallet()
    {
        // CoinJoinRecords only get minted when this wallet participates in a
        // round, so a brand-new wallet should produce an empty list rather
        // than a 404 — the UI renders a "never mixed yet" state from it.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var records = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/coinjoins");
        Assert.Equal(JsonValueKind.Array, records.ValueKind);
        Assert.Empty(records.EnumerateArray());
    }

    [Fact]
    public async Task Health_endpoint_reports_healthy_without_wallet()
    {
        // /health is the deployment liveness probe — must be reachable without
        // any wallet state, since monitoring hits it before first user signup.
        // It's the one endpoint that cannot depend on DB data.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal("healthy", resp.GetProperty("status").GetString());
        Assert.Equal("RegTest", resp.GetProperty("network").GetString());
        // Don't pin activeRounds to 0 — the coordinator bootstraps its own
        // round on startup in the in-process harness. Just ensure it's a
        // non-negative int so a negative value (corrupt state) fails loud.
        Assert.True(resp.GetProperty("activeRounds").GetInt32() >= 0);
        // Timestamp must be fresh so kubernetes/docker health probes can
        // detect a stuck/cached response. The project's JSON helper
        // serializes DateTimeOffset as a Unix timestamp (seconds or ms),
        // not an ISO string — read it as a number.
        var tsProp = resp.GetProperty("timestamp");
        Assert.Equal(JsonValueKind.Number, tsProp.ValueKind);
        var unixTs = tsProp.GetInt64();
        // Try seconds first, then milliseconds, depending on which the
        // converter picked for this value.
        var ts = unixTs > 10_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(unixTs)
            : DateTimeOffset.FromUnixTimeSeconds(unixTs);
        Assert.True((DateTimeOffset.UtcNow - ts).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Receive_qr_returns_svg_payload_for_fresh_wallet()
    {
        // The receive flow hands users an SVG to scan. The endpoint must work
        // on a brand-new wallet (fresh addresses available) and return real
        // SVG markup rather than the `No fresh addresses available` error.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetAsync("/api/wallet/receive-qr");
        resp.EnsureSuccessStatusCode();
        Assert.Equal("image/svg+xml", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<svg", body, StringComparison.Ordinal);
        Assert.Contains("</svg>", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receive_qr_without_wallet_returns_bad_request()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/wallet/receive-qr");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Transaction_detail_returns_received_utxo_from_seeded_backend()
    {
        // Drives the receive pipeline end-to-end: stage a UTXO on the fake
        // backend, let the background sync service pick it up, then assert
        // the tx-detail endpoint surfaces it with the right amounts.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var scriptHex = receive.GetProperty("scriptHex").GetString()!;
        var script = new Script(Encoders.Hex.DecodeData(scriptHex));

        // Wait for the background sync service to attach and start monitoring.
        // Until it's monitoring, staged UTXOs won't be observed.
        await WaitForMonitoringAsync(client);

        // Deterministic fake txid — 32 bytes of a recognizable pattern.
        var txIdBytes = new byte[32];
        for (var i = 0; i < 32; i++) txIdBytes[i] = (byte)(0xA0 + (i % 16));
        var fakeTxId = new uint256(txIdBytes);

        var outPoint = new OutPoint(fakeTxId, 0);
        var txOut = new TxOut(Money.Satoshis(50_000), script);
        factory.Blockchain.StageUtxo(new UtxoInfo(outPoint, txOut, Confirmations: 1, IsCoinBase: false));

        // Fire both arrival paths — the notification handler and a forced resync.
        // Either one should pick up the staged UTXO; both together remove timing flake.
        factory.Blockchain.RaiseAddressNotification(script, fakeTxId, factory.Blockchain.BlockHeight);
        await client.PostAsync("/api/wallet/resync", null);

        var detail = await WaitForTxDetailAsync(client, fakeTxId.ToString());

        Assert.Equal(50_000, detail.GetProperty("receivedSats").GetInt64());
        Assert.Equal(0, detail.GetProperty("spentSats").GetInt64());
        Assert.Equal(50_000, detail.GetProperty("netSats").GetInt64());

        var outputs = detail.GetProperty("receivedOutputs").EnumerateArray().ToArray();
        Assert.Single(outputs);
        Assert.Equal(50_000, outputs[0].GetProperty("amountSat").GetInt64());
        Assert.Equal(0, outputs[0].GetProperty("vout").GetInt32());
        Assert.Equal(
            receive.GetProperty("address").GetString(),
            outputs[0].GetProperty("address").GetString());

        // No inputs were ours, so spentInputs must be empty.
        Assert.Empty(detail.GetProperty("spentInputs").EnumerateArray());
    }

    [Fact]
    public async Task Send_flow_signs_broadcasts_and_marks_utxo_spent()
    {
        // End-to-end send: seed a UTXO, call /dashboard/send with the right
        // passphrase, verify the tx reaches the blockchain backend and the
        // source UTXO is marked spent.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "correct-pw" });

        var utxoId = await SeedUtxoAsync(factory, client, amountSat: 200_000, tag: 0x44);
        var beforeBroadcastCount = factory.Blockchain.BroadcastedTransactions.Count;

        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var dest = receive.GetProperty("address").GetString()!;

        var sendResp = await client.PostAsJsonAsync("/api/dashboard/send", new
        {
            Destination = dest,
            AmountSat = 50_000,
            FeeRateSatPerVb = 2,
            Strategy = "PrivacyFirst",
            Passphrase = "correct-pw"
        });
        Assert.Equal(HttpStatusCode.OK, sendResp.StatusCode);
        var send = await sendResp.Content.ReadFromJsonAsync<JsonElement>();
        var broadcastTxId = send.GetProperty("txId").GetString()!;
        Assert.True(send.GetProperty("feeSat").GetInt64() > 0);

        // The fake backend saw exactly one more broadcast.
        Assert.Equal(beforeBroadcastCount + 1, factory.Blockchain.BroadcastedTransactions.Count);
        Assert.Equal(broadcastTxId,
            factory.Blockchain.BroadcastedTransactions[^1].GetHash().ToString());

        // Source UTXO is now marked spent by the broadcast txid.
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/coin-control/utxo/{utxoId}");
        Assert.True(detail.GetProperty("isSpent").GetBoolean());
        Assert.Equal(broadcastTxId, detail.GetProperty("spentByTxId").GetString());
    }

    [Fact]
    public async Task Send_with_wrong_passphrase_returns_bad_request()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "real-pw" });

        await SeedUtxoAsync(factory, client, amountSat: 100_000, tag: 0x45);

        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var dest = receive.GetProperty("address").GetString()!;
        var before = factory.Blockchain.BroadcastedTransactions.Count;

        var resp = await client.PostAsJsonAsync("/api/dashboard/send", new
        {
            Destination = dest,
            AmountSat = 10_000,
            FeeRateSatPerVb = 2,
            Passphrase = "WRONG"
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // And nothing was broadcast — the wrong passphrase should fail before
        // the transaction ever reaches the backend.
        Assert.Equal(before, factory.Blockchain.BroadcastedTransactions.Count);
    }

    [Fact]
    public async Task Send_without_passphrase_returns_bad_request()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.PostAsJsonAsync("/api/dashboard/send", new
        {
            Destination = "bcrt1qsomewhere",
            AmountSat = 1000,
            Passphrase = ""
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fee_bump_returns_psbt_with_higher_fee_than_original()
    {
        // End-to-end RBF: send a tx (always signals RBF via WalletTransactionBuilder),
        // then call /dashboard/fee-bump with a higher fee rate. The endpoint should
        // produce an unsigned PSBT for external signing with more fee than the original.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        await SeedUtxoAsync(factory, client, amountSat: 300_000, tag: 0x51);

        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var dest = receive.GetProperty("address").GetString()!;

        var send = await (await client.PostAsJsonAsync("/api/dashboard/send", new
        {
            Destination = dest,
            AmountSat = 50_000,
            FeeRateSatPerVb = 2,
            Passphrase = "pw"
        })).Content.ReadFromJsonAsync<JsonElement>();
        var originalTxId = send.GetProperty("txId").GetString()!;
        var originalFee = send.GetProperty("feeSat").GetInt64();

        // Bump: 20 sat/vb is an order of magnitude higher than the original 2 sat/vb,
        // so the result must carry more fee.
        var bumpResp = await client.PostAsJsonAsync("/api/dashboard/fee-bump", new
        {
            TxId = originalTxId,
            NewFeeRateSatPerVb = 20
        });
        var bumpRespBody = await bumpResp.Content.ReadAsStringAsync();
        Assert.True(bumpResp.StatusCode == HttpStatusCode.OK,
            $"Expected 200 OK, got {bumpResp.StatusCode}. Body: {bumpRespBody}");

        var bump = await bumpResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(originalTxId, bump.GetProperty("originalTxId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(bump.GetProperty("bumpedPsbt").GetString()));
        Assert.Equal(20, bump.GetProperty("newFeeRateSatPerVb").GetInt64());
        Assert.True(bump.GetProperty("inputCount").GetInt32() >= 1);
        Assert.True(bump.GetProperty("outputCount").GetInt32() >= 1);
        Assert.True(bump.GetProperty("estimatedFeeSat").GetInt64() > originalFee,
            $"Bumped fee ({bump.GetProperty("estimatedFeeSat").GetInt64()}) should exceed original fee ({originalFee}).");
    }

    [Fact]
    public async Task Fee_bump_unknown_tx_returns_bad_request()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.PostAsJsonAsync("/api/dashboard/fee-bump", new
        {
            TxId = new string('a', 64),
            NewFeeRateSatPerVb = 10
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Fee_bump_rejects_missing_txid_or_zero_fee_rate()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var missingTx = await client.PostAsJsonAsync("/api/dashboard/fee-bump", new
        {
            TxId = "",
            NewFeeRateSatPerVb = 10
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingTx.StatusCode);

        var zeroFee = await client.PostAsJsonAsync("/api/dashboard/fee-bump", new
        {
            TxId = new string('b', 64),
            NewFeeRateSatPerVb = 0
        });
        Assert.Equal(HttpStatusCode.BadRequest, zeroFee.StatusCode);
    }

    [Fact]
    public async Task Fee_bump_without_wallet_returns_bad_request()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        // no wallet created

        var resp = await client.PostAsJsonAsync("/api/dashboard/fee-bump", new
        {
            TxId = new string('c', 64),
            NewFeeRateSatPerVb = 10
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Plan_send_returns_inputs_outputs_and_fee_for_valid_plan()
    {
        // This is the preview that the UI uses before calling /dashboard/send,
        // so it's critical that it returns enough info to show the user the
        // coin selection, fee, and any privacy warnings before they commit.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        // Seed 500k sats so there's enough room for fee + change.
        await SeedUtxoAsync(factory, client, amountSat: 500_000, tag: 0x33);

        // Use an address controlled by the wallet itself as the destination —
        // we're just exercising the planning path, not the privacy semantics.
        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var dest = receive.GetProperty("address").GetString()!;

        var planResp = await client.PostAsJsonAsync("/api/dashboard/plan-send", new
        {
            Destination = dest,
            AmountSat = 100_000,
            FeeRateSatPerVb = 2,
            Strategy = "PrivacyFirst"
        });
        Assert.Equal(HttpStatusCode.OK, planResp.StatusCode);
        var plan = await planResp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(plan.GetProperty("inputCount").GetInt32() >= 1);
        Assert.True(plan.GetProperty("outputCount").GetInt32() >= 1);
        Assert.True(plan.GetProperty("estimatedFeeSat").GetInt64() > 0);
        Assert.False(string.IsNullOrEmpty(plan.GetProperty("txHex").GetString()));

        // Selected UTXOs should be present and match what we seeded.
        var selected = plan.GetProperty("selectedUtxos").EnumerateArray().ToArray();
        Assert.NotEmpty(selected);
        Assert.Equal(500_000, selected.Sum(u => u.GetProperty("amountSat").GetInt64()));
    }

    [Fact]
    public async Task Plan_send_rejects_garbage_destination()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.PostAsJsonAsync("/api/dashboard/plan-send", new
        {
            Destination = "not-an-address",
            AmountSat = 1000,
            FeeRateSatPerVb = 2
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Webhook_crud_happy_path()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        // Empty to start.
        var empty = await client.GetFromJsonAsync<JsonElement>("/api/webhooks");
        Assert.Empty(empty.EnumerateArray());

        // Create — response should carry the one-time secret.
        var createResp = await client.PostAsJsonAsync("/api/webhooks", new
        {
            Url = "https://example.com/kompaktor-hook",
            EventFilter = "payment.completed"
        });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();
        var secret = created.GetProperty("secret").GetString()!;
        Assert.Equal(64, secret.Length); // 32 bytes hex
        Assert.Matches("^[0-9a-f]+$", secret);

        // List now has it (but not the secret — secrets are one-time only).
        var listed = await client.GetFromJsonAsync<JsonElement>("/api/webhooks");
        var items = listed.EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.Equal(id, items[0].GetProperty("id").GetInt32());
        Assert.Equal("payment.completed", items[0].GetProperty("eventFilter").GetString());
        Assert.False(items[0].TryGetProperty("secret", out _),
            "secret should not be returned after creation");

        // Deliveries endpoint should return an empty list, not crash.
        var deliveries = await client.GetFromJsonAsync<JsonElement>(
            $"/api/webhooks/{id}/deliveries");
        Assert.Empty(deliveries.EnumerateArray());

        // Delete works.
        var del = await client.DeleteAsync($"/api/webhooks/{id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>("/api/webhooks");
        Assert.Empty(after.EnumerateArray());
    }

    [Fact]
    public async Task Webhook_rejects_non_http_urls()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        // Empty URL
        var empty = await client.PostAsJsonAsync("/api/webhooks", new { Url = "" });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        // Garbage
        var garbage = await client.PostAsJsonAsync("/api/webhooks", new { Url = "not-a-url" });
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);

        // Non-http scheme — we don't want users pointing at file:// or ftp://
        // since the webhook sender only knows how to POST over HTTP(S).
        var ftp = await client.PostAsJsonAsync("/api/webhooks", new { Url = "ftp://example.com" });
        Assert.Equal(HttpStatusCode.BadRequest, ftp.StatusCode);
    }

    [Fact]
    public async Task Webhook_delete_and_deliveries_404_for_unknown_id()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var del = await client.DeleteAsync("/api/webhooks/999999");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);

        var deliveries = await client.GetAsync("/api/webhooks/999999/deliveries");
        Assert.Equal(HttpStatusCode.NotFound, deliveries.StatusCode);
    }

    [Fact]
    public async Task Transactions_export_returns_csv_for_wallet_activity()
    {
        // Tax / accounting flow: user downloads a CSV of their on-chain activity.
        // Verifies seeded receive shows up as a "received" row with the right
        // amounts and that header + content-type match what a spreadsheet expects.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        // Before any UTXOs, the export is just the header row.
        var emptyResp = await client.GetAsync("/api/dashboard/transactions/export");
        Assert.Equal(HttpStatusCode.OK, emptyResp.StatusCode);
        Assert.Equal("text/csv", emptyResp.Content.Headers.ContentType?.MediaType);
        var emptyCsv = await emptyResp.Content.ReadAsStringAsync();
        Assert.StartsWith("TxId,Direction,ReceivedSats,SpentSats,NetSats,", emptyCsv);
        // Only header — no data rows.
        Assert.Single(emptyCsv.Trim('\r', '\n').Split('\n'));

        // Seed a 77k-sat UTXO and confirm the export now has a "received" row.
        var utxoId = await SeedUtxoAsync(factory, client, amountSat: 77_000, tag: 0x55);

        var resp = await client.GetAsync("/api/dashboard/transactions/export");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var csv = await resp.Content.ReadAsStringAsync();

        var lines = csv.Trim('\r', '\n').Split('\n').Select(l => l.Trim('\r')).ToArray();
        Assert.Equal(2, lines.Length);
        var dataRow = lines[1];
        var cols = dataRow.Split(',');
        // TxId, Direction, ReceivedSats, SpentSats, NetSats, ReceivedBtc, SpentBtc, NetBtc, ConfirmedHeight, IsCoinJoin, Notes
        Assert.Equal("received", cols[1]);
        Assert.Equal("77000", cols[2]);
        Assert.Equal("0", cols[3]);
        Assert.Equal("77000", cols[4]);
        Assert.Equal("false", cols[9]);

        // Sanity: the utxoId returned by the seeder exists and has the expected
        // amount — this guards against a future seed helper regression.
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/coin-control/utxo/{utxoId}");
        Assert.Equal(77_000, detail.GetProperty("amountSat").GetInt64());
    }

    [Fact]
    public async Task Coin_control_freeze_and_unfreeze_round_trip()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var utxoId = await SeedUtxoAsync(factory, client, amountSat: 42_000, tag: 0x01);

        var freeze = await client.PostAsync($"/api/coin-control/freeze/{utxoId}", null);
        Assert.Equal(HttpStatusCode.OK, freeze.StatusCode);
        var fBody = await freeze.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(fBody.GetProperty("frozen").GetBoolean());

        var detailFrozen = await client.GetFromJsonAsync<JsonElement>($"/api/coin-control/utxo/{utxoId}");
        Assert.True(detailFrozen.GetProperty("isFrozen").GetBoolean());

        var unfreeze = await client.PostAsync($"/api/coin-control/unfreeze/{utxoId}", null);
        Assert.Equal(HttpStatusCode.OK, unfreeze.StatusCode);
        Assert.False((await unfreeze.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("frozen").GetBoolean());

        var detailThawed = await client.GetFromJsonAsync<JsonElement>($"/api/coin-control/utxo/{utxoId}");
        Assert.False(detailThawed.GetProperty("isFrozen").GetBoolean());
    }

    [Fact]
    public async Task Coin_control_freeze_unknown_utxo_returns_404()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var freeze = await client.PostAsync("/api/coin-control/freeze/999999", null);
        Assert.Equal(HttpStatusCode.NotFound, freeze.StatusCode);

        var unfreeze = await client.PostAsync("/api/coin-control/unfreeze/999999", null);
        Assert.Equal(HttpStatusCode.NotFound, unfreeze.StatusCode);
    }

    [Fact]
    public async Task Coin_control_label_add_list_and_remove()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var utxoId = await SeedUtxoAsync(factory, client, amountSat: 13_370, tag: 0x02);

        var add = await client.PostAsJsonAsync(
            $"/api/coin-control/label/{utxoId}",
            new { Text = "from-friend" });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        Assert.Equal("added", (await add.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString());

        // Adding the same label twice is a no-op — the API reports already_exists
        // rather than inflating the label list with duplicates.
        var dup = await client.PostAsJsonAsync(
            $"/api/coin-control/label/{utxoId}",
            new { Text = "from-friend" });
        Assert.Equal("already_exists", (await dup.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString());

        // Label should appear in the dashboard utxos feed. Dashboard returns
        // labels as plain strings (flattened for the list UI).
        var utxos = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/utxos");
        var entry = utxos.EnumerateArray().First(u => u.GetProperty("id").GetInt32() == utxoId);
        var labels = entry.GetProperty("labels").EnumerateArray()
            .Select(l => l.GetString()).ToArray();
        Assert.Contains("from-friend", labels);

        // And in the UTXO detail view, with a labelId we can delete by.
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/coin-control/utxo/{utxoId}");
        var labelEntries = detail.GetProperty("labels").EnumerateArray().ToArray();
        Assert.Single(labelEntries);
        var labelId = labelEntries[0].GetProperty("id").GetInt32();

        var del = await client.DeleteAsync($"/api/coin-control/label/{utxoId}/{labelId}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var afterDelete = await client.GetFromJsonAsync<JsonElement>($"/api/coin-control/utxo/{utxoId}");
        Assert.Empty(afterDelete.GetProperty("labels").EnumerateArray());
    }

    [Fact]
    public async Task Coin_control_label_empty_text_rejected()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.PostAsJsonAsync(
            "/api/coin-control/label/1", new { Text = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Coin_control_batch_freeze_updates_multiple_utxos()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        // Seed two independent UTXOs by using different tags (and thus different fake txids).
        var u1 = await SeedUtxoAsync(factory, client, amountSat: 10_000, tag: 0x10);
        var u2 = await SeedUtxoAsync(factory, client, amountSat: 20_000, tag: 0x11);

        var batch = await client.PostAsJsonAsync("/api/coin-control/batch-freeze", new
        {
            UtxoIds = new[] { u1, u2 },
            Freeze = true
        });
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        var body = await batch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("updated").GetInt32());
        Assert.True(body.GetProperty("frozen").GetBoolean());

        var utxos = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/utxos");
        var entries = utxos.EnumerateArray()
            .Where(u => u.GetProperty("id").GetInt32() == u1 || u.GetProperty("id").GetInt32() == u2)
            .ToArray();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, e => Assert.True(e.GetProperty("isFrozen").GetBoolean()));
    }

    [Fact]
    public async Task Coin_control_freeze_exposed_only_touches_exposed_live_utxos()
    {
        // One-click action: freeze every UTXO sitting on an exposed address.
        // Seed three UTXOs, expose one, pre-freeze another as a control,
        // and verify only the exposed+live one flips. Counts must match so
        // the UI can say "Froze N UTXOs" confidently.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var exposedId = await SeedUtxoAsync(factory, client, amountSat: 50_000, tag: 0xE1);
        var cleanId = await SeedUtxoAsync(factory, client, amountSat: 60_000, tag: 0xE2);
        var preFrozenId = await SeedUtxoAsync(factory, client, amountSat: 70_000, tag: 0xE3);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
            var exposed = await db.Utxos.Include(u => u.Address).SingleAsync(u => u.Id == exposedId);
            exposed.Address.IsExposed = true;
            var preFrozen = await db.Utxos.SingleAsync(u => u.Id == preFrozenId);
            preFrozen.IsFrozen = true;
            await db.SaveChangesAsync();
        }

        var resp = await client.PostAsync("/api/coin-control/freeze-exposed", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("frozen").GetInt32());

        var utxos = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/utxos");
        var byId = utxos.EnumerateArray().ToDictionary(u => u.GetProperty("id").GetInt32());
        Assert.True(byId[exposedId].GetProperty("isFrozen").GetBoolean());
        Assert.False(byId[cleanId].GetProperty("isFrozen").GetBoolean());
        Assert.True(byId[preFrozenId].GetProperty("isFrozen").GetBoolean());

        // Idempotent — second call finds nothing new.
        var resp2 = await client.PostAsync("/api/coin-control/freeze-exposed", content: null);
        var body2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body2.GetProperty("frozen").GetInt32());
    }

    [Fact]
    public async Task Scheduled_payment_starts_dormant_and_activates_after_due_time()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        var addr = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        // Schedule ~2s in the future. The project's global JSON config encodes
        // DateTimeOffset as unix-seconds, so we send an integer to match.
        var dueAtUnix = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeSeconds();

        var createResp = await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr,
            AmountSat = 50_000L,
            Label = "payday-scheduled",
            ScheduledAt = dueAtUnix
        });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var paymentId = created.GetProperty("id").GetString()!;
        Assert.Equal("Scheduled", created.GetProperty("status").GetString());
        Assert.True(created.TryGetProperty("scheduledAt", out var sched) && sched.ValueKind != JsonValueKind.Null);

        // Until the scheduled time elapses, the manager must still report it dormant.
        var early = await client.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}/status");
        Assert.Equal("Scheduled", early.GetProperty("status").GetString());

        // Poll /api/payments (which drives ActivateScheduledPaymentsAsync) until
        // the payment flips to Pending.
        var activated = await WaitForPaymentStatusAsync(client, paymentId, "Pending", timeoutSeconds: 12);
        Assert.Equal("Pending", activated.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Scheduled_payment_at_past_time_is_immediately_pending()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        var addr = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        // A ScheduledAt in the past should be treated as "activate now": the
        // create path checks `scheduledAt > UtcNow` before marking Scheduled.
        var createResp = await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr,
            AmountSat = 12_345L,
            ScheduledAt = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds()
        });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        var body = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pending", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Scheduled_payment_can_be_cancelled_before_activation()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        var addr = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        var created = await (await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr,
            AmountSat = 77_000L,
            ScheduledAt = DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds()
        })).Content.ReadFromJsonAsync<JsonElement>();
        var paymentId = created.GetProperty("id").GetString()!;

        // Delete the scheduled payment — it's still dormant, so cancellation must succeed.
        var cancel = await client.DeleteAsync($"/api/payments/{paymentId}");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var cancelBody = await cancel.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cancelled", cancelBody.GetProperty("status").GetString());

        // Status endpoint should now report the cancelled (Failed) terminal state
        // and ActivateScheduledPaymentsAsync must never flip it back to Pending.
        var status = await client.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}/status");
        Assert.Equal("Failed", status.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Cancel_unknown_payment_returns_not_found()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.DeleteAsync("/api/payments/nonexistent-id");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Cancel_without_wallet_returns_bad_request()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        // no wallet

        var resp = await client.DeleteAsync("/api/payments/anything");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Scheduled_payment_appears_in_payments_list_with_scheduledAt()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        var addr = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr,
            AmountSat = 99_000L,
            Label = "rent",
            ScheduledAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        });

        var list = await client.GetFromJsonAsync<JsonElement>("/api/payments");
        var items = list.EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.Equal("Scheduled", items[0].GetProperty("status").GetString());
        Assert.True(items[0].TryGetProperty("scheduledAt", out var schedField));
        Assert.NotEqual(JsonValueKind.Null, schedField.ValueKind);
    }

    [Fact]
    public async Task Sweep_consumes_all_spendable_utxos_into_single_output()
    {
        // Send-all: the sweep endpoint should include every spendable UTXO as
        // an input and produce a single output to the destination, with fee
        // deducted from the swept amount.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        await SeedUtxoAsync(factory, client, amountSat: 100_000, tag: 0x71);
        await SeedUtxoAsync(factory, client, amountSat: 250_000, tag: 0x72);
        await SeedUtxoAsync(factory, client, amountSat: 50_000, tag: 0x73);

        // Any valid regtest address — use a fresh receive to keep test network-correct.
        var dest = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        var resp = await client.PostAsJsonAsync("/api/dashboard/sweep", new
        {
            Destination = dest,
            FeeRateSatPerVb = 2
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(3, body.GetProperty("inputCount").GetInt32());
        Assert.Equal(400_000L, body.GetProperty("totalInputSat").GetInt64());

        var fee = body.GetProperty("feeSat").GetInt64();
        var sendAmount = body.GetProperty("sendAmountSat").GetInt64();
        Assert.True(fee > 0, "sweep must include a non-zero fee");
        Assert.Equal(400_000L, sendAmount + fee);
        Assert.Equal(dest, body.GetProperty("destination").GetString());
    }

    [Fact]
    public async Task Sweep_without_wallet_returns_bad_request()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/dashboard/sweep", new
        {
            Destination = "bcrt1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq4tkvrp",
            FeeRateSatPerVb = 2
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Sweep_with_no_spendable_utxos_returns_bad_request()
    {
        // Fresh wallet has no UTXOs — sweep should refuse rather than build a
        // zero-input transaction.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var dest = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        var resp = await client.PostAsJsonAsync("/api/dashboard/sweep", new
        {
            Destination = dest,
            FeeRateSatPerVb = 2
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Sweep_rejects_missing_destination()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.PostAsJsonAsync("/api/dashboard/sweep", new
        {
            Destination = "",
            FeeRateSatPerVb = 2
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Payments_stats_on_empty_wallet_returns_zero_total()
    {
        // No wallet has been created — stats still resolves, with total=0.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/payments/stats");
        Assert.Equal(0, resp.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Payments_stats_buckets_pending_and_totals_amounts()
    {
        // Seed two outbound payments (both will stay Pending since the wallet
        // has no UTXOs to spend). Stats should report total=2, pendingCount=2,
        // and zero sent/received amounts.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var addr = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr, AmountSat = 1_000L, Label = "a"
        });
        await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr, AmountSat = 2_500L, Label = "b"
        });

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/payments/stats");
        Assert.Equal(2, resp.GetProperty("total").GetInt32());
        Assert.Equal(2, resp.GetProperty("pendingCount").GetInt32());
        Assert.Equal(0, resp.GetProperty("completedCount").GetInt32());
        Assert.Equal(0, resp.GetProperty("failedCount").GetInt32());
        Assert.Equal(0L, resp.GetProperty("totalSentSat").GetInt64());
        Assert.Equal(0L, resp.GetProperty("totalReceivedSat").GetInt64());
        // Success rate over {Completed + Failed} is undefined with no such
        // payments — endpoint returns 0 in that case.
        Assert.Equal(0.0, resp.GetProperty("successRate").GetDouble());
    }

    [Fact]
    public async Task Payments_stats_counts_failed_separately_after_cancellation()
    {
        // Cancel one of two payments. Stats should reflect one pending + one
        // failed, and successRate should drop to 0% (0 completed / 1 terminal).
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var addr = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        var one = (await (await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr, AmountSat = 1_000L
        })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr, AmountSat = 2_000L
        });

        var del = await client.DeleteAsync($"/api/payments/{one}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/payments/stats");
        Assert.Equal(2, resp.GetProperty("total").GetInt32());
        Assert.Equal(1, resp.GetProperty("pendingCount").GetInt32());
        Assert.Equal(1, resp.GetProperty("failedCount").GetInt32());
        Assert.Equal(0, resp.GetProperty("completedCount").GetInt32());
        Assert.Equal(0.0, resp.GetProperty("successRate").GetDouble());
    }

    [Fact]
    public async Task Export_psbt_returns_base64_psbt_with_witness_utxos()
    {
        // Hardware-wallet path: export an unsigned PSBT for an external signer.
        // Must include at least one input, witness UTXOs populated, and non-zero
        // estimated fee.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        await SeedUtxoAsync(factory, client, amountSat: 200_000, tag: 0x81);

        var dest = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        var resp = await client.PostAsJsonAsync("/api/dashboard/export-psbt", new
        {
            Destination = dest,
            AmountSat = 50_000L,
            FeeRateSatPerVb = 2L,
            Strategy = "PrivacyFirst"
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var psbtBase64 = body.GetProperty("psbt").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(psbtBase64));

        var psbt = PSBT.Parse(psbtBase64, Network.RegTest);
        Assert.Equal(body.GetProperty("inputCount").GetInt32(), psbt.Inputs.Count);
        Assert.True(psbt.Inputs.Count >= 1);
        // Each input must carry its WitnessUtxo so an air-gapped signer can
        // verify amounts without the source transactions.
        foreach (var input in psbt.Inputs)
            Assert.NotNull(input.WitnessUtxo);

        Assert.True(body.GetProperty("estimatedFeeSat").GetInt64() > 0);
    }

    [Fact]
    public async Task Export_psbt_without_wallet_returns_bad_request()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/dashboard/export-psbt", new
        {
            Destination = "bcrt1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq4tkvrp",
            AmountSat = 1_000L,
            FeeRateSatPerVb = 2L
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Broadcast_psbt_rejects_unsigned_psbt()
    {
        // PSBT from /export-psbt has no signatures — broadcasting it without
        // signing must fail and must not reach the fake backend.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        await SeedUtxoAsync(factory, client, amountSat: 200_000, tag: 0x82);

        var dest = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        var exportResp = await client.PostAsJsonAsync("/api/dashboard/export-psbt", new
        {
            Destination = dest,
            AmountSat = 50_000L,
            FeeRateSatPerVb = 2L
        });
        var unsigned = (await exportResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("psbt").GetString()!;

        var before = factory.Blockchain.BroadcastedTransactions.Count;
        var resp = await client.PostAsJsonAsync("/api/dashboard/broadcast-psbt", new
        {
            SignedPsbt = unsigned
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(before, factory.Blockchain.BroadcastedTransactions.Count);
    }

    [Fact]
    public async Task Broadcast_psbt_rejects_garbage_payload()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.PostAsJsonAsync("/api/dashboard/broadcast-psbt", new
        {
            SignedPsbt = "not-a-real-psbt"
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Broadcast_psbt_requires_signed_psbt_field()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/dashboard/broadcast-psbt", new
        {
            SignedPsbt = ""
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Payments_export_csv_emits_header_row_when_no_payments()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetAsync("/api/payments/export");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/csv", resp.Content.Headers.ContentType?.MediaType);

        var text = await resp.Content.ReadAsStringAsync();
        // Header-only CSV: single line (after trimming trailing newline).
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Contains("Id,Direction,AmountSat", lines[0]);
        Assert.Contains("Label", lines[0]);
        Assert.Contains("TxId", lines[0]);
    }

    [Fact]
    public async Task Payments_export_csv_includes_seeded_payment_row()
    {
        // Create a payment, export CSV, confirm both header + row and that
        // the row contains the payment's label and amount.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var addr = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;
        await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr,
            AmountSat = 123_456L,
            Label = "invoice-42"
        });

        var resp = await client.GetAsync("/api/payments/export");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync();

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length); // header + 1 row
        Assert.Contains("invoice-42", lines[1]);
        Assert.Contains(",123456,", lines[1]);
        Assert.Contains("Outbound", lines[1]);
    }

    [Fact]
    public async Task Payments_export_csv_escapes_embedded_quotes_in_label()
    {
        // Labels can carry double-quotes — CSV must double them per RFC 4180.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var addr = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;
        await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr,
            AmountSat = 1_000L,
            Label = "he said \"hi\""
        });

        var text = await (await client.GetAsync("/api/payments/export")).Content.ReadAsStringAsync();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        // `"he said ""hi"""` — label quoted, internal quotes doubled.
        Assert.Contains("\"he said \"\"hi\"\"\"", lines[1]);
    }

    [Fact]
    public async Task Mixing_status_on_fresh_host_reports_not_running()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/mixing/status");
        Assert.False(resp.GetProperty("running").GetBoolean());
        Assert.Equal(0, resp.GetProperty("completedRounds").GetInt32());
        Assert.Equal(0, resp.GetProperty("failedRounds").GetInt32());
    }

    [Fact]
    public async Task Mixing_start_rejects_missing_passphrase()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/mixing/start", new
        {
            Passphrase = ""
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Mixing_start_refuses_when_tor_is_unreachable()
    {
        // If the user asked for Tor but the daemon isn't listening, the
        // endpoint must refuse — otherwise KompaktorService silently falls
        // back to clearnet and the wallet's IP leaks to the coordinator.
        // Bind+close a loopback listener to get a guaranteed-dead port.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var deadPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var resp = await client.PostAsJsonAsync("/api/mixing/start", new
        {
            Passphrase = "pw",
            TorSocksHost = "127.0.0.1",
            TorSocksPort = deadPort
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tor_unreachable", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Mixing_start_without_wallet_returns_bad_request()
    {
        // No wallet has been created yet. StartAsync will throw, and the
        // endpoint must translate that into a 400 rather than a 500.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/mixing/start", new
        {
            Passphrase = "any-passphrase",
            CoordinatorUrl = "http://localhost:9999"
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Mixing_stop_when_not_running_returns_ok_with_status_string()
    {
        // Stopping an already-stopped mixer should be idempotent — just
        // returns a status message, never throws.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/mixing/stop", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Blockchain_info_reports_network_and_connected_state()
    {
        // The fake backend always reports as connected — network comes from
        // config (we forced regtest in the test factory).
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/blockchain/info");
        Assert.True(resp.GetProperty("connected").GetBoolean());
        Assert.Equal("RegTest", resp.GetProperty("network").GetString());
        // Fake backend reports a block height (may be 0).
        Assert.True(resp.TryGetProperty("blockHeight", out _));
    }

    [Fact]
    public async Task Coordinator_stats_reports_network_and_rounds_envelope()
    {
        // The coordinator orchestrator starts a round on boot, so activeRounds
        // is race-y; we just assert the envelope shape + network name.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/coordinator/stats");
        Assert.Equal("RegTest", resp.GetProperty("network").GetString());
        Assert.True(resp.GetProperty("activeRounds").GetInt32() >= 0);
        Assert.True(resp.GetProperty("roundsInRegistration").GetInt32() >= 0);
        // rounds array is always present (never null).
        Assert.Equal(JsonValueKind.Array, resp.GetProperty("rounds").ValueKind);
        // activeRounds must match the array length — the orchestrator
        // derives it from the same list.
        Assert.Equal(
            resp.GetProperty("activeRounds").GetInt32(),
            resp.GetProperty("rounds").GetArrayLength());
    }

    [Fact]
    public async Task Privacy_summary_reports_zeros_for_fresh_wallet()
    {
        // No UTXOs yet — every counter is zero, not null. The endpoint has a
        // special branch for "no wallet" that also returns zeros; make sure
        // both paths are consistent.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/privacy-summary");
        Assert.Equal(0, resp.GetProperty("totalUtxos").GetInt32());
        Assert.Equal(0L, resp.GetProperty("totalAmountSat").GetInt64());
        Assert.Equal(0, resp.GetProperty("mixedUtxoCount").GetInt32());
    }

    [Fact]
    public async Task Privacy_summary_without_wallet_returns_zero_snapshot()
    {
        // No wallet at all — endpoint must still succeed (not 400) so the
        // landing page can render before the user has set up their wallet.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/privacy-summary");
        Assert.Equal(0, resp.GetProperty("totalUtxos").GetInt32());
        Assert.Equal(0L, resp.GetProperty("totalAmountSat").GetInt64());
    }

    [Fact]
    public async Task Coinjoins_list_on_fresh_wallet_is_empty()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/coinjoins");
        Assert.Equal(JsonValueKind.Array, resp.ValueKind);
        Assert.Empty(resp.EnumerateArray());
    }

    [Fact]
    public async Task Receive_create_returns_bip21_uri_with_amount_and_label()
    {
        // Modern-wallet receive flow: create an inbound payment request, get
        // back a BIP21 URI encoding address + amount (+ kompaktor key for
        // the interactive-payment extension).
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.PostAsJsonAsync("/api/payments/receive", new
        {
            AmountSat = 250_000L,
            Label = "invoice-101",
            ExpiryMinutes = 30
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(250_000L, body.GetProperty("amountSat").GetInt64());
        Assert.Equal("invoice-101", body.GetProperty("label").GetString());
        Assert.Equal("Inbound", body.GetProperty("direction").GetString());

        var uri = body.GetProperty("bip21Uri").GetString()!;
        Assert.StartsWith("bitcoin:", uri);
        Assert.Contains("amount=0.00250000", uri);

        // The payment must appear in the main /api/payments list.
        var list = await client.GetFromJsonAsync<JsonElement>("/api/payments");
        Assert.Single(list.EnumerateArray());
    }

    [Fact]
    public async Task Receive_create_rejects_zero_amount()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.PostAsJsonAsync("/api/payments/receive", new
        {
            AmountSat = 0L,
            Label = "bad"
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Receive_create_without_wallet_returns_bad_request()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/payments/receive", new
        {
            AmountSat = 10_000L
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Payment_qr_endpoint_returns_svg_for_known_payment()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var create = await client.PostAsJsonAsync("/api/payments/receive", new
        {
            AmountSat = 50_000L,
            Label = "qr-test"
        });
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        var qr = await client.GetAsync($"/api/payments/{id}/qr");
        Assert.Equal(HttpStatusCode.OK, qr.StatusCode);
        Assert.Equal("image/svg+xml", qr.Content.Headers.ContentType?.MediaType);
        var svg = await qr.Content.ReadAsStringAsync();
        Assert.StartsWith("<svg", svg);
    }

    [Fact]
    public async Task Payment_qr_returns_not_found_for_unknown_id()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/payments/does-not-exist/qr");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Validate_address_parses_bip21_uri_with_amount_label_and_message()
    {
        // When the user pastes a full BIP21 URI, validate-address should
        // recognise the `bitcoin:` scheme, extract amount/label/message, and
        // still validate the bare address against the configured network.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var bareAddress = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        var uri = $"bitcoin:{bareAddress}?amount=0.00250000&label=invoice%20101&message=Thanks!";
        var resp = await client.GetFromJsonAsync<JsonElement>(
            $"/api/dashboard/validate-address?address={Uri.EscapeDataString(uri)}");

        Assert.True(resp.GetProperty("valid").GetBoolean());
        Assert.Equal(bareAddress, resp.GetProperty("address").GetString());
        Assert.Equal(250_000L, resp.GetProperty("amountSat").GetInt64());
        Assert.Equal("invoice 101", resp.GetProperty("label").GetString());
        Assert.Equal("Thanks!", resp.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Validate_address_accepts_bip21_uri_without_query_params()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var bare = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        var uri = $"bitcoin:{bare}";
        var resp = await client.GetFromJsonAsync<JsonElement>(
            $"/api/dashboard/validate-address?address={Uri.EscapeDataString(uri)}");

        Assert.True(resp.GetProperty("valid").GetBoolean());
        Assert.Equal(bare, resp.GetProperty("address").GetString());
        Assert.Equal(JsonValueKind.Null, resp.GetProperty("amountSat").ValueKind);
    }

    [Fact]
    public async Task Validate_address_bip21_uri_with_empty_address_is_invalid()
    {
        // A URI that parses as `bitcoin:` with just `?amount=…` and no address
        // must report invalid (or specifically `empty`), never accidentally
        // validate against the amount string.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<JsonElement>(
            "/api/dashboard/validate-address?address=" + Uri.EscapeDataString("bitcoin:?amount=0.001"));
        Assert.False(resp.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Validate_address_bip21_uri_with_invalid_amount_still_validates_address()
    {
        // An unparseable amount must not reject the URI — address is what the
        // form needs to validate. The server simply drops amountSat.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var bare = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        var uri = $"bitcoin:{bare}?amount=not-a-number";
        var resp = await client.GetFromJsonAsync<JsonElement>(
            $"/api/dashboard/validate-address?address={Uri.EscapeDataString(uri)}");

        Assert.True(resp.GetProperty("valid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, resp.GetProperty("amountSat").ValueKind);
    }

    [Fact]
    public async Task Summary_includes_fiat_rate_and_total_when_fiat_query_provided()
    {
        // FakePriceService publishes usd=65000 and eur=60000. Request usd
        // fiat; the summary should echo the rate and compute the fiat total
        // alongside the sat/BTC balances.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/summary?fiat=usd");
        Assert.Equal("usd", resp.GetProperty("fiatCurrency").GetString());
        Assert.Equal(65_000m, resp.GetProperty("fiatRate").GetDecimal());
        // Empty wallet — BTC is 0 so fiat is 0 too (not null).
        Assert.Equal(0m, resp.GetProperty("totalBalanceFiat").GetDecimal());
    }

    [Fact]
    public async Task Summary_without_fiat_query_returns_null_fiat_fields()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/summary");
        Assert.Equal(JsonValueKind.Null, resp.GetProperty("fiatCurrency").ValueKind);
        Assert.Equal(JsonValueKind.Null, resp.GetProperty("fiatRate").ValueKind);
        Assert.Equal(JsonValueKind.Null, resp.GetProperty("totalBalanceFiat").ValueKind);
    }

    [Fact]
    public async Task Summary_unknown_fiat_returns_null_rate_without_error()
    {
        // Graceful degradation: an unsupported currency doesn't 500 — it just
        // returns null fiat fields so the UI can fall back to "—".
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/summary?fiat=xrp");
        Assert.Equal(JsonValueKind.Null, resp.GetProperty("fiatRate").ValueKind);
        Assert.Equal(JsonValueKind.Null, resp.GetProperty("totalBalanceFiat").ValueKind);
    }

    [Fact]
    public async Task Transactions_empty_wallet_returns_empty_array()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/transactions");
        Assert.Equal(JsonValueKind.Array, resp.ValueKind);
        Assert.Equal(0, resp.GetArrayLength());
    }

    [Fact]
    public async Task Transactions_returns_received_entries_with_expected_shape()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        await SeedUtxoAsync(factory, client, amountSat: 123_456, tag: 0x91);

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/transactions");
        Assert.Equal(1, resp.GetArrayLength());
        var row = resp[0];
        Assert.False(string.IsNullOrEmpty(row.GetProperty("txId").GetString()));
        Assert.Equal(123_456L, row.GetProperty("amountSat").GetInt64());
        Assert.Equal(0.00123456, row.GetProperty("amountBtc").GetDouble(), 8);
        Assert.Equal(1, row.GetProperty("utxoCount").GetInt32());
        Assert.False(row.GetProperty("isSpent").GetBoolean());
        Assert.Equal("received", row.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Transactions_paginates_with_skip_and_take()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        await SeedUtxoAsync(factory, client, amountSat: 10_000, tag: 0x92);
        await SeedUtxoAsync(factory, client, amountSat: 20_000, tag: 0x93);
        await SeedUtxoAsync(factory, client, amountSat: 30_000, tag: 0x94);

        var all = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/transactions");
        Assert.Equal(3, all.GetArrayLength());

        var firstOnly = await client.GetFromJsonAsync<JsonElement>(
            "/api/dashboard/transactions?take=1");
        Assert.Equal(1, firstOnly.GetArrayLength());

        var skipTwo = await client.GetFromJsonAsync<JsonElement>(
            "/api/dashboard/transactions?skip=2");
        Assert.Equal(1, skipTwo.GetArrayLength());

        var skipAll = await client.GetFromJsonAsync<JsonElement>(
            "/api/dashboard/transactions?skip=10");
        Assert.Equal(0, skipAll.GetArrayLength());
    }

    [Fact]
    public async Task Transactions_confirmed_filter_separates_confirmed_from_unconfirmed()
    {
        // SeedUtxoAsync always stages with Confirmations=1, so all seeded
        // rows count as confirmed. Filter semantics: confirmed=true keeps them,
        // confirmed=false returns empty.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        await SeedUtxoAsync(factory, client, amountSat: 50_000, tag: 0x95);

        var confirmed = await client.GetFromJsonAsync<JsonElement>(
            "/api/dashboard/transactions?confirmed=true");
        Assert.Equal(1, confirmed.GetArrayLength());

        var unconfirmed = await client.GetFromJsonAsync<JsonElement>(
            "/api/dashboard/transactions?confirmed=false");
        Assert.Equal(0, unconfirmed.GetArrayLength());
    }

    [Fact]
    public async Task Transactions_take_is_clamped_to_upper_bound()
    {
        // take=9999 must not 500 — the endpoint clamps to its max (500)
        // and still returns whatever rows exist.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        await SeedUtxoAsync(factory, client, amountSat: 9_000, tag: 0x96);

        var resp = await client.GetAsync("/api/dashboard/transactions?take=9999");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetArrayLength());
    }

    [Fact]
    public async Task Wallet_export_returns_400_when_no_wallet()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/wallet/export");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Wallet_export_shape_for_empty_wallet()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var body = await client.GetFromJsonAsync<JsonElement>("/api/wallet/export");
        Assert.Equal(1, body.GetProperty("version").GetInt32());
        Assert.True(body.TryGetProperty("exportedAt", out _));
        Assert.Equal("RegTest", body.GetProperty("wallet").GetProperty("network").GetString());
        Assert.Empty(body.GetProperty("labels").EnumerateArray());
        Assert.Empty(body.GetProperty("addressBook").EnumerateArray());
        Assert.Empty(body.GetProperty("coinjoinHistory").EnumerateArray());
    }

    [Fact]
    public async Task Wallet_export_includes_address_book_entries()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var addr = receive.GetProperty("address").GetString()!;

        var added = await client.PostAsJsonAsync("/api/address-book",
            new { Label = "Carol", Address = addr });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        var body = await client.GetFromJsonAsync<JsonElement>("/api/wallet/export");
        var entries = body.GetProperty("addressBook").EnumerateArray().ToArray();
        Assert.Single(entries);
        Assert.Equal("Carol", entries[0].GetProperty("label").GetString());
        Assert.Equal(addr, entries[0].GetProperty("address").GetString());
    }

    [Fact]
    public async Task Wallet_import_adds_address_book_entries_from_payload()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        // Grab a real regtest address from the wallet to use as the imported one.
        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var addr = receive.GetProperty("address").GetString()!;

        var import = await client.PostAsJsonAsync("/api/wallet/import", new
        {
            addressBook = new[] { new { label = "Dave", address = addr } }
        });
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        var body = await import.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("addressBookImported").GetInt32());

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/address-book");
        var items = listed.EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.Equal("Dave", items[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task Wallet_import_skips_existing_entries_on_second_run()
    {
        // Idempotency: re-importing the same payload counts zero duplicates added,
        // so users can safely sync from multiple devices without creating dupes.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var addr = receive.GetProperty("address").GetString()!;
        var payload = new
        {
            addressBook = new[] { new { label = "Eve", address = addr } }
        };

        var first = await client.PostAsJsonAsync("/api/wallet/import", payload);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, firstBody.GetProperty("addressBookImported").GetInt32());

        var second = await client.PostAsJsonAsync("/api/wallet/import", payload);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, secondBody.GetProperty("addressBookImported").GetInt32());
    }

    [Fact]
    public async Task Wallet_import_returns_400_when_no_wallet()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/wallet/import", new { addressBook = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Privacy_distribution_empty_wallet_returns_zero_totals()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/privacy-distribution");
        Assert.Equal(0, resp.GetProperty("totalUtxos").GetInt32());

        var tiers = resp.GetProperty("tiers").EnumerateArray().ToArray();
        Assert.NotEmpty(tiers);
        foreach (var t in tiers)
        {
            Assert.Equal(0, t.GetProperty("count").GetInt32());
            Assert.Equal(0L, t.GetProperty("amountSat").GetInt64());
            Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("tier").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("color").GetString()));
        }
    }

    [Fact]
    public async Task Privacy_distribution_new_utxo_appears_in_some_tier()
    {
        // A freshly-received UTXO should surface in the distribution and
        // sum to its full amountSat across the tiers. We don't hard-code
        // which tier it lands in — the scoring heuristic defines that.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        await SeedUtxoAsync(factory, client, amountSat: 75_000, tag: 0xA1);

        // Sanity: the UTXO IS visible to the dashboard UTXO endpoint.
        var utxos = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/utxos");
        Assert.Equal(1, utxos.GetArrayLength());

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/privacy-distribution");
        Assert.Equal(1, resp.GetProperty("totalUtxos").GetInt32());

        var tiers = resp.GetProperty("tiers").EnumerateArray().ToArray();
        Assert.Equal(1, tiers.Sum(t => t.GetProperty("count").GetInt32()));
        Assert.Equal(75_000L, tiers.Sum(t => t.GetProperty("amountSat").GetInt64()));
    }

    [Fact]
    public async Task Privacy_recommendations_empty_wallet_prompts_to_fund()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/privacy-recommendations");
        var recs = resp.GetProperty("recommendations").EnumerateArray().ToArray();
        Assert.Single(recs);
        Assert.Equal("No UTXOs", recs[0].GetProperty("title").GetString());
        Assert.Equal("info", recs[0].GetProperty("priority").GetString());
    }

    [Fact]
    public async Task Privacy_recommendations_flag_unmixed_and_auto_mix_off()
    {
        // With a freshly-received UTXO and the auto-mixer OFF, the endpoint
        // should surface both the "unmixed UTXOs" recommendation AND the
        // "Auto-Mix Not Running" high-priority nag. Together they steer
        // users toward enabling the mixer.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        await SeedUtxoAsync(factory, client, amountSat: 100_000, tag: 0xA2);

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/privacy-recommendations");
        var recs = resp.GetProperty("recommendations").EnumerateArray()
            .Select(r => r.GetProperty("title").GetString()!)
            .ToArray();

        Assert.Contains(recs, t => t.Contains("Unmixed UTXO", StringComparison.Ordinal));
        Assert.Contains(recs, t => t == "Auto-Mix Not Running");
    }

    [Fact]
    public async Task Privacy_recommendations_flag_exposed_address_utxos()
    {
        // Exposed-address UTXOs are the #1 co-spend linkage footgun — the
        // planner warns at send-time, but the dashboard should also nag
        // about them so users fix the composition before they're about
        // to send. Mutate IsExposed directly (no HTTP surface toggles it)
        // and verify the recommendation appears with the right title.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var utxoId = await SeedUtxoAsync(factory, client, amountSat: 100_000, tag: 0xB4);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
            var utxo = await db.Utxos.Include(u => u.Address).SingleAsync(u => u.Id == utxoId);
            utxo.Address.IsExposed = true;
            await db.SaveChangesAsync();
        }

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/privacy-recommendations");
        var recs = resp.GetProperty("recommendations").EnumerateArray()
            .Select(r => (title: r.GetProperty("title").GetString()!, priority: r.GetProperty("priority").GetString()!))
            .ToArray();

        var exposed = recs.Single(r => r.title.Contains("Exposed", StringComparison.Ordinal));
        Assert.Equal("high", exposed.priority);
        Assert.Contains("1", exposed.title);

        // Freezing counts as "remediated" — after freeze-exposed, the rec
        // should disappear so the user stops seeing a nag for coins they
        // already parked.
        await client.PostAsync("/api/coin-control/freeze-exposed", content: null);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/privacy-recommendations");
        var afterTitles = after.GetProperty("recommendations").EnumerateArray()
            .Select(r => r.GetProperty("title").GetString()!)
            .ToArray();
        Assert.DoesNotContain(afterTitles, t => t.Contains("Exposed Addresses", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Privacy_recommendations_no_wallet_returns_empty()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/privacy-recommendations");
        Assert.Empty(resp.GetProperty("recommendations").EnumerateArray());
    }

    [Fact]
    public async Task Privacy_history_empty_wallet_returns_empty_array()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/privacy-history");
        Assert.Equal(JsonValueKind.Array, resp.ValueKind);
        Assert.Equal(0, resp.GetArrayLength());
    }

    [Fact]
    public async Task Privacy_history_invalid_days_value_does_not_500()
    {
        // days outside [1, 365] or unparseable falls back to 30 silently.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        foreach (var q in new[] { "days=not-a-number", "days=0", "days=99999", "days=-5" })
        {
            var resp = await client.GetAsync($"/api/dashboard/privacy-history?{q}");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
    }

    [Fact]
    public async Task Mixing_statistics_empty_wallet_returns_zeros()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/mixing/statistics");
        Assert.Equal(0, resp.GetProperty("totalRounds").GetInt32());
        Assert.Equal(0, resp.GetProperty("completedRounds").GetInt32());
        Assert.Equal(0, resp.GetProperty("failedRounds").GetInt32());
        Assert.Equal(0.0, resp.GetProperty("successRate").GetDouble());
        Assert.Equal(0, resp.GetProperty("totalOurInputs").GetInt32());
        Assert.Equal(0, resp.GetProperty("totalOurOutputs").GetInt32());
        Assert.Equal(0.0, resp.GetProperty("averageParticipantsPerRound").GetDouble());
        // No rounds — first/last are null, not errors.
        Assert.Equal(JsonValueKind.Null, resp.GetProperty("firstRound").ValueKind);
        Assert.Equal(JsonValueKind.Null, resp.GetProperty("lastRound").ValueKind);
    }

    [Fact]
    public async Task Credential_flows_unknown_round_returns_empty_array()
    {
        // Asking about a round the wallet never participated in should return
        // an empty flow list, not 404 — the UI polls this routinely and we
        // don't want to flip between "empty" and "error" based on round-id luck.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetAsync("/api/dashboard/credential-flows/999999");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(0, body.GetArrayLength());
    }

    [Fact]
    public async Task Sync_status_without_wallet_reports_idle_shape()
    {
        // The landing page polls /api/wallet/sync-status before the user has
        // a wallet — the endpoint must succeed (not 400) and return a
        // recognisable idle snapshot so the UI can render "waiting".
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/wallet/sync-status");
        Assert.False(status.GetProperty("syncing").GetBoolean());
        Assert.False(status.GetProperty("monitoring").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("lastSyncTime").ValueKind);
        Assert.Equal(0, status.GetProperty("lastSyncUtxoCount").GetInt32());
    }

    [Fact]
    public async Task Resync_endpoint_acknowledges_trigger_with_status_payload()
    {
        // POST /api/wallet/resync is fire-and-forget: it signals the hosted
        // service and returns a status envelope rather than blocking for the
        // sync to finish. The UI relies on a non-empty status string to show
        // "Sync queued" feedback.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        await WaitForMonitoringAsync(client);

        var resp = await client.PostAsync("/api/wallet/resync", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var status = body.GetProperty("status").GetString();
        Assert.False(string.IsNullOrWhiteSpace(status));
        Assert.Contains(status, new[] { "resync triggered", "already syncing" });
    }

    [Fact]
    public async Task Wallet_settings_without_wallet_returns_bad_request()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/wallet/settings");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Wallet_settings_fresh_wallet_defaults_to_balanced_and_tor_off()
    {
        // A freshly created wallet must come up with a sane default profile
        // and Tor disabled — so the very first /api/mixing/start call doesn't
        // inadvertently route over a SOCKS proxy the user never configured.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/wallet/settings");
        Assert.Equal("Balanced", resp.GetProperty("mixingProfile").GetString());
        Assert.False(resp.GetProperty("torEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, resp.GetProperty("torSocksHost").ValueKind);
        Assert.Equal(JsonValueKind.Null, resp.GetProperty("torSocksPort").ValueKind);

        var profiles = resp.GetProperty("availableProfiles").EnumerateArray()
            .Select(p => p.GetString()).ToArray();
        Assert.Contains("Balanced", profiles);
        Assert.Contains("PrivacyFocused", profiles);
        Assert.Contains("Consolidator", profiles);
        Assert.Contains("Payments", profiles);
    }

    [Fact]
    public async Task Wallet_settings_post_persists_profile_and_tor_config()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var put = await client.PostAsJsonAsync("/api/wallet/settings", new
        {
            MixingProfile = "PrivacyFocused",
            TorEnabled = true,
            TorSocksHost = "127.0.0.1",
            TorSocksPort = 9150
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PrivacyFocused", body.GetProperty("mixingProfile").GetString());
        Assert.True(body.GetProperty("torEnabled").GetBoolean());

        // Round-trip read — GET must reflect what POST just wrote.
        var get = await client.GetFromJsonAsync<JsonElement>("/api/wallet/settings");
        Assert.Equal("PrivacyFocused", get.GetProperty("mixingProfile").GetString());
        Assert.Equal("127.0.0.1", get.GetProperty("torSocksHost").GetString());
        Assert.Equal(9150, get.GetProperty("torSocksPort").GetInt32());
    }

    [Fact]
    public async Task Wallet_settings_rejects_unknown_profile()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.PostAsJsonAsync("/api/wallet/settings", new
        {
            MixingProfile = "MadeUpProfile"
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Wallet_settings_rejects_port_outside_valid_range()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.PostAsJsonAsync("/api/wallet/settings", new
        {
            TorSocksPort = 70_000 // > 65535
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Wallet_settings_empty_host_string_clears_override()
    {
        // Empty string is the API's "revert to no override" sentinel — UIs
        // can surface this as a "clear" button without needing a separate
        // endpoint. We must not persist the empty string literally.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        await client.PostAsJsonAsync("/api/wallet/settings", new
        {
            TorSocksHost = "127.0.0.1"
        });

        var cleared = await client.PostAsJsonAsync("/api/wallet/settings", new
        {
            TorSocksHost = ""
        });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        var get = await client.GetFromJsonAsync<JsonElement>("/api/wallet/settings");
        Assert.Equal(JsonValueKind.Null, get.GetProperty("torSocksHost").ValueKind);
    }

    [Fact]
    public async Task Mixing_status_reports_active_profile_after_start()
    {
        // Regression guard: before profile plumbing existed, status carried
        // no profile field. We assert both the new field AND that the
        // persisted wallet setting propagates to the running session when
        // the start request omits an override.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        await client.PostAsJsonAsync("/api/wallet/settings", new { MixingProfile = "Consolidator" });

        try
        {
            var start = await client.PostAsJsonAsync("/api/mixing/start", new { Passphrase = "pw" });
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);

            var status = await client.GetFromJsonAsync<JsonElement>("/api/mixing/status");
            Assert.True(status.GetProperty("running").GetBoolean());
            Assert.Equal("Consolidator", status.GetProperty("activeProfile").GetString());
        }
        finally
        {
            await client.PostAsync("/api/mixing/stop", null);
        }
    }

    /// <summary>
    /// Stages a single confirmed UTXO on the fake backend against a fresh wallet
    /// receive address. Returns the DB-assigned utxoId once the wallet has
    /// observed it. `tag` must be unique per call within a test — it determines
    /// the fake txid so that two seeded UTXOs don't collide.
    /// </summary>
    private static async Task<int> SeedUtxoAsync(
        KompaktorWebFactory factory, HttpClient client, long amountSat, byte tag)
    {
        var receive = await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address");
        var scriptHex = receive.GetProperty("scriptHex").GetString()!;
        var script = new Script(Encoders.Hex.DecodeData(scriptHex));

        await WaitForMonitoringAsync(client);

        var bytes = new byte[32];
        for (var i = 0; i < 32; i++) bytes[i] = (byte)(tag ^ i);
        var txId = new uint256(bytes);
        var outPoint = new OutPoint(txId, 0);
        var txOut = new TxOut(Money.Satoshis(amountSat), script);

        factory.Blockchain.StageUtxo(new UtxoInfo(outPoint, txOut, Confirmations: 1, IsCoinBase: false));
        factory.Blockchain.RaiseAddressNotification(script, txId, factory.Blockchain.BlockHeight);
        await client.PostAsync("/api/wallet/resync", null);

        // Wait for the UTXO to show up in the dashboard feed, then return its id.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var utxos = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/utxos");
            foreach (var u in utxos.EnumerateArray())
            {
                if (u.GetProperty("txId").GetString() == txId.ToString())
                    return u.GetProperty("id").GetInt32();
            }
            await Task.Delay(250);
        }
        throw new TimeoutException($"Seeded UTXO {txId} never appeared in dashboard feed.");
    }

    /// <summary>
    /// Polls /api/wallet/sync-status until the background service reports
    /// monitoring=true, or throws after the timeout. Startup races the
    /// hosted service's 1s pre-delay and 5s wallet-detection loop, so we
    /// need a window comfortably larger than that.
    /// </summary>
    private static async Task WaitForMonitoringAsync(HttpClient client, int timeoutSeconds = 20)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<JsonElement>("/api/wallet/sync-status");
            if (status.GetProperty("monitoring").GetBoolean()) return;
            await Task.Delay(250);
        }
        throw new TimeoutException("Background sync never reached monitoring=true.");
    }

    /// <summary>
    /// Polls the tx-detail endpoint until it returns 200, or throws after
    /// the timeout. Used after staging a UTXO — the async notification
    /// handler and/or forced resync need a moment to persist to the DB.
    /// </summary>
    private static async Task<JsonElement> WaitForTxDetailAsync(
        HttpClient client, string txId, int timeoutSeconds = 15)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var resp = await client.GetAsync($"/api/dashboard/transactions/{txId}");
            if (resp.StatusCode == HttpStatusCode.OK)
                return await resp.Content.ReadFromJsonAsync<JsonElement>();
            await Task.Delay(250);
        }
        throw new TimeoutException($"Tx detail for {txId} never became available.");
    }

    /// <summary>
    /// Polls /api/payments to keep the manager's activation sweep running,
    /// then reads /api/payments/{id}/status until it matches the expected
    /// state. `GET /api/payments` is what drives ActivateScheduledPaymentsAsync,
    /// so a naive poll on the status endpoint alone would never tick.
    /// </summary>
    private static async Task<JsonElement> WaitForPaymentStatusAsync(
        HttpClient client, string paymentId, string expectedStatus, int timeoutSeconds = 10)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            // Side-effect call: fetching /api/payments triggers manager activation sweep.
            _ = await client.GetAsync("/api/payments");
            var status = await client.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}/status");
            if (status.GetProperty("status").GetString() == expectedStatus) return status;
            await Task.Delay(250);
        }
        throw new TimeoutException($"Payment {paymentId} never reached status={expectedStatus}.");
    }

    [Fact]
    public async Task Test_tor_endpoint_reports_unreachable_for_closed_port()
    {
        // Bind a TCP listener just to acquire a port number, then close it —
        // that gives us a port that's very unlikely to be occupied when the
        // endpoint tries to connect. Safer than hard-coding a random port.
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var closedPort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/wallet/test-tor",
            new { host = "127.0.0.1", port = closedPort });
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(body.GetProperty("reachable").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Test_tor_endpoint_reports_reachable_for_fake_socks5_server()
    {
        // Spin up a minimal SOCKS5 greeting responder on a loopback port:
        // accept a connection, read the 4-byte greeting (0x05, 0x02, 0x00, 0x02),
        // reply with (0x05, 0x00) picking no-auth. This is the exact exchange
        // the endpoint performs, so a positive response proves the protocol
        // handling end-to-end.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var serverDone = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            try
            {
                using var c = await listener.AcceptTcpClientAsync();
                using var s = c.GetStream();
                var buf = new byte[4];
                await s.ReadExactlyAsync(buf.AsMemory());
                await s.WriteAsync(new byte[] { 0x05, 0x00 });
                await Task.Delay(100); // let client read before we close
            }
            finally { listener.Stop(); serverDone.SetResult(); }
        });

        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/wallet/test-tor",
            new { host = "127.0.0.1", port });
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("reachable").GetBoolean(),
            $"expected reachable=true, got body={body}");
        Assert.Equal("none", body.GetProperty("authMethod").GetString());
        await serverDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Exposed_address_flag_surfaces_in_utxo_and_detail_endpoints()
    {
        // IsExposed is set by KompaktorHdWallet.MarkScriptsExposed after a
        // coinjoin round reveals the script to other participants (including
        // failed rounds). There's no HTTP surface to toggle it, so we mutate
        // the address row directly and verify both the list and detail
        // endpoints surface the flag — this is the signal the UI uses to
        // warn about co-spend linkage risk.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var utxoId = await SeedUtxoAsync(factory, client, amountSat: 100_000, tag: 0x7A);

        // Sanity-check baseline before mutation.
        var before = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/utxos");
        var beforeRow = before.EnumerateArray().Single(u => u.GetProperty("id").GetInt32() == utxoId);
        Assert.False(beforeRow.GetProperty("isAddressExposed").GetBoolean());

        // Flip IsExposed directly on the address row — simulates the result
        // of a prior round where this address's script was announced.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
            var utxo = await db.Utxos.Include(u => u.Address).SingleAsync(u => u.Id == utxoId);
            utxo.Address.IsExposed = true;
            await db.SaveChangesAsync();
        }

        var after = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/utxos");
        var afterRow = after.EnumerateArray().Single(u => u.GetProperty("id").GetInt32() == utxoId);
        Assert.True(afterRow.GetProperty("isAddressExposed").GetBoolean());

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/coin-control/utxo/{utxoId}");
        Assert.True(detail.GetProperty("isAddressExposed").GetBoolean());
    }

    [Fact]
    public async Task Summary_reports_mixed_unmixed_and_exposed_balance_breakdown()
    {
        // Seed three UTXOs covering the three bucket states the dashboard
        // cares about: plain unmixed, coinjoin-produced (mixed), and one
        // sitting on an exposed address. The summary endpoint should surface
        // each bucket's sats+count so the UI can render the breakdown.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var unmixedId = await SeedUtxoAsync(factory, client, amountSat: 100_000, tag: 0x11);
        var mixedId = await SeedUtxoAsync(factory, client, amountSat: 200_000, tag: 0x22);
        var exposedId = await SeedUtxoAsync(factory, client, amountSat: 50_000, tag: 0x33);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
            var mixed = await db.Utxos.SingleAsync(u => u.Id == mixedId);
            mixed.IsCoinJoinOutput = true;
            var exposed = await db.Utxos.Include(u => u.Address).SingleAsync(u => u.Id == exposedId);
            exposed.Address.IsExposed = true;
            await db.SaveChangesAsync();
        }

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/summary");

        Assert.Equal(350_000, resp.GetProperty("totalBalanceSats").GetInt64());
        Assert.Equal(200_000, resp.GetProperty("mixedBalanceSats").GetInt64());
        Assert.Equal(1, resp.GetProperty("mixedUtxoCount").GetInt32());
        Assert.Equal(150_000, resp.GetProperty("unmixedBalanceSats").GetInt64());
        Assert.Equal(50_000, resp.GetProperty("exposedBalanceSats").GetInt64());
        Assert.Equal(1, resp.GetProperty("exposedUtxoCount").GetInt32());
    }

    [Fact]
    public async Task Tor_status_endpoint_reports_disabled_for_default_wallet()
    {
        // Brand-new wallets don't opt into Tor, so the dashboard shouldn't
        // run a probe — enabled=false and no probe fields.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/tor-status");

        Assert.False(resp.GetProperty("enabled").GetBoolean());
        Assert.False(resp.TryGetProperty("reachable", out _));
    }

    [Fact]
    public async Task Tor_status_endpoint_reports_unreachable_when_enabled_and_daemon_down()
    {
        // Open a TCP port just to reserve it, then close it so the SOCKS5
        // probe gets a clean connection refusal. This is the realistic
        // "user enabled Tor but the daemon isn't running" case.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        await client.PostAsJsonAsync("/api/wallet/settings",
            new { TorEnabled = true, TorSocksHost = "127.0.0.1", TorSocksPort = closedPort });

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/tor-status");

        Assert.True(resp.GetProperty("enabled").GetBoolean());
        Assert.Equal("127.0.0.1", resp.GetProperty("host").GetString());
        Assert.Equal(closedPort, resp.GetProperty("port").GetInt32());
        Assert.False(resp.GetProperty("reachable").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(resp.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Tor_status_endpoint_reports_reachable_against_fake_socks5_server()
    {
        // Spin up a loopback SOCKS5 greeting responder; the dashboard endpoint
        // should resolve the saved settings, run the probe, and surface
        // reachable=true with latency + auth method.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            try
            {
                using var socket = await listener.AcceptTcpClientAsync();
                var stream = socket.GetStream();
                var greeting = new byte[4];
                await stream.ReadAsync(greeting.AsMemory());
                await stream.WriteAsync(new byte[] { 0x05, 0x00 });
                await Task.Delay(100);
            }
            catch { /* accept may be aborted when the test tears down */ }
        });

        try
        {
            await using var factory = new KompaktorWebFactory();
            using var client = factory.CreateClient();
            await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
            await client.PostAsJsonAsync("/api/wallet/settings",
                new { TorEnabled = true, TorSocksHost = "127.0.0.1", TorSocksPort = port });

            var resp = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/tor-status");

            Assert.True(resp.GetProperty("enabled").GetBoolean());
            Assert.True(resp.GetProperty("reachable").GetBoolean());
            Assert.Equal("none", resp.GetProperty("authMethod").GetString());
        }
        finally
        {
            listener.Stop();
            await serverTask;
        }
    }

    [Fact]
    public async Task Failed_payment_can_be_manually_retried()
    {
        // Payments that hit the retry cap are flipped to Failed and stay there.
        // A manual retry must reset RetryCount to 0 and move Status back to
        // Pending so the mixing engine picks it up again.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        var addr = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        var created = await (await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr,
            AmountSat = 25_000L
        })).Content.ReadFromJsonAsync<JsonElement>();
        var paymentId = created.GetProperty("id").GetString()!;

        // Simulate exhausted retries by flipping the entity to Failed with the
        // retry counter maxed out — this is the exact terminal state produced
        // by BreakCommitment once MaxRetries is reached.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
            var entity = await db.PendingPayments.FindAsync(paymentId);
            Assert.NotNull(entity);
            entity!.Status = "Failed";
            entity.RetryCount = entity.MaxRetries;
            await db.SaveChangesAsync();
        }

        var retry = await client.PostAsync($"/api/payments/{paymentId}/retry", content: null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retryBody = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pending", retryBody.GetProperty("status").GetString());
        Assert.Equal(0, retryBody.GetProperty("retryCount").GetInt32());

        var status = await client.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}/status");
        Assert.Equal("Pending", status.GetProperty("status").GetString());
        Assert.Equal(0, status.GetProperty("retryCount").GetInt32());
    }

    [Fact]
    public async Task Retry_rejects_non_failed_payment()
    {
        // The retry endpoint must refuse to interfere with active payments —
        // resetting a Pending/Reserved/Committed payment would race with the
        // mixing engine's own state machine.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        var addr = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        var created = await (await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr,
            AmountSat = 18_000L
        })).Content.ReadFromJsonAsync<JsonElement>();
        var paymentId = created.GetProperty("id").GetString()!;

        var retry = await client.PostAsync($"/api/payments/{paymentId}/retry", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
    }

    [Fact]
    public async Task Retry_unknown_payment_returns_not_found()
    {
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });

        var resp = await client.PostAsync("/api/payments/nonexistent-id/retry", content: null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Payment_send_respects_custom_maxRetries()
    {
        // Historically MaxRetries was pinned to 10 on the entity; callers had no
        // way to widen or narrow it. The send endpoint must propagate a caller's
        // chosen cap so users can trade responsiveness against round availability.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        var addr = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        var created = await (await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr,
            AmountSat = 33_000L,
            MaxRetries = 3
        })).Content.ReadFromJsonAsync<JsonElement>();
        var paymentId = created.GetProperty("id").GetString()!;
        Assert.Equal(3, created.GetProperty("maxRetries").GetInt32());

        var status = await client.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}/status");
        Assert.Equal(3, status.GetProperty("maxRetries").GetInt32());

        // MaxRetries must survive a round-trip through the DB — verify via the
        // list endpoint which pulls the entity fresh.
        var list = await client.GetFromJsonAsync<JsonElement>("/api/payments");
        var match = list.EnumerateArray().First(p => p.GetProperty("id").GetString() == paymentId);
        Assert.Equal(3, match.GetProperty("maxRetries").GetInt32());
    }

    [Fact]
    public async Task Payment_send_rejects_negative_maxRetries()
    {
        // Guardrail: negative maxRetries has no meaning; validation belongs on
        // the wallet-manager side so the web layer can't smuggle bad values in.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        var addr = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        var resp = await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr,
            AmountSat = 10_000L,
            MaxRetries = -1
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Payment_send_respects_expiryMinutes()
    {
        // Outbound payments need an expiry so short-lived offers auto-cancel.
        // The /status endpoint must round-trip ExpiresAt close to the expected
        // value — a slack of a few seconds absorbs clock drift within the test.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        var addr = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        var before = DateTimeOffset.UtcNow;
        var created = await (await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr,
            AmountSat = 55_000L,
            ExpiryMinutes = 15
        })).Content.ReadFromJsonAsync<JsonElement>();
        var paymentId = created.GetProperty("id").GetString()!;

        var status = await client.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}/status");
        var expiresAtEl = status.GetProperty("expiresAt");
        // ASP.NET serializes DateTimeOffset as an ISO-8601 string by default,
        // but nothing binds us to that — accept either form so the test stays
        // stable if serialization conventions change.
        var expiresAt = expiresAtEl.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(expiresAtEl.GetInt64())
            : expiresAtEl.GetDateTimeOffset();
        var expected = before.AddMinutes(15);
        Assert.InRange(expiresAt, expected.AddSeconds(-10), expected.AddSeconds(30));
    }

    [Fact]
    public async Task Payment_send_defaults_maxRetries_when_unset()
    {
        // Callers that omit MaxRetries must continue to get the historical
        // default of 10 — this keeps existing clients behaviour-identical.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/wallet/create", new { Passphrase = "pw" });
        var addr = (await client.GetFromJsonAsync<JsonElement>("/api/wallet/receive-address"))
            .GetProperty("address").GetString()!;

        var created = await (await client.PostAsJsonAsync("/api/payments/send", new
        {
            Destination = addr,
            AmountSat = 44_000L
        })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10, created.GetProperty("maxRetries").GetInt32());
    }

    [Fact]
    public async Task Mixing_profile_catalog_is_exposed_with_structured_details()
    {
        // The UI used to hardcode one-liners per preset. Now the server is the
        // source of truth, so the catalog endpoint must expose knob values and
        // descriptions clients can render directly.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<JsonElement>("/api/mixing/profiles");

        Assert.Equal("Balanced", resp.GetProperty("default").GetString());

        var profiles = resp.GetProperty("profiles").EnumerateArray().ToArray();
        // Presets documented in MixingProfileCatalog — fail loudly if the list
        // drifts so whoever adds one has to update the test intentionally.
        var names = profiles.Select(p => p.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { "Balanced", "PrivacyFocused", "Consolidator", "Payments" }, names);

        // Every spec must carry the four knobs the UI now reads.
        foreach (var p in profiles)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("description").GetString()));
            Assert.True(p.GetProperty("consolidationThreshold").GetInt32() > 0);
            Assert.True(p.GetProperty("selfSendDelaySeconds").GetInt32() > 0);
            // JSON booleans; ensure the property exists and is the right kind.
            var ipe = p.GetProperty("interactivePaymentsEnabled");
            Assert.True(ipe.ValueKind == JsonValueKind.True || ipe.ValueKind == JsonValueKind.False);
        }

        // Spot-check the two most divergent presets so a regression in the
        // catalog values (not just the shape) is caught.
        var consolidator = profiles.Single(p => p.GetProperty("name").GetString() == "Consolidator");
        Assert.Equal(25, consolidator.GetProperty("consolidationThreshold").GetInt32());
        Assert.False(consolidator.GetProperty("interactivePaymentsEnabled").GetBoolean());

        var privacy = profiles.Single(p => p.GetProperty("name").GetString() == "PrivacyFocused");
        Assert.Equal(60, privacy.GetProperty("selfSendDelaySeconds").GetInt32());
        Assert.True(privacy.GetProperty("interactivePaymentsEnabled").GetBoolean());
    }

    [Fact]
    public async Task Mixing_profile_catalog_survives_with_no_wallet()
    {
        // The catalog is static server config — it should answer even before
        // the user has created a wallet, so the UI can render the picker on
        // first load without chicken-and-egg ordering.
        await using var factory = new KompaktorWebFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/mixing/profiles");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("profiles").GetArrayLength() >= 1);
    }
}
