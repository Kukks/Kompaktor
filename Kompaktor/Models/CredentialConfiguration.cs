using System.Text.Json.Serialization;
using Kompaktor.JsonConverters;
using WabiSabi.Crypto;

namespace Kompaktor.Models;

public record CredentialConfiguration
{
    public CredentialConfiguration(long Max, IntRange IssuanceIn, IntRange IssuanceOut, CredentialIssuerParameters Parameters)
    {
        this.Max = Max;
        this.IssuanceIn = IssuanceIn;
        this.IssuanceOut = IssuanceOut;
        this.Parameters = Parameters;
    }

    [JsonPropertyName("max")]
    public long Max { get; init; }
    [JsonPropertyName("issuanceIn")]
    public IntRange IssuanceIn { get; init; }
    [JsonPropertyName("issuanceOut")]
    public IntRange IssuanceOut { get; init; }
    [JsonPropertyName("parameters")]
    [JsonConverter(typeof(CredentialIssuerParametersJsonConverter))]
    public CredentialIssuerParameters Parameters { get; init; }

    public void Deconstruct(out long Max, out IntRange IssuanceIn, out IntRange IssuanceOut, out CredentialIssuerParameters Parameters)
    {
        Max = this.Max;
        IssuanceIn = this.IssuanceIn;
        IssuanceOut = this.IssuanceOut;
        Parameters = this.Parameters;
    }
}