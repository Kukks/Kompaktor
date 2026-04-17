namespace Kompaktor.Blockchain;

public class ElectrumOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 50001;
    public bool UseSsl { get; set; }
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxReconnectAttempts { get; set; } = 10;
    public TimeSpan ReconnectBaseDelay { get; set; } = TimeSpan.FromSeconds(1);
}
