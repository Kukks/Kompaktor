using Kompaktor.Blockchain;
using Kompaktor.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kompaktor.Tests.E2E;

internal class FakePriceService : IPriceService
{
    public Task<PriceSnapshot> GetAsync(CancellationToken ct = default)
        => Task.FromResult(new PriceSnapshot(
            new Dictionary<string, decimal> { ["usd"] = 65_000m, ["eur"] = 60_000m },
            DateTimeOffset.UtcNow));
}

/// <summary>
/// Boots the full Kompaktor.Web app in-process with:
///   - a unique per-test SQLite database (via Wallet:Path config)
///   - FakeBlockchainBackend swapped in for IBlockchainBackend
/// Each factory instance is disposable and cleans up its DB file.
/// </summary>
public class KompaktorWebFactory : WebApplicationFactory<WebEntryPoint>
{
    public string DbPath { get; } = Path.Combine(
        Path.GetTempPath(),
        $"kompaktor-e2e-{Guid.NewGuid():N}.db");

    public FakeBlockchainBackend Blockchain { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Wallet:Path"] = DbPath,
                ["Bitcoin:Network"] = "regtest",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace the blockchain backend before the app resolves it.
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IBlockchainBackend));
            if (descriptor is not null) services.Remove(descriptor);
            services.AddSingleton<IBlockchainBackend>(Blockchain);

            // Replace the price service so tests don't hit Coingecko.
            var priceDesc = services.SingleOrDefault(d => d.ServiceType == typeof(IPriceService));
            if (priceDesc is not null) services.Remove(priceDesc);
            services.AddSingleton<IPriceService, FakePriceService>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* best-effort */ }
    }
}
