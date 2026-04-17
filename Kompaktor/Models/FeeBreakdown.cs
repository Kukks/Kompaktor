using NBitcoin;

namespace Kompaktor.Models;

/// <summary>
/// Breakdown of fees in a coinjoin transaction for transparency/audit.
/// Clients verify that surplus is zero (or negligible rounding) before signing,
/// ensuring no hidden coordinator fee extraction.
/// </summary>
public record FeeBreakdown(
    Money TotalInputs,
    Money TotalOutputs,
    Money ActualFee,
    Money ExpectedMiningFee,
    Money Surplus)
{
    /// <summary>
    /// Returns true if the surplus exceeds the acceptable threshold.
    /// A small surplus (under the dust limit) can occur due to rounding
    /// in fee estimation vs actual serialized sizes.
    /// </summary>
    public bool HasExcessiveSurplus(Money? threshold = null)
    {
        threshold ??= Money.Satoshis(546); // dust limit
        return Surplus > threshold;
    }
}
