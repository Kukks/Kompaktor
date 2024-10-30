using Microsoft.Extensions.Logging;

namespace Kompaktor.Utils;

public static class LoggerHelper
{
    public static void LogException(this ILogger logger, string message, Exception exception)
    {
        logger.LogError(Random.Shared.Next(0, 65535),exception, message);
    }
}