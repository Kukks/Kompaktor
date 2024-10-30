using Kompaktor.Credentials;
using WabiSabi.CredentialRequesting;

namespace Kompaktor.Models;

public record KompaktorRoundEventOutputRegistered(
    RegisterOutputRequest Request,
    Dictionary<CredentialType, CredentialsResponse> Credentials) : KompaktorRoundEvent
{
    public override string ToString() => $"Output Registered: {Request.Output.ScriptPubKey} {Request.Output.Value}";
}