using System.Text.Json.Serialization;
using Kompaktor.Credentials;
using Kompaktor.JsonConverters;
using NBitcoin;
using WabiSabi.CredentialRequesting;

namespace Kompaktor.Models;

public record RegisterOutputRequest : CredentialReissuanceRequest
{
    public RegisterOutputRequest(Dictionary<CredentialType, ICredentialsRequest> CredentialsRequest, TxOut Output) :
        base(CredentialsRequest)
    {
        this.Output = Output;
    }

    [JsonPropertyName("output")]
    [JsonConverter(typeof(TxOutJsonConverter))]
    public TxOut Output { get; init; }

    public void Deconstruct(out Dictionary<CredentialType, ICredentialsRequest> CredentialsRequest, out TxOut Output)
    {
        CredentialsRequest = this.CredentialsRequest;
        Output = this.Output;
    }
}