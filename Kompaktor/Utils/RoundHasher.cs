using System.Security.Cryptography;
using System.Text;
using Kompaktor.Credentials;
using Kompaktor.Models;
using NBitcoin;

namespace Kompaktor.Utils;

/// <summary>
/// Computes deterministic round IDs from round parameters.
/// Clients can independently verify that a round's ID matches its parameters.
/// </summary>
public static class RoundHasher
{
    public static string CalculateHash(
        DateTimeOffset startTime,
        FeeRate feeRate,
        TimeSpan inputTimeout,
        TimeSpan outputTimeout,
        TimeSpan signingTimeout,
        IntRange inputCount,
        MoneyRange inputAmount,
        IntRange outputCount,
        MoneyRange outputAmount,
        Dictionary<CredentialType, CredentialConfiguration> credentials,
        string? blameOf = null)
    {
        using var sha = SHA256.Create();
        var sb = new StringBuilder();

        sb.Append(startTime.ToUnixTimeSeconds());
        sb.Append('|').Append(feeRate.FeePerK.Satoshi);
        sb.Append('|').Append(inputTimeout.Ticks);
        sb.Append('|').Append(outputTimeout.Ticks);
        sb.Append('|').Append(signingTimeout.Ticks);
        sb.Append('|').Append(inputCount.Min).Append('|').Append(inputCount.Max);
        sb.Append('|').Append(inputAmount.Min.Satoshi).Append('|').Append(inputAmount.Max.Satoshi);
        sb.Append('|').Append(outputCount.Min).Append('|').Append(outputCount.Max);
        sb.Append('|').Append(outputAmount.Min.Satoshi).Append('|').Append(outputAmount.Max.Satoshi);

        foreach (var (type, config) in credentials.OrderBy(kvp => kvp.Key))
        {
            sb.Append('|').Append(type);
            sb.Append('|').Append(config.Max);
            sb.Append('|').Append(config.IssuanceIn.Min).Append('|').Append(config.IssuanceIn.Max);
            sb.Append('|').Append(config.IssuanceOut.Min).Append('|').Append(config.IssuanceOut.Max);
            sb.Append('|').Append(config.UseBulletproofs);
        }

        if (blameOf is not null)
            sb.Append('|').Append(blameOf);

        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLower();
    }
}
