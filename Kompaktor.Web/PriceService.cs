using System.Text.Json;

namespace Kompaktor.Web;

/// <summary>
/// Fetches the current BTC price in fiat currencies.
/// Implementations are expected to be safe to call concurrently.
/// </summary>
public interface IPriceService
{
    /// <summary>Returns a map of ISO currency code (lowercase) to BTC price.</summary>
    Task<PriceSnapshot> GetAsync(CancellationToken ct = default);
}

public record PriceSnapshot(IReadOnlyDictionary<string, decimal> Rates, DateTimeOffset FetchedAt)
{
    public static PriceSnapshot Empty => new(new Dictionary<string, decimal>(), DateTimeOffset.MinValue);
}

/// <summary>
/// Coingecko-backed price service with in-memory caching.
/// Default cache window is 60 seconds to avoid rate limiting on the public endpoint.
/// </summary>
public class CoingeckoPriceService : IPriceService, IDisposable
{
    private static readonly string[] DefaultCurrencies = ["usd", "eur"];

    private readonly HttpClient _http;
    private readonly ILogger<CoingeckoPriceService> _logger;
    private readonly TimeSpan _cacheTtl;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private PriceSnapshot _cached = PriceSnapshot.Empty;

    public CoingeckoPriceService(ILogger<CoingeckoPriceService> logger, TimeSpan? cacheTtl = null)
    {
        _logger = logger;
        _cacheTtl = cacheTtl ?? TimeSpan.FromSeconds(60);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<PriceSnapshot> GetAsync(CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow - _cached.FetchedAt < _cacheTtl && _cached.Rates.Count > 0)
            return _cached;

        await _fetchGate.WaitAsync(ct);
        try
        {
            // Double-check after acquiring gate: another caller may have refreshed.
            if (DateTimeOffset.UtcNow - _cached.FetchedAt < _cacheTtl && _cached.Rates.Count > 0)
                return _cached;

            var url = $"https://api.coingecko.com/api/v3/simple/price?ids=bitcoin&vs_currencies={string.Join(",", DefaultCurrencies)}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Coingecko returned {Status} — serving stale price", resp.StatusCode);
                return _cached;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var bitcoin = doc.RootElement.GetProperty("bitcoin");

            var rates = new Dictionary<string, decimal>();
            foreach (var currency in DefaultCurrencies)
            {
                if (bitcoin.TryGetProperty(currency, out var rate))
                    rates[currency] = rate.GetDecimal();
            }

            _cached = new PriceSnapshot(rates, DateTimeOffset.UtcNow);
            return _cached;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Price fetch failed — serving stale price");
            return _cached;
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    public void Dispose() => _http.Dispose();
}
