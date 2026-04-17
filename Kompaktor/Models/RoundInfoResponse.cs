using System.Text.Json.Serialization;
using Kompaktor.Credentials;
using Kompaktor.JsonConverters;
using NBitcoin;

namespace Kompaktor.Models;

/// <summary>
/// Round parameters returned by the info endpoint.
/// Used by clients to verify round consistency across isolated circuits
/// and detect coordinator equivocation.
/// </summary>
public record RoundInfoResponse
{
    [JsonPropertyName("roundId")]
    public string RoundId { get; init; } = "";

    [JsonPropertyName("feeRate")]
    public long FeeRateSatPerK { get; init; }

    [JsonPropertyName("inputTimeout")]
    public long InputTimeoutTicks { get; init; }

    [JsonPropertyName("outputTimeout")]
    public long OutputTimeoutTicks { get; init; }

    [JsonPropertyName("signingTimeout")]
    public long SigningTimeoutTicks { get; init; }

    [JsonPropertyName("inputCountMin")]
    public int InputCountMin { get; init; }

    [JsonPropertyName("inputCountMax")]
    public int InputCountMax { get; init; }

    [JsonPropertyName("inputAmountMin")]
    public long InputAmountMinSat { get; init; }

    [JsonPropertyName("inputAmountMax")]
    public long InputAmountMaxSat { get; init; }

    [JsonPropertyName("outputCountMin")]
    public int OutputCountMin { get; init; }

    [JsonPropertyName("outputCountMax")]
    public int OutputCountMax { get; init; }

    [JsonPropertyName("outputAmountMin")]
    public long OutputAmountMinSat { get; init; }

    [JsonPropertyName("outputAmountMax")]
    public long OutputAmountMaxSat { get; init; }

    [JsonPropertyName("credentials")]
    public Dictionary<string, CredentialConfiguration> Credentials { get; init; } = new();

    [JsonPropertyName("allowedInputTypes")]
    public HashSet<ScriptType> AllowedInputTypes { get; init; } = [];

    [JsonPropertyName("isBlameRound")]
    public bool IsBlameRound { get; init; }

    [JsonPropertyName("blameOf")]
    public string? BlameOf { get; init; }

    /// <summary>
    /// Creates a RoundInfoResponse from a KompaktorRoundEventCreated event.
    /// </summary>
    public static RoundInfoResponse FromCreatedEvent(KompaktorRoundEventCreated created)
    {
        return new RoundInfoResponse
        {
            RoundId = created.RoundId,
            FeeRateSatPerK = created.FeeRate.FeePerK.Satoshi,
            InputTimeoutTicks = created.InputTimeout.Ticks,
            OutputTimeoutTicks = created.OutputTimeout.Ticks,
            SigningTimeoutTicks = created.SigningTimeout.Ticks,
            InputCountMin = created.InputCount.Min,
            InputCountMax = created.InputCount.Max,
            InputAmountMinSat = created.InputAmount.Min.Satoshi,
            InputAmountMaxSat = created.InputAmount.Max.Satoshi,
            OutputCountMin = created.OutputCount.Min,
            OutputCountMax = created.OutputCount.Max,
            OutputAmountMinSat = created.OutputAmount.Min.Satoshi,
            OutputAmountMaxSat = created.OutputAmount.Max.Satoshi,
            Credentials = created.Credentials.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => kvp.Value),
            AllowedInputTypes = created.AllowedInputTypes,
            IsBlameRound = created.IsBlameRound,
            BlameOf = created.BlameOf
        };
    }

    /// <summary>
    /// Compares this response against the local round creation event.
    /// Returns a list of mismatched field names, or empty if consistent.
    /// </summary>
    public List<string> CompareWith(KompaktorRoundEventCreated local)
    {
        var mismatches = new List<string>();

        if (RoundId != local.RoundId)
            mismatches.Add("RoundId");
        if (FeeRateSatPerK != local.FeeRate.FeePerK.Satoshi)
            mismatches.Add("FeeRate");
        if (InputTimeoutTicks != local.InputTimeout.Ticks)
            mismatches.Add("InputTimeout");
        if (OutputTimeoutTicks != local.OutputTimeout.Ticks)
            mismatches.Add("OutputTimeout");
        if (SigningTimeoutTicks != local.SigningTimeout.Ticks)
            mismatches.Add("SigningTimeout");
        if (InputCountMin != local.InputCount.Min || InputCountMax != local.InputCount.Max)
            mismatches.Add("InputCount");
        if (InputAmountMinSat != local.InputAmount.Min.Satoshi || InputAmountMaxSat != local.InputAmount.Max.Satoshi)
            mismatches.Add("InputAmount");
        if (OutputCountMin != local.OutputCount.Min || OutputCountMax != local.OutputCount.Max)
            mismatches.Add("OutputCount");
        if (OutputAmountMinSat != local.OutputAmount.Min.Satoshi || OutputAmountMaxSat != local.OutputAmount.Max.Satoshi)
            mismatches.Add("OutputAmount");
        if (!AllowedInputTypes.SetEquals(local.AllowedInputTypes))
            mismatches.Add("AllowedInputTypes");
        if (IsBlameRound != local.IsBlameRound)
            mismatches.Add("IsBlameRound");
        if (BlameOf != local.BlameOf)
            mismatches.Add("BlameOf");

        // Compare credential issuer parameters — this is the critical check.
        // A malicious coordinator could serve different issuer keys to different clients,
        // enabling it to distinguish which credentials came from which participant.
        foreach (var (type, config) in local.Credentials)
        {
            var key = type.ToString();
            if (!Credentials.TryGetValue(key, out var remoteConfig))
            {
                mismatches.Add($"Credentials[{key}].Missing");
                continue;
            }

            if (config.Max != remoteConfig.Max)
                mismatches.Add($"Credentials[{key}].Max");
            if (config.IssuanceIn != remoteConfig.IssuanceIn)
                mismatches.Add($"Credentials[{key}].IssuanceIn");
            if (config.IssuanceOut != remoteConfig.IssuanceOut)
                mismatches.Add($"Credentials[{key}].IssuanceOut");
            if (config.UseBulletproofs != remoteConfig.UseBulletproofs)
                mismatches.Add($"Credentials[{key}].UseBulletproofs");
            if (config.Parameters.Cw != remoteConfig.Parameters.Cw ||
                config.Parameters.I != remoteConfig.Parameters.I)
                mismatches.Add($"Credentials[{key}].IssuerParameters");
        }

        foreach (var key in Credentials.Keys)
        {
            if (!local.Credentials.ContainsKey(Enum.Parse<CredentialType>(key)))
                mismatches.Add($"Credentials[{key}].Extra");
        }

        return mismatches;
    }
}
