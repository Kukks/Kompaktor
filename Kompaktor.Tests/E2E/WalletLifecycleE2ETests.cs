using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
}
