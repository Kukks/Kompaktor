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

    /// <summary>Failed ownership proof verification.</summary>
    FailedToVerify,

    /// <summary>Attempted to double-spend a coin (registered in multiple rounds).</summary>
    DoubleSpend,

    /// <summary>Repeated registration failures.</summary>
    RepeatedFailure,

    /// <summary>Attempted to register an already banned coin.</summary>
    BannedCoinReuse
}

/// <summary>
/// Configuration for ban duration penalties.
/// </summary>
public class PrisonOptions
{
    /// <summary>Base ban duration for failing to sign.</summary>
    public TimeSpan FailedToSignDuration { get; set; } = TimeSpan.FromHours(1);

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
            BanReason.FailedToVerify => _options.FailedToVerifyDuration,
            BanReason.DoubleSpend => _options.DoubleSpendDuration,
            BanReason.RepeatedFailure => _options.RepeatedFailureDuration,
            BanReason.BannedCoinReuse => _options.FailedToVerifyDuration,
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
