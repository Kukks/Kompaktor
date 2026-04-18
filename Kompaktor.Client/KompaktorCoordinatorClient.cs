using Kompaktor.Contracts;
using Kompaktor.JsonConverters;
using Kompaktor.Models;

namespace Kompaktor.Client;

/// <summary>
/// High-level client for interacting with a Kompaktor coordinator server.
/// Handles round discovery, parameter fetching, and API factory creation.
/// This is the entry point for wallet integrations — consumers call
/// <see cref="GetActiveRoundsAsync"/> to discover rounds, then create
/// per-round API instances via <see cref="CreateRoundApiFactory"/>.
/// </summary>
public class KompaktorCoordinatorClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ICircuitFactory _circuitFactory;

    public KompaktorCoordinatorClient(Uri coordinatorUri, ICircuitFactory? circuitFactory = null)
    {
        _circuitFactory = circuitFactory ?? new DefaultCircuitFactory();
        // Discovery uses a single shared circuit — round listing is public info
        var circuit = _circuitFactory.Create("discovery");
        _httpClient = new HttpClient(circuit.CreateHandler()) { BaseAddress = coordinatorUri };
    }

    /// <summary>
    /// Lists all active round IDs on the coordinator.
    /// </summary>
    public async Task<string[]> GetActiveRoundsAsync(CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync("/api/rounds", ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return KompaktorJsonHelper.DeserializeFromBytes<string[]>(bytes) ?? [];
    }

    /// <summary>
    /// Gets full round parameters and configuration for a specific round.
    /// Use this to decide which round to join based on fee rate, input range, etc.
    /// </summary>
    public async Task<RoundInfoResponse> GetRoundInfoAsync(string roundId, CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync($"/api/round/{roundId}/info", ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return KompaktorJsonHelper.DeserializeFromBytes<RoundInfoResponse>(bytes)
            ?? throw new InvalidOperationException("Null response from coordinator");
    }

    /// <summary>
    /// Gets current round status including input/output/signature counts.
    /// </summary>
    public async Task<RoundStatusResponse> GetRoundStatusAsync(string roundId, CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync($"/api/round/{roundId}/status", ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return KompaktorJsonHelper.DeserializeFromBytes<RoundStatusResponse>(bytes)
            ?? throw new InvalidOperationException("Null response from coordinator");
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
