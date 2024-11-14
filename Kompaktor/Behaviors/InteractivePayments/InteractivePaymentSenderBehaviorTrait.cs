using Kompaktor.Contracts;
using Kompaktor.Models;
using Kompaktor.Utils;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace Kompaktor.Behaviors.InteractivePayments;

public class InteractivePaymentSenderBehaviorTrait : KompaktorClientBaseBehaviorTrait
{
    private  ILogger _logger => Client.Logger;
    private readonly IOutboundPaymentManager _outboundPaymentManager;
    private readonly IKompaktorPeerCommunicationApi _communicationApi;

    public InteractivePaymentSenderBehaviorTrait( IOutboundPaymentManager outboundPaymentManager,
        IKompaktorPeerCommunicationApi communicationApi)
    {
        _outboundPaymentManager = outboundPaymentManager;
        _communicationApi = communicationApi;
    }

    private CancellationTokenSource _cts = new();
    private CancellationTokenSource paymentEndCts = new();

    public override void Start(KompaktorRoundClient client)
    {
        base.Start(client);
        Client.StartCoinSelection += OnStartCoinSelection;
        Client.FinishedCoinRegistration += OnFinishedCoinRegistration;
        Client.StartSigning += OnStartSigning;
    }

    private async Task OnStartSigning(object sender)
    {
        Client.DoNotSignKillSwitches.AddOrReplace(this, Committed.Any(flow => !flow.CanSign));
        foreach (var interactivePendingPaymentSenderFlow in Committed)
        {
            interactivePendingPaymentSenderFlow.TxId = Client.GetTransaction().GetHash();
        }
        _logger.LogInformation("interactive payment sender can sign: {0}",
            Committed.All(flow => flow.CanSign));
        await Task.WhenAll(Committed.Select(flow => flow.WaitUntilOkToSign(_cts.Token)));

        Client.DoNotSignKillSwitches.AddOrReplace(this, Committed.Any(flow => !flow.CanSign));
        
        _logger.LogInformation("interactive payment sender can sign: {0}",
            Committed.All(flow => flow.CanSign));
    }

    public InteractivePendingPaymentSenderFlow[] Committed { get; private set; }
    private List<PendingPayment> _toFulfill;

    /// <summary>
    ///  Check that the previously selected coins and payments were selected and that the payments can be cmmitted to, else use whatever coins were registered and try to fulfill as many othe rpayments as possible
    /// </summary>
    /// <param name="sender"></param>
    private async Task OnFinishedCoinRegistration(object sender)
    {
        var credentials =
            Client!.AvailableCredentialsForTrait(this);
        var credentialsAmounts = credentials.Select(credential => Money.Satoshis(credential.Value)).ToArray();

        var computed = await ComputePendingPayments(credentialsAmounts, _toFulfill.ToArray(), true);
        var tasks = new List<Task>();
        var flows = new List<InteractivePendingPaymentSenderFlow>();
        foreach (var flow in computed.SelectedPayments.Select(payment =>
                     new InteractivePendingPaymentSenderFlow(_logger, payment, _communicationApi)))
        {
            flows.Add(flow);
            tasks.Add(flow.SignalIntentPay(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token));
        }

        await Task.WhenAll(tasks);
        foreach (var failed in flows.Where(flow => !flow.Continue).ToArray())
        {
            await _outboundPaymentManager.BreakCommitment(failed.Payment.Id);
        }


        flows = flows.Where(flow => flow.Continue).ToList();
        Client.DoNotSignKillSwitches.AddOrReplace(this, flows.Any(flow => !flow.SignalReady));
        var workingCredentials = credentials.ToList();
        tasks = new List<Task>();
        foreach (var flow in flows)
        {
            if (flow.P3 is null)
            {
                Client.AllocatedPlannedOutputs.TryAdd(flow.Payment.TxOut(), this);
                flow.RegisteredOutput = true;
                continue;
            }

            while (workingCredentials.All(credential => credential.Value != flow.Payment.Amount.Satoshi))
            {
                var c1 = workingCredentials.First();
                var c2 = workingCredentials.LastOrDefault(blindedCredential =>
                    blindedCredential.Value != 0 && blindedCredential != c1) ?? workingCredentials.Last();
                var sum = c1.Value + c2.Value;
                var o1 = flow.Payment.Amount.Satoshi >= (c1.Value + c2.Value)
                    ? (c1.Value + c2.Value)
                    : flow.Payment.Amount.Satoshi;
                var o2 = sum - o1;

                Client.AllocatedCredentials.TryAdd(c1.Mac.Serial(), this);
                Client.AllocatedCredentials.TryAdd(c2.Mac.Serial(), this);
                var newCreds = await Client.Reissue([c1, c2], new[] {o1, o2});
                
               
                workingCredentials.AddRange(newCreds);
                workingCredentials.Remove(c1);
                workingCredentials.Remove(c2);
                
            }

            workingCredentials.AddRange(await Client.Generate0Credentials());
            var credential = workingCredentials.First(credential => credential.Value == flow.Payment.Amount.Satoshi);
            var zeroCredential = workingCredentials.First(credential => credential.Value == 0);


            workingCredentials.Remove(credential);
            workingCredentials.Remove(zeroCredential);
            Client.AllocatedCredentials.TryAdd(credential.Mac.Serial(), this);
            Client.AllocatedCredentials.TryAdd(zeroCredential.Mac.Serial(), this);
            flow.AssignedCredentials = new[] {credential, zeroCredential};
            
            tasks.Add(flow.SendPayment(_cts.Token).ContinueWith(task =>
            {
                if (flow.P5 is null)
                {
                    foreach (var flowAssignedCredential in flow.AssignedCredentials)
                    {
                        Client.AllocatedCredentials.TryRemove(flowAssignedCredential.Mac.Serial(), out _);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        var processManuallyIfPossible = flows.Where(flow => flow.P5 is null).ToDictionary(flow => flow.Payment, flow => flow);
        credentials =
            Client!.AvailableCredentialsForTrait(this);
        credentialsAmounts = credentials.Select(credential => Money.Satoshis(credential.Value)).ToArray();

        computed = await ComputePendingPayments(credentialsAmounts, processManuallyIfPossible.Keys.ToArray(), false);
        foreach (var payment in processManuallyIfPossible)
        {
            if(computed.SelectedPayments.Contains(payment.Key))
            {
                Client.AllocatedPlannedOutputs.TryAdd(payment.Key.TxOut(), this);
                payment.Value.RegisteredOutput = true;
            }
            else
            {
                await _outboundPaymentManager.BreakCommitment(payment.Key.Id);
                
            }
            
        }
        flows = flows.Where(flow => flow.P3 is not null || flow.RegisteredOutput).ToList();
        
        while (Client.AllocatedSelectedCoins.FirstOrDefault(pair => pair.Value == this) is
               {Key: not null, Value: not null} pair)
        {
            Client.AllocatedSelectedCoins.AddOrReplace(pair.Key, null);
        }

        Committed = flows.ToArray();
        _ = Task.WhenAll(flows.Select(flow => flow.WaitForReadyToSignal(_cts.Token))).ContinueWith(task =>
        {
            _logger.LogInformation("All send flows completed, ready to sign: {0}",
                flows.All(flow => flow.SignalReady));
            Client.DoNotSignKillSwitches.AddOrReplace(this, flows.Any(flow => !flow.SignalReady));
        });
        _logger.LogInformation("Committed to handling {0} payments", Committed.Length);
    }


    /// <summary>
// select the optimal number of payments to pay with the available coins
    /// </summary>
    /// <param name="coins">the available coins</param>
    /// <param name="list">the list of pending payments</param>
    /// <param name="commit">COmmit to paying, and if false, skip that payment </param>
    /// <returns></returns>
    private async Task<(List<PendingPayment> SelectedPayments, Money[] RequiredCoins)> ComputePendingPayments(
        Money[] inputCoins,
        PendingPayment[] list, bool commit)
    {
        var toFulfill = new List<PendingPayment>();
        var toFulfillAmount = Money.Zero;
        var payments = list.OrderByDescending(p => p.Amount).ToList();
        var availableCoins = inputCoins.OrderDescending().ToList();
        var selectedCoins = new List<Money>();

        var feerate = Client.Round.RoundEventCreated.FeeRate;
        foreach (var payment in payments)
        {
            var balance = selectedCoins.Sum() - toFulfillAmount;
            Money remainingPayment = payment.TxOut().EffectiveCost(feerate) - balance;
            var currentSelectedCoins = new List<Money>();

            foreach (var coin in availableCoins.ToList()) // Clone list to modify during iteration
            {
                if (coin >= remainingPayment)
                {
                    currentSelectedCoins.Add(coin);
                    remainingPayment -= coin;
                    availableCoins.Remove(coin); // Remove coin from available pool
                }

                // If we have fulfilled the payment
                if (remainingPayment <= Money.Zero)
                {
                    break;
                }
            }

            // If we can fully fulfill the payment, add to the selected coins and fulfill the payment
            if (remainingPayment.Satoshi <= 0 && (!commit || await _outboundPaymentManager.Commit(payment.Id)))
            {
                selectedCoins.AddRange(currentSelectedCoins);
                toFulfill.Add(payment);
                toFulfillAmount += payment.TxOut().EffectiveCost(feerate);
            }
        }

        _logger.LogInformation("Selected {0} payments to fulfill with {1} coins", toFulfill.Count, selectedCoins.Count);
        return (toFulfill, selectedCoins.ToArray());
    }

    private async Task OnStartCoinSelection(object sender)
    {
        var cc = Client.RemainingCoinCandidates;
        if (cc?.Any() is not true)
        {
            return;
        }

        var ccc = cc.Value.ToDictionary(coin => coin,
            coin => coin.EstimateEffectiveValue(Client.Round.RoundEventCreated.FeeRate));
        var payments = (await _outboundPaymentManager.GetOutboundPendingPayments(false))
            .OrderByDescending(payment => payment is InteractivePendingPayment).ToArray();
        var computed = await ComputePendingPayments(ccc.Values.ToArray(),
            payments, false);
        if (!computed.Item1.Any())
        {
            return;
        }
        //
        // var selectedPayments = new Dictionary<PendingPayment, InteractivePendingPaymentSenderFlow>();
        // foreach (var selectedPayment in computed.SelectedPayments)
        // {
        //     
        //     var flow = new InteractivePendingPaymentSenderFlow(selectedPayment, _communicationApi);
        //     
        //     await flow.SignalIntentPay(cts.Token);
        //     if (flow.Continue)
        //     {
        //         selectedPayments.Add(selectedPayment, flow);
        //     }
        // }
        // if(selectedPayments.Keys.Count != computed.SelectedPayments.Count)
        // {
        //     computed = await ComputePendingPayments(ccc.Values.ToArray(),
        //         payments, false);
        //   
        // }


        // make use of InteractivePendingPaymentSenderFlow to signal intent to pay if the payment is interactive
        // otherwise just use it as normal
        // if it is interactive but urgent, do it as normal payment
        // we can check if signalling failed (because it is not interactive or because the eceiver is offfline by checking if the P3 is null)

        // clean up the computed payments  list, and see 
        // if we can add more payments to the list due to reduction of payments and excess coins


        // Allocate selected coins to the client
        foreach (var effectiveValue in computed.Item2)
        {
            var coin = ccc.First(pair => pair.Value == effectiveValue).Key;
            ccc.Remove(coin);
            Client.AllocatedSelectedCoins.TryAdd(coin, this);
        }

        _toFulfill = computed.Item1;
    }


    protected override async Task OnStatusChanged(object sender, KompaktorStatus phase)
    {
        switch (phase)
        {
            case KompaktorStatus.Failed:
            {
                
                foreach (var payment in Committed)
                {
                    await _outboundPaymentManager.BreakCommitment(payment.Payment.Id);
                }

                break;
            }
            case KompaktorStatus.Completed:
            {
                var txId = Client.GetTransaction().GetHash();
                Committed.Where(flow => flow.Proof is not null)
                    .Select(flow =>
                        _outboundPaymentManager.AddProof(flow.Payment.Id, new KompaktorOffchainPaymentProof(txId,
                            flow.Payment.Amount.Satoshi,
                            ((InteractivePendingPayment) flow.Payment).KompaktorPubKey!, flow.Proof))).ToArray();
                break;
            }
        }
    }


    public override void Dispose()
    {
        Client.StartCoinSelection -= OnStartCoinSelection;
        Client.FinishedCoinRegistration -= OnFinishedCoinRegistration;
        _cts.Cancel();
    }
}