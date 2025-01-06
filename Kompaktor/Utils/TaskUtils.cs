using Microsoft.Extensions.Logging;

namespace Kompaktor.Utils;

public static class TaskUtils
{
    public static  async Task Loop(Func<Task> task, Func<bool> stop, ILogger logger, string name, CancellationToken cancellationToken)
    {
        while (!stop() && !cancellationToken.IsCancellationRequested)
        {
            try
            {

                await task();
                await Task.Delay(100, cancellationToken);
            }
            catch (Exception e) when (e is not TaskCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogException($"Error with loop task: {name}", e);
            }
        }
    }
    
}