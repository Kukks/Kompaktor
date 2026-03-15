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
    public int MaxConcurrentFlows { get; }
    private ILogger Logger => Client.Logger;
    private readonly IInboundPaymentManager _inboundPaymentManager;
    private readonly IKompaktorPeerCommunicationApi _kompaktorPeerCommunicationApi;
    private readonly ConcurrentBag<InteractivePendingPaymentReceiverFlow> _flows = new();

    public InteractivePaymentReceiverBehaviorTrait(IInboundPaymentManager inboundPaymentManager,
        IKompaktorPeerCommunicationApi kompaktorPeerCommunicationApi, int maxConcurrentFlows = 50)
    {
        MaxConcurrentFlows = maxConcurrentFlows;
        _inboundPaymentManager = inboundPaymentManager;
        _kompaktorPeerCommunicationApi = kompaktorPeerCommunicationApi;
    }


    public override void Start(KompaktorRoundClient client)
    {
        base.Start(client);
        _ = StartListen();
    }

    public override void Dispose()
    {
        _flowCts.Dispose();
        base.Dispose();
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


            case KompaktorStatus.Completed:
            {
                var success = 0;
                var failure = 0;
                foreach (var interactivePendingPaymentReceiverFlow in _flows.Where(flow => flow.Proof != null))
                {
                    await _inboundPaymentManager.AddProof(interactivePendingPaymentReceiverFlow.Payment.Id,
                        interactivePendingPaymentReceiverFlow.Proof!);
                    success++;
                }

                foreach (var interactivePendingPaymentReceiverFlow in _flows.Where(flow => flow.Proof == null))
                {
                    await _inboundPaymentManager.BreakCommitment(interactivePendingPaymentReceiverFlow.Payment.Id);
                    failure++;
                }

                Logger.LogInformation("Received {Success} interactive payments  {Failure} failures", success, failure);
                break;
            }
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
                    ReissueReceivedCredentials, WaitUntilReady));
        await foreach (var msg in _kompaktorPeerCommunicationApi.Messages(true, _flowCts.Token))
        {
            if (msg.Length != 64 || !ECXOnlyPubKey.TryCreate(msg[..32], out var p1) ||
                !pp.Remove(p1, out var flow))
            {
                continue;
            }

            Logger.LogInformation($"Received message for intent of payment {flow.Payment.Id}");
            var p2 = ECXOnlyPubKey.Create(msg[32..]);

            if (await _inboundPaymentManager.Commit(flow.Payment.Id))
            {
                _ = flow.Start(p2, _flowCts.Token);
                _flows.Add(flow);
                if (_flows.Count >= MaxConcurrentFlows)
                {
                   break;
                }
            }
        }
    }

    private async Task<bool> WaitUntilReady()
    {
        // Use IsOutputRegistrationComplete instead of ShouldSign to avoid deadlock
        // in mesh scenarios where this client also has a sender trait whose kill switch
        // would prevent ShouldSign from ever returning true
        while (!_flowCts.IsCancellationRequested &&
               !Client.IsOutputRegistrationComplete())
        {
            await Task.Delay(100, _flowCts.Token);
        }

        return Client.IsOutputRegistrationComplete();
    }

    private readonly ConcurrentHashSet<string> _reissued = new();

    private async Task ReissueReceivedCredentials(BlindedCredential[] arg)
    {
        try
        {
            var config = Client.Round.RoundEventCreated.Credentials[CredentialType.Amount];

            //separate the ins (0s and non-0s)
            var ins = arg.Where(credential => credential.Value != 0).ToList();
            var zeros = arg.Where(credential => credential.Value == 0).ToList();
            var tasks = new List<Task>();
            if (!config.IssuanceIn.Contains(ins.Count))
            {
                //if too many non-0s, split and recursively reissue
                if (config.IssuanceIn.Max < ins.Count)
                {
                    for (var i = 0; i < ins.Count;)
                    {
                        var toReissue = ins.Skip(i).Take(config.IssuanceIn.Max).ToArray();
                        tasks.Add(ReissueReceivedCredentials(toReissue));
                        i += toReissue.Length;
                    }

                    await Task.WhenAll(tasks);
                    return;
                }
                else if (config.IssuanceIn.Min > ins.Count)
                {
                    try
                    {
                        //add 0s until you hit the min
                        while (ins.Count < config.IssuanceIn.Min)
                        {
                            var zero = zeros.FirstOrDefault();
                            if (zero != null)
                            {
                                ins.Add(zero);
                                zeros.Remove(zero);
                            }
                            else
                            {
                                // Always generate fresh zeros to avoid TOCTOU races with
                                // concurrent ReissueReceivedCredentials calls and IssuanceTask
                                var newZeros = await Client.Generate0Credentials();
                                var needed = config.IssuanceIn.Min - ins.Count;
                                ins.AddRange(newZeros.Take(needed));
                            }
                        }
                    }
                    finally
                    {
                        foreach (var credential in ins)
                        {
                            _reissued.Add(credential.Mac.Serial());
                        }
                    }
                }
            }

            // Mark all ins as allocated to prevent IssuanceTask from grabbing them
            foreach (var cred in ins)
            {
                Client.AllocatedCredentials.TryAdd(cred.Mac.Serial(), this);
            }

            var outs = new List<long> {ins.Sum(credential => credential.Value)};
            //add 0s until you max out reissuance
            while (outs.Count < config.IssuanceOut.Max)
            {
                outs.Add(0);
            }

            await Client.Reissue(ins.ToArray(), outs.ToArray());
            foreach (var credential in arg)
            {
                Client.AllCredentials.TryAdd(credential.Mac.Serial(), new BlindedCredential(credential));
            }
        }
        catch (Exception e)
        {
            Logger.LogError("Failed to reissue received credentials: {Error}", e.Message);
            throw;
        }
    }
}