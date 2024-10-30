namespace Kompaktor.Utils;

/// <summary>
///     An asynchronous event handler.
///     To trigger the event, use the function
///     <see cref="AsyncEventHandlerExtensions.InvokeAsync{TEventArgs}(AsyncEventHandler{TEventArgs}, object, TEventArgs)" />
///     instead of <see cref="EventHandler.Invoke(object, EventArgs)" />, it will guarantee a serialization
///     of calls to the event delegates and collects any exceptions.
///     <example>
///         <code>
///     public event AsyncEventHandler<EventArgs>
///                 MyEvent;
///                 // Trigger the event
///                 public async Task OnMyEvent()
///                 {
///                 // A null check is not necessary, the ? operator would fail here
///                 await MyEvent.InvokeIfNotNullAsync(this, new EventArgs());
///                 }
/// </code>
///     </example>
/// </summary>
/// <typeparam name="TEventArgs">The type of event arguments.</typeparam>
/// <param name="sender">The sender of the event.</param>
/// <param name="args">Event arguments.</param>
/// <returns>An awaitable task.</returns>
public delegate Task AsyncEventHandler(object sender);

