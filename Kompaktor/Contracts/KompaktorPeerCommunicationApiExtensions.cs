namespace Kompaktor.Contracts;

public static class KompaktorPeerCommunicationApiExtensions
{
    public static async Task<byte[]> SendAndWaitForMessageAsync(this IKompaktorPeerCommunicationApi api, byte[] message, byte[] prefix, CancellationToken cancellationToken, string reasonToWaitForLog)
    {
        var waitMsgTask = api.WaitForMessage(prefix, cancellationToken, reasonToWaitForLog);
        await api.SendMessageAsync(message, reasonToWaitForLog);
        return await waitMsgTask;
    }

}