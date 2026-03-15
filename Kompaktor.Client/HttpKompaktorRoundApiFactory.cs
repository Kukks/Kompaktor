using Kompaktor.Contracts;

namespace Kompaktor.Client;

/// <summary>
/// Factory that creates HttpKompaktorRoundApi instances, each with its own HttpClient
/// routed through a separate circuit for network identity isolation.
/// </summary>
public class HttpKompaktorRoundApiFactory : IKompaktorRoundApiFactory
{
    private readonly Uri _baseUri;
    private readonly string _roundId;
    private readonly ICircuitFactory _circuitFactory;
    private int _identityCounter;

    public HttpKompaktorRoundApiFactory(Uri baseUri, string roundId, ICircuitFactory? circuitFactory = null)
    {
        _baseUri = baseUri;
        _roundId = roundId;
        _circuitFactory = circuitFactory ?? new DefaultCircuitFactory();
    }

    public IKompaktorRoundApi Create()
    {
        var identity = $"kompaktor-{_roundId}-{Interlocked.Increment(ref _identityCounter)}";
        var circuit = _circuitFactory.Create(identity);
        var handler = circuit.CreateHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = _baseUri };
        return new HttpKompaktorRoundApi(httpClient, _roundId);
    }
}
