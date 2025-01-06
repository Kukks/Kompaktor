using Microsoft.Extensions.Logging;
using NBitcoin;
using WabiSabi.Crypto.Randomness;

namespace Kompaktor.Utils;

public static  class TaskScheduler
{
    //given a set of tasks, schedule them to run at a random time before the expiry
    public static Task Schedule(string taskName,Func<Task>[] tasks, DateTimeOffset expiry, WasabiRandom random, CancellationToken token, ILogger logger)
    {
        if(tasks.Length == 0)
            return Task.CompletedTask;
        var fromNow = 0;// Math.Max(0, (expiry - DateTimeOffset.UtcNow).TotalMilliseconds);
        var delays = tasks.Select(task => random.GetInt(0, (int) fromNow+1)).ToArray();
        logger.LogInformation($"Scheduling {tasks.Length} {taskName} tasks to run at random times before {expiry}({fromNow} from now) (in {string.Join(",", delays)} ms)");
        var orderedTasks = tasks.Zip(delays, (task, delay) => (task, delay)).OrderBy(tuple => tuple.delay).Select(tuple => tuple.task.Invoke().WithCancellation(token)).ToArray();
        return Task.WhenAll(orderedTasks);
        
    }
}