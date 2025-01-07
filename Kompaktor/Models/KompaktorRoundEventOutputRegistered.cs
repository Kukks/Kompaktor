using System.Text.Json.Serialization;
using Kompaktor.Credentials;
using WabiSabi.CredentialRequesting;

namespace Kompaktor.Models;

public record KompaktorRoundEventOutputRegistered : KompaktorRoundEvent
{
    public KompaktorRoundEventOutputRegistered(RegisterOutputRequest Request,
        Dictionary<CredentialType, CredentialsResponse> Credentials)
    {
        this.Request = Request;
        this.Credentials = Credentials;
    }

    public override string ToString() => $"Output Registered: {Request.Output.ScriptPubKey} {Request.Output.Value}";
    [JsonPropertyName("request")]
    public RegisterOutputRequest Request { get; init; }
    [JsonPropertyName("credentials")]
    public Dictionary<CredentialType, CredentialsResponse> Credentials { get; init; }

    public void Deconstruct(out RegisterOutputRequest Request, out Dictionary<CredentialType, CredentialsResponse> Credentials)
    {
        Request = this.Request;
        Credentials = this.Credentials;
    }
}