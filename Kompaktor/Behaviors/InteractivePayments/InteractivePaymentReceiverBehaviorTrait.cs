using System.Collections.Concurrent;
using Kompaktor.Contracts;
using Kompaktor.Credentials;
using Kompaktor.Mapper;
using Kompaktor.Models;
using Kompaktor.Utils;
using Microsoft.Extensions.Logging;
using NBitcoin.Secp256k1;
using WabiSabi.Crypto.ZeroKnowledge;

namespace Kompaktor.Behaviors.InteractivePayments;

public class InteractivePaymentReceiverBehaviorTrait : KompaktorClientBaseBehaviorTrait
{
    private ILogger Logger => Client.Logger;
    private readonly IInboundPaymentManager _inboundPaymentManager;
    private readonly IKompaktorPeerCommunicationApi _kompaktorPeerCommunicationApi;
    private readonly ConcurrentBag<InteractivePendingPaymentReceiverFlow> _flows = new();

    public InteractivePaymentReceiverBehaviorTrait(IInboundPaymentManager inboundPaymentManager,
        IKompaktorPeerCommunicationApi kompaktorPeerCommunicationApi)
    {
        _inboundPaymentManager = inboundPaymentManager;
        _kompaktorPeerCommunicationApi = kompaktorPeerCommunicationApi;
    }


    public override void Start(KompaktorRoundClient client)
    {
        base.Start(client);
        _ = StartListen();
    }

    private readonly CancellationTokenSource _flowCts = new();

    protected override async Task OnStatusChanged(object sender, KompaktorStatus phase)
    {
        switch (phase)
        {
            case KompaktorStatus.Signing:
                foreach (var interactivePendingPaymentReceiverFlow in _flows)
                {
                    interactivePendingPaymentReceiverFlow.TxId = Client.GetTransaction().GetHash();
                }

                break;
            case KompaktorStatus.Failed:
                foreach (var interactivePendingPaymentReceiverFlow in _flows)
                {
                    await _inboundPaymentManager.BreakCommitment(interactivePendingPaymentReceiverFlow.Payment.Id);
                }

                break;
            case > KompaktorStatus.Signing:
                await _flowCts.CancelAsync();
                break;
        }
    }

    private async Task StartListen()
    {
        var pp = (await _inboundPaymentManager.GetInboundPendingPayments(false))
            .OfType<InteractiveReceiverPendingPayment>().ToDictionary(
                payment => (XPubKey) payment.KompaktorKey!.Key.CreateXOnlyPubKey(),
                payment => new InteractivePendingPaymentReceiverFlow(Logger, payment, _kompaktorPeerCommunicationApi,
                    Reissue, WaitUntilReady));
        await foreach (var msg in _kompaktorPeerCommunicationApi.Messages(_flowCts.Token))
        {
            if (msg.Length != 64 || !ECXOnlyPubKey.TryCreate(msg[..32], out var p1) ||
                !pp.TryGetValue(p1, out var flow))
            {
                continue;
            }

            Logger.LogInformation("Received message for intent of payment");
            var p2 = ECXOnlyPubKey.Create(msg[32..]);

            if (await _inboundPaymentManager.Commit(flow.Payment.Id))
            {
                _ = flow.Start(p2, _flowCts.Token);
                _flows.Add(flow);
            }
        }
    }

    private async Task<bool> WaitUntilReady()
    {
        while (!_flowCts.IsCancellationRequested &&
               !Client.ShouldSign())
        {
            await Task.Delay(100, _flowCts.Token);
        }
        return Client.ShouldSign();
    }

    private async Task Reissue(Credential[] arg)
    {
        var config = Client.Round.RoundEventCreated.Credentials[CredentialType.Amount];
        var outs = new List<long> {arg.Sum(credential => credential.Value)};
        //add 0s until you max out reissuance
        while (outs.Count < config.IssuanceOut.Max)
        {
            outs.Add(0);
        }

        await Client.Reissue(arg, outs.ToArray());

        foreach (var credential in arg)
        {
            Client.AllCredentials.TryAdd(credential.Mac.Serial(), new BlindedCredential(credential));
        }
    }
}