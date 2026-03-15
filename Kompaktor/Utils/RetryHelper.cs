using Kompaktor.Errors;
using Microsoft.Extensions.Logging;

namespace Kompaktor.Utils;

/// <summary>
/// Retry helper with exponential backoff and jitter for transient failures.
/// </summary>
public static class RetryHelper
{
    /// <summary>
    /// Executes an async operation with retry logic using exponential backoff with jitter.
    /// </summary>
    /// <typeparam name="T">Return type of the operation.</typeparam>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="maxRetries">Maximum number of retry attempts.</param>
    /// <param name="baseDelay">Base delay between retries (doubles each attempt).</param>
    /// <param name="shouldRetry">Predicate to determine if an exception is retryable. Defaults to retrying all non-protocol exceptions.</param>
    /// <param name="logger">Optional logger for retry attempts.</param>
    /// <param name="operationName">Name of the operation for logging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        int maxRetries = 3,
        TimeSpan? baseDelay = null,
        Func<Exception, bool>? shouldRetry = null,
        ILogger? logger = null,
        string? operationName = null,
        CancellationToken cancellationToken = default)
    {
        baseDelay ??= TimeSpan.FromMilliseconds(500);
        shouldRetry ??= IsTransient;

        Exception? lastException = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (attempt < maxRetries && shouldRetry(ex) && !cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
                var delay = CalculateDelay(baseDelay.Value, attempt);
                logger?.LogWarning("Retry {Attempt}/{MaxRetries} for {Operation} after {Delay}ms: {Error}",
                    attempt + 1, maxRetries, operationName ?? "operation", (int)delay.TotalMilliseconds, ex.Message);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw lastException!;
    }

    /// <summary>
    /// Executes an async void operation with retry logic.
    /// </summary>
    public static async Task ExecuteWithRetryAsync(
        Func<Task> operation,
        int maxRetries = 3,
        TimeSpan? baseDelay = null,
        Func<Exception, bool>? shouldRetry = null,
        ILogger? logger = null,
        string? operationName = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await operation();
            return true;
        }, maxRetries, baseDelay, shouldRetry, logger, operationName, cancellationToken);
    }

    private static TimeSpan CalculateDelay(TimeSpan baseDelay, int attempt)
    {
        // Exponential backoff: baseDelay * 2^attempt
        var exponentialDelay = baseDelay.TotalMilliseconds * Math.Pow(2, attempt);
        // Add jitter: ±25% randomization to prevent thundering herd
        var jitter = exponentialDelay * (Random.Shared.NextDouble() * 0.5 - 0.25);
        var totalDelay = exponentialDelay + jitter;
        // Cap at 30 seconds
        return TimeSpan.FromMilliseconds(Math.Min(totalDelay, 30000));
    }

    private static bool IsTransient(Exception ex)
    {
        // Protocol exceptions are intentional rejections — don't retry
        if (ex is KompaktorProtocolException)
            return false;

        // Task cancellation — don't retry
        if (ex is OperationCanceledException or TaskCanceledException)
            return false;

        // Network errors, timeouts, RPC failures — retry
        return ex is HttpRequestException
            or TimeoutException
            or System.IO.IOException
            or InvalidOperationException; // RPC client throws these for transient failures
    }
}
