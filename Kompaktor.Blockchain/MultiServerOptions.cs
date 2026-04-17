namespace Kompaktor.Blockchain;

public class MultiServerOptions
{
    public List<ElectrumServerConfig> Servers { get; set; } = [];
    public RoutingStrategy Strategy { get; set; } = RoutingStrategy.RoundRobin;
}

public class ElectrumServerConfig
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 50001;
    public bool UseSsl { get; set; }
}

public enum RoutingStrategy
{
    RoundRobin,
    Random,
    Manual
}
