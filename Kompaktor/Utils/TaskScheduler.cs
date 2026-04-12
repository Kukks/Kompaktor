using Microsoft.Extensions.Logging;
using NBitcoin;
using WabiSabi.Crypto.Randomness;

namespace Kompaktor.Utils;

public static class TaskScheduler
{
    /// <summary>
    /// Schedules tasks to run at random times before the expiry, with per-task jitter.
    /// Each task is delayed by a random amount between 0 and the time remaining until expiry,
    /// preventing timing correlation between operations from the same client.
    /// </summary>
    public static async Task Schedule(string taskName, Func<Task>[] tasks, DateTimeOffset expiry,
        WasabiRandom random, CancellationToken token, ILogger logger)
    {
        if (tasks.Length == 0)
            return;
        var fromNow = (int)Math.Max(0, (expiry - DateTimeOffset.UtcNow).TotalMilliseconds);
        var maxDelay = (int)(fromNow * 0.75);
        var delays = tasks.Select(_ => maxDelay > 0 ? random.GetInt(0, maxDelay) : 0).ToArray();
        logger.LogInformation(
            $"Scheduling {tasks.Length} {taskName} tasks with random delays before {expiry} ({fromNow}ms from now): [{string.Join(",", delays)}]ms");

        var delayedTasks = tasks.Zip(delays, (task, delay) => (task, delay))
            .OrderBy(t => t.delay)
            .Select(async t =>
            {
                if (t.delay > 0)
                    await Task.Delay(t.delay, token);
                token.ThrowIfCancellationRequested();
                await t.task().WithCancellation(token);
            })
            .ToArray();

        await Task.WhenAll(delayedTasks);
    }
}