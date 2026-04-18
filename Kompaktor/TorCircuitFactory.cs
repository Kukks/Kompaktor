using System.Net;
using Kompaktor.Contracts;

namespace Kompaktor;

/// <summary>
/// Configuration for connecting to a Tor SOCKS5 proxy.
/// </summary>
public class TorOptions
{
    /// <summary>Tor SOCKS5 proxy host.</summary>
    public string SocksHost { get; set; } = "127.0.0.1";

    /// <summary>Tor SOCKS5 proxy port (default 9050 for Tor daemon, 9150 for Tor Browser).</summary>
    public int SocksPort { get; set; } = 9050;
}

/// <summary>
/// Circuit factory that routes each identity through a separate Tor stream.
/// Tor uses SOCKS5 username/password authentication as the stream isolation key —
/// traffic with different credentials gets routed through different Tor circuits,
/// preventing the exit node or destination from correlating connections.
/// </summary>
public class TorCircuitFactory : ICircuitFactory
{
    private readonly TorOptions _options;

    public TorCircuitFactory(TorOptions? options = null)
    {
        _options = options ?? new TorOptions();
    }

    public ICircuit Create(string identity) => new TorCircuit(identity, _options);
}

/// <summary>
/// A Tor-backed circuit providing network identity isolation via SOCKS5 stream isolation.
/// Each circuit uses a unique SOCKS5 credential pair derived from the identity string,
/// causing Tor to route the traffic through a distinct circuit path.
/// </summary>
internal class TorCircuit : ICircuit
{
    private readonly TorOptions _options;

    public TorCircuit(string identity, TorOptions options)
    {
        Id = identity;
        _options = options;
    }

    public string Id { get; }

    public HttpMessageHandler CreateHandler()
    {
        var proxy = new WebProxy(new Uri($"socks5://{_options.SocksHost}:{_options.SocksPort}"));
        // Tor uses SOCKS5 auth credentials as stream isolation keys.
        // Different username:password pairs → different Tor circuits.
        proxy.Credentials = new NetworkCredential(Id, Id);

        return new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = true,
            // Disable connection pooling across different handlers to prevent circuit reuse.
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        };
    }

    public event Action? IsolationIdChanged;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
