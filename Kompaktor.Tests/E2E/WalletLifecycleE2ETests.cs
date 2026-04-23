using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kompaktor.Blockchain;
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
}
