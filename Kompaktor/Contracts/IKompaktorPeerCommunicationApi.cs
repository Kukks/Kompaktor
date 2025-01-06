using System.Runtime.CompilerServices;

namespace Kompaktor.Contracts;

public interface  IKompaktorPeerCommunicationApi:IDisposable
{
    Task SendMessageAsync(byte[] message, string reasonToWaitFOrLog);
    IAsyncEnumerable<byte[]> Messages(bool fromStart, CancellationToken cancellationToken);

    Task<byte[]> WaitForMessage(byte[] prefix, CancellationToken cancellationToken, string reasonToWaitFOrLog);

}