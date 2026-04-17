using System.Runtime.CompilerServices;

namespace Kompaktor.Contracts;

public interface  IKompaktorPeerCommunicationApi:IDisposable
{
    Task SendMessageAsync(byte[] message, string reasonToWaitForLog);
    IAsyncEnumerable<byte[]> Messages(bool fromStart, CancellationToken cancellationToken);

    Task<byte[]> WaitForMessage(byte[] prefix, CancellationToken cancellationToken, string reasonToWaitForLog);

}