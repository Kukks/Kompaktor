using Kompaktor.Contracts;
using Kompaktor.JsonConverters;
using Kompaktor.Models;
using Kompaktor.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kompaktor.Client;

/// <summary>
/// High-level client for interacting with a Kompaktor coordinator server.
/// Handles round discovery, parameter fetching, and API factory creation.
/// This is the entry point for wallet integrations — consumers call
/// <see cref="GetActiveRoundsAsync"/> to discover rounds, then create
/// per-round API instances via <see cref="CreateRoundApiFactory"/>.
///
/// Discovery calls include automatic retry with exponential backoff for
/// transient network failures (especially relevant over Tor).
/// </summary>
public class KompaktorCoordinatorClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ICircuitFactory _circuitFactory;
    private readonly ILogger _logger;

    public KompaktorCoordinatorClient(
        Uri coordinatorUri,
        ICircuitFactory? circuitFactory = null,
        ILogger? logger = null)
    {
        _circuitFactory = circuitFactory ?? new DefaultCircuitFactory();
        _logger = logger ?? NullLogger.Instance;
        // Discovery uses a single shared circuit — round listing is public info
        var circuit = _circuitFactory.Create("discovery");
        _httpClient = new HttpClient(circuit.CreateHandler())
        {
            BaseAddress = coordinatorUri,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// Lists all active round IDs on the coordinator.
    /// Retries up to 3 times on transient failures.
    /// </summary>
    public async Task<string[]> GetActiveRoundsAsync(CancellationToken ct = default)
    {
        return await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            using var response = await _httpClient.GetAsync("/api/rounds", ct);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return KompaktorJsonHelper.DeserializeFromBytes<string[]>(bytes) ?? [];
        }, maxRetries: 3, baseDelay: TimeSpan.FromSeconds(1),
            logger: _logger, operationName: "GetActiveRounds", cancellationToken: ct);
    }

    /// <summary>
    /// Gets full round parameters and configuration for a specific round.
    /// Use this to decide which round to join based on fee rate, input range, etc.
    /// Retries up to 3 times on transient failures.
    /// </summary>
    public async Task<RoundInfoResponse> GetRoundInfoAsync(string roundId, CancellationToken ct = default)
    {
        return await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            using var response = await _httpClient.GetAsync($"/api/round/{roundId}/info", ct);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return KompaktorJsonHelper.DeserializeFromBytes<RoundInfoResponse>(bytes)
                ?? throw new InvalidOperationException("Null response from coordinator");
        }, maxRetries: 3, baseDelay: TimeSpan.FromSeconds(1),
            logger: _logger, operationName: $"GetRoundInfo({roundId})", cancellationToken: ct);
    }

    /// <summary>
    /// Gets current round status including input/output/signature counts.
    /// Retries up to 3 times on transient failures.
    /// </summary>
    public async Task<RoundStatusResponse> GetRoundStatusAsync(string roundId, CancellationToken ct = default)
    {
        return await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            using var response = await _httpClient.GetAsync($"/api/round/{roundId}/status", ct);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return KompaktorJsonHelper.DeserializeFromBytes<RoundStatusResponse>(bytes)
                ?? throw new InvalidOperationException("Null response from coordinator");
        }, maxRetries: 3, baseDelay: TimeSpan.FromSeconds(1),
            logger: _logger, operationName: $"GetRoundStatus({roundId})", cancellationToken: ct);
    }

    /// <summary>
    /// Checks coordinator health. Returns true if the coordinator is reachable.
    /// </summary>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a round API factory for participating in a specific round.
    /// Each call to <see cref="IKompaktorRoundApiFactory.Create"/> returns
    /// an API instance with its own network circuit for identity isolation.
    /// </summary>
    public IKompaktorRoundApiFactory CreateRoundApiFactory(string roundId)
    {
        return new HttpKompaktorRoundApiFactory(_httpClient.BaseAddress!, roundId, _circuitFactory);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// Response from the round status endpoint.
/// </summary>
public record RoundStatusResponse
{
    public string RoundId { get; init; } = "";
    public string Status { get; init; } = "";
    public int InputCount { get; init; }
    public int OutputCount { get; init; }
    public int SignatureCount { get; init; }
    public bool IsBlameRound { get; init; }
    public string? BlameOf { get; init; }
}
