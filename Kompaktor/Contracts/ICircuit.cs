namespace Kompaktor.Contracts;

/// <summary>
/// Represents a network circuit for identity isolation.
/// Each circuit provides a separate network path (e.g., a different Tor circuit)
/// to prevent linking multiple operations from the same participant.
/// </summary>
public interface ICircuit : IAsyncDisposable
{
    /// <summary>Unique identifier for this circuit.</summary>
    string Id { get; }

    /// <summary>
    /// Creates an HttpMessageHandler that routes traffic through this circuit.
    /// For Tor: returns a Socks5HttpClientHandler pointing at the Tor SOCKS5 proxy with a per-circuit isolation ID.
    /// For development: returns a standard HttpClientHandler.
    /// </summary>
    HttpMessageHandler CreateHandler();

    /// <summary>
    /// Fires when the circuit's isolation identity changes (e.g., Tor circuit rotation).
    /// Consumers should dispose and recreate their HTTP clients when this fires.
    /// </summary>
    event Action? IsolationIdChanged;
}

/// <summary>
/// Factory for creating circuits with per-identity isolation.
/// </summary>
public interface ICircuitFactory
{
    /// <summary>
    /// Creates a new circuit for the given identity string.
    /// Different identity strings should yield circuits routed through different network paths.
    /// </summary>
    /// <param name="identity">
    /// An opaque string identifying the participant role.
    /// For coinjoins: each input registration, output registration, and signing operation
    /// for a given participant should use different identities to prevent network-level linking.
    /// </param>
    ICircuit Create(string identity);
}

/// <summary>
/// Default circuit factory that returns plain HttpClientHandler instances (no Tor).
/// Suitable for development, testing, and environments where network identity isolation is not required.
/// </summary>
public class DefaultCircuitFactory : ICircuitFactory
{
    public ICircuit Create(string identity) => new DefaultCircuit(identity);

    private class DefaultCircuit : ICircuit
    {
        public DefaultCircuit(string identity) => Id = identity;
        public string Id { get; }
        public HttpMessageHandler CreateHandler() => new HttpClientHandler();
        public event Action? IsolationIdChanged;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
