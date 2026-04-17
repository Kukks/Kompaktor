using NBitcoin;

namespace Kompaktor.Contracts;

public interface IRoundHistoryTracker
{
    int ConsecutiveFailures { get; }
    void RecordFailedRound(IEnumerable<OutPoint> registeredInputs);
    void RecordSuccess();
    bool ShouldBackOff();
    HashSet<OutPoint> GetCoinsToExclude(IEnumerable<Coin> proposedCoins);
}
