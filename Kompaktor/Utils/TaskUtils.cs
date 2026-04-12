using Microsoft.Extensions.Logging;

namespace Kompaktor.Utils;

public static class TaskUtils
{
    /// <summary>
    /// Loops a task until the stop condition is met, with randomized inter-iteration delays
    /// to prevent timing fingerprinting by a malicious coordinator.
    /// </summary>
    public static async Task Loop(Func<Task> task, Func<bool> stop, ILogger logger, string name,
        CancellationToken cancellationToken)
    {
        while (!stop() && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await task();
                // Randomize polling interval (50-200ms) to prevent timing fingerprinting
                var jitteredDelay = 50 + Random.Shared.Next(150);
                await Task.Delay(jitteredDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogException($"Error with loop task: {name}", e);
            }
        }
    }
}