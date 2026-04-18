using System.Diagnostics;
using Kompaktor.Contracts;
using Kompaktor.Mapper;
using Kompaktor.Models;
using Kompaktor.Utils;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Secp256k1;
using WabiSabi.Crypto.ZeroKnowledge;

namespace Kompaktor.Behaviors.InteractivePayments;

public class InteractivePendingPaymentSenderFlow
{
    private readonly ILogger _logger;
    public readonly PendingPayment Payment;
    private readonly IKompaktorPeerCommunicationApi _communicationApi;
    private uint256? _txId;

    public InteractivePendingPaymentSenderFlow(ILogger logger, PendingPayment payment,
        IKompaktorPeerCommunicationApi communicationApi)
    {
        _logger = logger;
        Payment = payment;
        _communicationApi = communicationApi;
    }

    public XPubKey? P1 => Payment is InteractivePendingPayment interactivePendingPayment
        ? interactivePendingPayment.KompaktorPubKey
        : null;

    public PrivKey? P2 { get; set; }
    public XPubKey? P3 { get; set; }
    public PrivKey? P4 { get; set; }
    public XPubKey? P5 { get; set; }
    public XPubKey? P6 { get; set; }

    public uint256? TxId
    {
        get => _txId;
        set
        {
            _txId = value;
            TxIdSet.SetResult();
        }
    }

    private TaskCompletionSource TxIdSet { get; } = new();
    public KompaktorOffchainPaymentProof? Proof { get; set; }


    public Credential[] AssignedCredentials { get; set; }


    public bool Continue => Payment is not InteractivePendingPayment interactivePendingPayment ||
                            (P3 is not null || interactivePendingPayment.Urgent);

    public bool CanSign => Payment is not InteractivePendingPayment interactivePendingPayment ||
                           (Continue && (Proof is not null || RegisteredOutput));

    public bool SignalReady => Payment is not InteractivePendingPayment interactivePendingPayment ||
                               (Continue && (P6 is not null || RegisteredOutput));

    public bool RegisteredOutput { get; set; }

    public async Task SignalIntentPay(CancellationToken cancellationToken)
    {
        if (P1 is null)
        {
            return;
        }

        P2 ??= ECPrivKey.Create(RandomUtils.GetBytes(32));

        var receivedMessageTask = _communicationApi.WaitForMessage( P2.ToXPubKey().ToBytes(), cancellationToken,
            $"intent to pay ack {Payment.Id}");
        await _communicationApi.SendMessageAsync(IntentPay(), $"intent to pay {Payment.Id}");
        try
        {
            var receivedMessage = await receivedMessageTask;
            
            P3 = ECXOnlyPubKey.Create(receivedMessage[32..]);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation($"Timeout waiting for response {Payment.Id}");
        }
    }

    public async Task SendPayment(CancellationToken cancellationToken)
    {
        if (RegisteredOutput)
        {
            return;
        }

        if (P3 is null || AssignedCredentials.Sum(credential => credential.Value) != Payment.Amount.Satoshi)
        {
            _logger.LogWarning("Payment {Id}: invalid credentials sum, skipping", Payment.Id);
            return;
        }

        try
        {
            P4 ??= ECPrivKey.Create(RandomUtils.GetBytes(32));
            var receivedMessage = await _communicationApi.SendAndWaitForMessageAsync(Pay(),
                P4.ToXPubKey().ToBytes(), cancellationToken,
                $"credential payment {Payment.Id}");

            P5 = ECXOnlyPubKey.Create(receivedMessage[32..]);
            _logger.LogInformation("Received cred ack for payment {Id}", Payment.Id);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Payment {Id}: timeout waiting for cred ack", Payment.Id);
        }
        catch (Exception e)
        {
            _logger.LogException($"Payment {Payment.Id}: error during credential send", e);
        }
    }

    public async Task WaitForReadyToSignal(CancellationToken cancellationToken)
    {
        if(RegisteredOutput  || P5 is null)
        {
            return;
        }
        var receivedMessageTask =  _communicationApi.WaitForMessage(P5.ToBytes(), cancellationToken,
            $"Waiting for ready to sign {Payment.Id}");
        var receivedMessage = await receivedMessageTask;
        _logger.LogInformation($"Received ready {Payment.Id}");
        P6 = ECXOnlyPubKey.Create(receivedMessage[32..]);
    }

    public async Task WaitUntilOkToSign(CancellationToken cancellationToken)
    {
        if (RegisteredOutput)
        {
            return;
        }

        try
        {
            await TxIdSet.Task.WithCancellation(cancellationToken);
            if (P6 is null || TxId is null)
            {
                return;
            }

            var receivedMessageTask =
                _communicationApi.WaitForMessage(P6.ToBytes(), cancellationToken,
                    $"waiting for proof {Payment.Id}");
            var receivedMessage = await receivedMessageTask;
            if (SecpSchnorrSignature.TryCreate(receivedMessage[32..], out var sig))
            {
                var proof = new KompaktorOffchainPaymentProof(TxId, Payment.Amount.Satoshi, P1, sig);
                if (proof.Verify())
                {
                    _logger.LogInformation("Received valid proof for payment {Id}", Payment.Id);
                    Proof = proof;
                    return;
                }
            }

            _logger.LogWarning("Payment {Id}: received invalid proof, payment will not complete this round",
                Payment.Id);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Payment {Id} proof wait cancelled", Payment.Id);
        }
        catch (Exception ex)
        {
            _logger.LogException($"Payment {Payment.Id}: proof verification failed", ex);
        }
    }


    private byte[] IntentPay()
    {
        return P1!.ToBytes().Concat(P2.ToXPubKey().ToBytes()).ToArray();
    }

    private byte[] Pay()
    {
        return P3.ToBytes().Concat(P4.ToXPubKey().ToBytes())
            .Concat(AssignedCredentials.SelectMany(credential => credential.ToBytes())).ToArray();
    }
}