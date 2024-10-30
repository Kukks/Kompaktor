using System.Diagnostics.CodeAnalysis;
using System.Text;
using Kompaktor.Contracts;
using Kompaktor.Models;
using Kompaktor.Utils;
using System.Threading;
using System.Threading.Tasks;
using Kompaktor.Models;
using NBitcoin;
using NBitcoin.Payment;
using NBitcoin.RPC;
using NBitcoin.Secp256k1;
using NNostr.Client.Crypto;

namespace Kompaktor.Behaviors;

public class InteractivePaymentBehaviorTrait : KompaktorClientBaseBehaviorTrait
{
    private readonly ILogger _logger;
    private readonly IOutboundPaymentManager _outboundPaymentManager;
    private readonly IKompaktorPeerCommunicationApi _communicationApi;

    public InteractivePaymentBehaviorTrait(ILogger logger, IOutboundPaymentManager outboundPaymentManager,
        IKompaktorPeerCommunicationApi communicationApi)
    {
        _logger = logger;
        _outboundPaymentManager = outboundPaymentManager;
        _communicationApi = communicationApi;
        Client.StatusChanged += OnStatusChanged;
        await HandleInteractivePayments(CancellationToken.None);
    }

    private async Task HandleInteractivePayments(CancellationToken cancellationToken)
    {
        foreach (var payment in _toFulfill.OfType<InteractivePendingPayment>())
        {
            var flow = new InteractivePendingPaymentSenderFlow(payment, _communicationApi);
            await flow.SignalIntentPay(cancellationToken);
            await flow.SendPayment(cancellationToken);
            await flow.WaitUntilReady(cancellationToken);

            if (flow.Proof != null)
            {
                _logger.LogInformation("Payment {0} completed successfully.", payment.Id);
            }
            else
            {
                _logger.LogWarning("Payment {0} failed to complete.", payment.Id);
            }
        }
    }


    public override void Start(KompaktorRoundClient client)
    {
        base.Start(client);
        Client.StartCoinSelection += OnStartCoinSelection;
        Client.FinishedCoinRegistration += OnFinishedCoinRegistration;
    }

    public PendingPayment[] Committed { get; private set; }
    private PendingPayment[] _toFulfill;

    /// <summary>
    ///  Check that the previously selected coins and payments were selected and that the payments can be cmmitted to, else use whatever coins were registered and try to fulfill as many othe rpayments as possible
    /// </summary>
    /// <param name="sender"></param>
    private async Task OnFinishedCoinRegistration(object sender)
    {
        // Check if we can pay all TooFulfill payments with the registered coins
        var committedPayments = new List<PendingPayment>();

        var credentials =
            Money.Satoshis(Client!.AvailableCredentialsForTrait(this).Sum(credential => credential.Value));

        var computed = await ComputePendingPayments(new[] {credentials}, _toFulfill, true);
        foreach (var payment in computed.SelectedPayments)
        {
            committedPayments.Add(payment);
            Client.AllocatedPlannedOutputs.TryAdd(payment.TxOut(), this);
        }
        //TODO: Try and see if we can add in some more payments if we have leftover due to failure to commit or extra coins added from other traits without an exclusive trait

        while (Client.AllocatedSelectedCoins.FirstOrDefault(pair => pair.Value == this) is
               {Key: not null, Value: not null} pair)
        {
            Client.AllocatedSelectedCoins.AddOrReplace(pair.Key, null);
        }

        Committed = committedPayments.ToArray();
        _logger.LogInformation("Committed to handling {0} payments", Committed.Length);
    }

    /// <summary>
// select the optimal number of payments to pay with the available coins
    /// </summary>
    /// <param name="coins">the available coins</param>
    /// <param name="list">the list of pending payments</param>
    /// <param name="commit">COmmit to paying, and if false, skip that payment </param>
    /// <returns></returns>
    private async Task<(PendingPayment[] SelectedPayments, Money[] RequiredCoins)> ComputePendingPayments(
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
        return (toFulfill.ToArray(), selectedCoins.ToArray());
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
        var payments = (await _outboundPaymentManager.GetOutboundPendingPayments(false)).OrderByDescending(payment => payment is InteractivePendingPayment).ToArray();
        var computed = await ComputePendingPayments(ccc.Values.ToArray(),
            payments, false);
        if (!computed.Item1.Any())
        {
            return;
        }
        CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
     
        // make use of InteractivePendingPaymentSenderFlow to signal intent to pay if the payment is interactive
        // otherwise just use it as normal
        // if it is interactive but urgent, do it as normal payment
        // we can check if signalling failed (because it is not interactive or because the eceiver is offfline by checking if the P3 is null)
        
        // clean up the computed payments  list, and see 
        // if we can add more payments to the list due to reduction of payments and excess coins
        //TODO:!
        
        // Allocate selected coins to the client
        foreach (var effectiveValue in computed.Item2)
        {
            var coin = ccc.First(pair => pair.Value == effectiveValue).Key;
            ccc.Remove(coin);
            Client.AllocatedSelectedCoins.TryAdd(coin, this);
        }
        _toFulfill = computed.Item1;
    }


    protected virtual async Task OnStatusChanged(object sender, KompaktorStatus phase)
    {
    }


    public override void Dispose()
    {
        Client.StatusChanged -= OnStatusChanged;
        Client.StartCoinSelection -= OnStartCoinSelection;
        Client.FinishedCoinRegistration -= OnFinishedCoinRegistration;
    }
}
