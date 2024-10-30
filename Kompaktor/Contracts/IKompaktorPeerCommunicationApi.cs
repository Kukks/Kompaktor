using System.Runtime.CompilerServices;

namespace Kompaktor.Contracts;

public interface  IKompaktorPeerCommunicationApi:IDisposable
{
    Task SendMessageAsync(byte[] message);
    IAsyncEnumerable<byte[]> Messages([EnumeratorCancellation] CancellationToken cancellationToken  );

}