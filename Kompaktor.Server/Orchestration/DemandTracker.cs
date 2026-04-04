using System.Collections.Concurrent;

namespace Kompaktor.Server.Orchestration;

/// <summary>
/// Tracks round completion metrics to inform scheduling decisions.
/// Uses a sliding window to measure recent success/failure rates and fill rates.
/// </summary>
public class DemandTracker
{
    private readonly TimeSpan _windowSize;
    private readonly ConcurrentQueue<RoundCompletionRecord> _completions = new();

    public DemandTracker(TimeSpan? windowSize = null)
    {
        _windowSize = windowSize ?? TimeSpan.FromMinutes(30);
    }

    public void RecordCompletion(string roundId, int inputCount, int maxInputCount, bool success)
    {
        _completions.Enqueue(new RoundCompletionRecord(
            DateTimeOffset.UtcNow,
            inputCount,
            maxInputCount,
            success));
        PurgeOld();
    }

    public double AverageFillRate
    {
        get
        {
            PurgeOld();
            var records = _completions.ToArray();
            if (records.Length == 0) return 0;
            return records.Average(r => r.MaxInputCount > 0
                ? (double)r.InputCount / r.MaxInputCount
                : 0);
        }
    }

    public int RecentCompletedCount
    {
        get
        {
            PurgeOld();
            return _completions.Count(r => r.Success);
        }
    }

    public int RecentFailedCount
    {
        get
        {
            PurgeOld();
            return _completions.Count(r => !r.Success);
        }
    }

    private void PurgeOld()
    {
        var cutoff = DateTimeOffset.UtcNow - _windowSize;
        while (_completions.TryPeek(out var oldest) && oldest.Timestamp < cutoff)
            _completions.TryDequeue(out _);
    }

    private record RoundCompletionRecord(
        DateTimeOffset Timestamp,
        int InputCount,
        int MaxInputCount,
        bool Success);
}
