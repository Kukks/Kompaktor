using Kompaktor.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kompaktor.Client;

/// <summary>
/// A KompaktorRound backed by HTTP event polling from a remote coordinator.
/// Periodically fetches events via the /events endpoint and feeds them into
/// the local round state, triggering NewEvent for the KompaktorRoundClient.
///
/// Includes exponential backoff on consecutive poll failures (capped at 30s)
/// to avoid hammering a temporarily-unavailable coordinator.
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
    private readonly ILogger _logger;
    private string? _lastEventId;
    private Task? _pollingTask;
    private int _consecutiveFailures;

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    public RemoteKompaktorRound(
        HttpKompaktorRoundApi api,
        TimeSpan? pollInterval = null,
        ILogger? logger = null)
    {
        _api = api;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Number of consecutive poll failures. Resets on success.
    /// </summary>
    public int ConsecutiveFailures => _consecutiveFailures;

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
                var delay = _consecutiveFailures == 0
                    ? _pollInterval
                    : CalculateBackoff(_consecutiveFailures);

                await Task.Delay(delay, ct);
                await FetchEventsAsync(ct);

                if (_consecutiveFailures > 0)
                {
                    _logger.LogInformation(
                        "Poll recovered after {Failures} consecutive failures", _consecutiveFailures);
                }
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                var backoff = CalculateBackoff(_consecutiveFailures);
                _logger.LogWarning(
                    "Poll failure #{Failures} ({Error}), backing off {Backoff}ms",
                    _consecutiveFailures, ex.Message, (int)backoff.TotalMilliseconds);
            }
        }
    }

    private TimeSpan CalculateBackoff(int failures)
    {
        // Exponential backoff: pollInterval * 2^(failures-1), capped at MaxBackoff
        var backoffMs = _pollInterval.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 10));
        return TimeSpan.FromMilliseconds(Math.Min(backoffMs, MaxBackoff.TotalMilliseconds));
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
