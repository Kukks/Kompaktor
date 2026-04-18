using Kompaktor.Models;

namespace Kompaktor.Client;

/// <summary>
/// A KompaktorRound backed by HTTP event polling from a remote coordinator.
/// Periodically fetches events via the /events endpoint and feeds them into
/// the local round state, triggering NewEvent for the KompaktorRoundClient.
///
/// Usage:
/// <code>
/// var api = new HttpKompaktorRoundApi(httpClient, roundId);
/// var round = new RemoteKompaktorRound(api, pollInterval: TimeSpan.FromSeconds(1));
/// await round.StartPollingAsync(cts.Token);
/// // round is now kept in sync — pass it to KompaktorRoundClient
/// </code>
/// </summary>
public class RemoteKompaktorRound : KompaktorRound
{
    private readonly HttpKompaktorRoundApi _api;
    private readonly TimeSpan _pollInterval;
    private string? _lastEventId;
    private Task? _pollingTask;

    public RemoteKompaktorRound(HttpKompaktorRoundApi api, TimeSpan? pollInterval = null)
    {
        _api = api;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Fetches the initial round events and begins polling for updates.
    /// This must be called before passing the round to KompaktorRoundClient.
    /// </summary>
    public async Task StartPollingAsync(CancellationToken ct = default)
    {
        // Initial fetch — gets all events including the Created event
        await FetchEventsAsync(ct);

        // Start background polling loop
        _pollingTask = PollLoopAsync(ct);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, ct);
                await FetchEventsAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Transient failure — continue polling
            }
        }
    }

    private async Task FetchEventsAsync(CancellationToken ct)
    {
        var events = await _api.GetEventsSinceAsync(_lastEventId);
        foreach (var evt in events)
        {
            ct.ThrowIfCancellationRequested();
            await AddEvent(evt);
            _lastEventId = evt.Id;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
    }
}
