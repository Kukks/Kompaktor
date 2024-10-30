namespace Kompaktor.Utils;

internal static class AsyncEventHandlerExtensions
{
    /// <summary>
    ///     Invokes asynchronous event handlers, returning an awaitable task. Each handler is fully executed
    ///     before the next handler in the list is invoked.
    /// </summary>
    /// <typeparam name="TEventArgs">The type of argument passed to each handler.</typeparam>
    /// <param name="handlers">The event handlers. May be <c>null</c>.</param>
    /// <param name="sender">The event source.</param>
    /// <param name="args">The event argument.</param>
    /// <returns>An awaitable task that completes when all handlers have completed.</returns>
    /// <exception cref="T:System.AggregateException">
    ///     Thrown if any handlers fail. It contains all
    ///     collected exceptions.
    /// </exception>
    public static async Task InvokeIfNotNullAsync<TEventArgs>(
        this AsyncEventHandler<TEventArgs>? handlers,
        object sender,
        TEventArgs args)
    {
        if (handlers == null)
            return;

        List<Exception>? exceptions = null;
        var listenerDelegates = handlers.GetInvocationList();
        for (var index = 0; index < listenerDelegates.Length; ++index)
        {
            var listenerDelegate = (AsyncEventHandler<TEventArgs>) listenerDelegates[index];
            try
            {
                await listenerDelegate(sender, args).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (exceptions == null)
                    exceptions = new List<Exception>(2);
                exceptions.Add(ex);
            }
        }

        // Throw collected exceptions, if any
        if (exceptions != null)
            throw new AggregateException(exceptions);
    }

    /// <summary>
    ///     Invokes asynchronous event handlers, returning an awaitable task. Each handler is fully executed
    ///     before the next handler in the list is invoked.
    /// </summary>
    /// <param name="handlers">The event handlers. May be <c>null</c>.</param>
    /// <param name="sender">The event source.</param>
    /// <returns>An awaitable task that completes when all handlers have completed.</returns>
    /// <exception cref="T:System.AggregateException">
    ///     Thrown if any handlers fail. It contains all
    ///     collected exceptions.
    /// </exception>
    public static async Task InvokeIfNotNullAsync(
        this AsyncEventHandler? handlers,
        object sender)
    {
        if (handlers == null)
            return;

        List<Exception>? exceptions = null;
        var listenerDelegates = handlers.GetInvocationList();
        foreach (var t in listenerDelegates)
        {
            var listenerDelegate = (AsyncEventHandler) t;
            try
            {
                await listenerDelegate(sender).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                exceptions ??= new List<Exception>(2);
                exceptions.Add(ex);
            }
        }

        // Throw collected exceptions, if any
        if (exceptions != null)
            throw new AggregateException(exceptions);
    }


    public static Task OnFailureCancel(this Task task, CancellationTokenSource cts )
    {
        if (task.IsFaulted && task.Exception is { } exception)
        {
            // If one task is failing, cancel all the tasks and throw.
            cts.Cancel();
            throw exception;
        }
        return task;
    }  
    public static Task<T> OnFailureCancel<T>(this Task<T> task, CancellationTokenSource cts )
    {
        if (task.IsFaulted && task.Exception is { } exception)
        {
            // If one task is failing, cancel all the tasks and throw.
            cts.Cancel();
            throw exception;
        }
        return task;
    }
    
    
}