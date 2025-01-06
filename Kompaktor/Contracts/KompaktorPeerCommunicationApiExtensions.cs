namespace Kompaktor.Contracts;

public static class KompaktorPeerCommunicationApiExtensions
{
    public static async Task<byte[]> SendAndWaitForMessageAsync(this IKompaktorPeerCommunicationApi api, byte[] message, byte[] prefix, CancellationToken cancellationToken, string reasonToWaitFOrLog)
    {
        var waitMsgTask = api.WaitForMessage(prefix, cancellationToken, reasonToWaitFOrLog);
        await api.SendMessageAsync(message, reasonToWaitFOrLog);
        return await waitMsgTask;
    }

}