using System.Collections.Concurrent;
using NBitcoin;

namespace Kompaktor.Prison;

/// <summary>
/// Types of offenses that can result in a ban.
/// </summary>
public enum BanReason
{
    /// <summary>Failed to sign during signing phase, disrupting the round.</summary>
    FailedToSign,

    /// <summary>Failed to signal ready to sign during output registration phase (lighter penalty — hardware wallets may be slow).</summary>
    FailedToSignalReady,

    /// <summary>Failed to confirm connection after input registration.</summary>
    FailedToConfirm,

    /// <summary>Failed ownership proof verification.</summary>
    FailedToVerify,

    /// <summary>Attempted to double-spend a coin (registered in multiple rounds).</summary>
    DoubleSpend,

    /// <summary>Repeated registration failures.</summary>
    RepeatedFailure,

    /// <summary>Attempted to register an already banned coin.</summary>
    BannedCoinReuse,

    /// <summary>Coordinator stability safety — too many simultaneous offenders suggest coordinator fault.</summary>
    CoordinatorStabilitySafety
}

/// <summary>
/// Configuration for ban duration penalties.
/// </summary>
public class PrisonOptions
{
    /// <summary>Base ban duration for failing to sign.</summary>
    public TimeSpan FailedToSignDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Base ban duration for failing to signal ready (lighter — hardware wallets).</summary>
    public TimeSpan FailedToSignalReadyDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Base ban duration for failing to confirm connection.</summary>
    public TimeSpan FailedToConfirmDuration { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>Base ban duration for failed ownership verification.</summary>
    public TimeSpan FailedToVerifyDuration { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Base ban duration for double-spending attempt.</summary>
    public TimeSpan DoubleSpendDuration { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Base ban duration for repeated registration failures.</summary>
    public TimeSpan RepeatedFailureDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Multiplier applied to ban duration for repeat offenders.</summary>
    public double PenaltyFactor { get; set; } = 2.0;

    /// <summary>Maximum ban duration cap.</summary>
    public TimeSpan MaxBanDuration { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>
/// Record of a ban imposed on a coin.
/// </summary>
public record BanRecord(OutPoint Outpoint, BanReason Reason, DateTimeOffset BannedAt, DateTimeOffset ExpiresAt, int OffenseCount);

/// <summary>
/// Manages banning of misbehaving coins/participants.
/// Inspired by Wasabi Wallet's Prison system.
/// </summary>
public class KompaktorPrison
{
    private readonly PrisonOptions _options;
    private readonly ConcurrentDictionary<OutPoint, BanRecord> _bannedCoins = new();

    public KompaktorPrison(PrisonOptions? options = null)
    {
        _options = options ?? new PrisonOptions();
    }

    /// <summary>
    /// Checks if a coin is currently banned.
    /// </summary>
    public bool IsBanned(OutPoint outpoint)
    {
        if (!_bannedCoins.TryGetValue(outpoint, out var record))
            return false;

        if (record.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _bannedCoins.TryRemove(outpoint, out _);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Gets the ban record for a coin, if it exists and is active.
    /// </summary>
    public BanRecord? GetBan(OutPoint outpoint)
    {
        if (!_bannedCoins.TryGetValue(outpoint, out var record))
            return null;

        if (record.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _bannedCoins.TryRemove(outpoint, out _);
            return null;
        }

        return record;
    }

    /// <summary>
    /// Bans a coin for the specified reason. Repeat offenses increase the ban duration.
    /// </summary>
    public BanRecord Ban(OutPoint outpoint, BanReason reason)
    {
        var baseDuration = reason switch
        {
            BanReason.FailedToSign => _options.FailedToSignDuration,
            BanReason.FailedToSignalReady => _options.FailedToSignalReadyDuration,
            BanReason.FailedToConfirm => _options.FailedToConfirmDuration,
            BanReason.FailedToVerify => _options.FailedToVerifyDuration,
            BanReason.DoubleSpend => _options.DoubleSpendDuration,
            BanReason.RepeatedFailure => _options.RepeatedFailureDuration,
            BanReason.BannedCoinReuse => _options.FailedToVerifyDuration,
            BanReason.CoordinatorStabilitySafety => _options.FailedToConfirmDuration,
            _ => _options.RepeatedFailureDuration
        };

        var offenseCount = 1;
        if (_bannedCoins.TryGetValue(outpoint, out var existing))
        {
            offenseCount = existing.OffenseCount + 1;
        }

        var multiplier = Math.Pow(_options.PenaltyFactor, offenseCount - 1);
        var duration = TimeSpan.FromTicks((long)(baseDuration.Ticks * multiplier));
        if (duration > _options.MaxBanDuration)
            duration = _options.MaxBanDuration;

        var now = DateTimeOffset.UtcNow;
        var record = new BanRecord(outpoint, reason, now, now + duration, offenseCount);
        _bannedCoins[outpoint] = record;
        return record;
    }

    /// <summary>
    /// Bans all coins associated with inputs that failed to sign in a round.
    /// </summary>
    public void BanDisruptors(IEnumerable<OutPoint> disruptors)
    {
        foreach (var outpoint in disruptors)
        {
            Ban(outpoint, BanReason.FailedToSign);
        }
    }

    /// <summary>
    /// Gets all currently active bans.
    /// </summary>
    public IReadOnlyCollection<BanRecord> ActiveBans =>
        _bannedCoins.Values.Where(r => r.ExpiresAt > DateTimeOffset.UtcNow).ToList();

    /// <summary>
    /// Inherits punishment from an ancestor outpoint to a descendant.
    /// The descendant receives half the remaining ban duration of the ancestor.
    /// Used to prevent ban evasion by spending a banned UTXO.
    /// </summary>
    public BanRecord? InheritPunishment(OutPoint ancestor, OutPoint descendant)
    {
        var ancestorBan = GetBan(ancestor);
        if (ancestorBan is null) return null;

        var remaining = ancestorBan.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return null;

        var inheritedDuration = TimeSpan.FromTicks(remaining.Ticks / 2);
        if (inheritedDuration < TimeSpan.FromMinutes(1)) return null; // Not worth inheriting

        var now = DateTimeOffset.UtcNow;
        var record = new BanRecord(descendant, BanReason.BannedCoinReuse, now, now + inheritedDuration, 1);
        _bannedCoins[descendant] = record;
        return record;
    }

    /// <summary>
    /// Removes expired bans. Call periodically to prevent memory growth.
    /// </summary>
    public int PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = _bannedCoins.Where(kvp => kvp.Value.ExpiresAt <= now).Select(kvp => kvp.Key).ToList();
        foreach (var key in expired)
            _bannedCoins.TryRemove(key, out _);
        return expired.Count;
    }
}
