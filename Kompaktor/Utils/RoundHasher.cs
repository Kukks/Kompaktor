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
        Dictionary<CredentialType, CredentialConfiguration> credentials)
    {
        using var sha = SHA256.Create();
        var sb = new StringBuilder();

        sb.Append(startTime.ToUnixTimeMilliseconds());
        sb.Append(feeRate.FeePerK.Satoshi);
        sb.Append(inputTimeout.Ticks);
        sb.Append(outputTimeout.Ticks);
        sb.Append(signingTimeout.Ticks);
        sb.Append(inputCount.Min).Append(inputCount.Max);
        sb.Append(inputAmount.Min.Satoshi).Append(inputAmount.Max.Satoshi);
        sb.Append(outputCount.Min).Append(outputCount.Max);
        sb.Append(outputAmount.Min.Satoshi).Append(outputAmount.Max.Satoshi);

        foreach (var (type, config) in credentials.OrderBy(kvp => kvp.Key))
        {
            sb.Append(type);
            sb.Append(config.Max);
            sb.Append(config.IssuanceIn.Min).Append(config.IssuanceIn.Max);
            sb.Append(config.IssuanceOut.Min).Append(config.IssuanceOut.Max);
            sb.Append(config.UseBulletproofs);
        }

        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLower();
    }
}
