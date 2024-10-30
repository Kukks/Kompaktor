using Kompaktor.Contracts;
using NBitcoin;
using WabiSabi.Crypto;

namespace Kompaktor.Models;

public record KompaktorIdentity
{
    public KompaktorIdentity(IKompaktorRoundApi Api,
        OutPoint[]? RegisteredInputs,
        TxOut[]? RegisteredOutputs,
        MAC[]? SpentCredentials,
        MAC[]? CreatedCredentials,
        string? Secret,
        bool SignalledReady)
    {
        this.Api = Api;
        this.RegisteredInputs = RegisteredInputs?? Array.Empty<OutPoint>();
        this.RegisteredOutputs = RegisteredOutputs?? Array.Empty<TxOut>();
        this.SpentCredentials = SpentCredentials?? Array.Empty<MAC>();
        this.CreatedCredentials = CreatedCredentials?? Array.Empty<MAC>();
        this.Secret = Secret;
        this.SignalledReady = SignalledReady;
    }

    public IKompaktorRoundApi Api { get; init; }
    public OutPoint[]RegisteredInputs { get; init; }
    public TxOut[] RegisteredOutputs { get; init; }
    public MAC[] SpentCredentials { get; }
    public MAC[] CreatedCredentials { get; }
    public string? Secret { get; init; }
    public bool SignalledReady { get; set; }
}