using NBitcoin;

namespace Kompaktor.Server;

/// <summary>
/// Provides amount tiers for round creation, ensuring rounds are segmented
/// by suggested maximum input amount. Smaller-tier rounds run more frequently,
/// whale-tier rounds less often.
///
/// Tiers are powers of 10 from the base (e.g., 0.1, 1.0, 10.0 BTC).
/// Round counter dividers are powers of 2: every round gets the base tier,
/// every 2nd round gets 10x, every 4th gets 100x, etc.
/// </summary>
public class MaxSuggestedAmountProvider
{
    private readonly Money _baseTier;
    private readonly Money _maxRegistrable;
    private readonly Money[] _tiers;
    private int _roundCounter;

    public MaxSuggestedAmountProvider(Money baseTier, Money maxRegistrable)
    {
        _baseTier = baseTier;
        _maxRegistrable = maxRegistrable;

        // Build tiers: base * 10^0, base * 10^1, base * 10^2, ...
        var tiers = new List<Money>();
        var current = baseTier;
        while (current <= maxRegistrable)
        {
            tiers.Add(current);
            current = Money.Satoshis(current.Satoshi * 10);
        }
        if (tiers.Count == 0) tiers.Add(maxRegistrable);
        _tiers = tiers.ToArray();
    }

    /// <summary>
    /// Gets the MaxSuggestedAmount for the next round.
    /// The first round is always a whale round. Subsequent rounds alternate
    /// through tiers based on round counter.
    /// </summary>
    public Money GetNextMaxSuggestedAmount()
    {
        var counter = Interlocked.Increment(ref _roundCounter);

        // First round: whale tier
        if (counter == 1) return _tiers[^1];

        // Find the highest tier where counter is divisible by 2^tierIndex
        for (int i = _tiers.Length - 1; i >= 0; i--)
        {
            var divider = 1 << i; // 2^i
            if (counter % divider == 0)
                return _tiers[i];
        }

        return _tiers[0]; // Base tier as fallback
    }

    /// <summary>Gets all configured tiers.</summary>
    public IReadOnlyList<Money> Tiers => _tiers;
}
