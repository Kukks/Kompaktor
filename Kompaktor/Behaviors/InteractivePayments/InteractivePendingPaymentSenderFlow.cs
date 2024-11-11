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
    public SecpSchnorrSignature? Proof { get; set; }


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

        _logger.LogInformation($"Sending intent to pay {Payment.Id}");
        await _communicationApi.SendMessageAsync(IntentPay());
        try
        {
            await foreach (var receivedMessage in _communicationApi.Messages(cancellationToken))
            {
                if (receivedMessage.Length != 64)
                {
                    continue;
                }

//check that the first 32 bytes are a valid pubkey and is p2
                if (!ECXOnlyPubKey.TryCreate(receivedMessage[..32], out var pkey2) || ((XPubKey) pkey2) != P2)
                {
                    continue;
                }

                _logger.LogInformation($"Response received. Continue with interactive payment {Payment.Id}");

                if (ECXOnlyPubKey.TryCreate(receivedMessage[32..], out var pkey3))
                {
                    P3 = pkey3;
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation($"Timeout waiting for response {Payment.Id}");
        }
    }

    public async Task SendPayment(CancellationToken cancellationToken)
    {
        if (P3 is null || AssignedCredentials.Sum(credential => credential.Value) != Payment.Amount.Satoshi)
        {
            return;
        }

        P4 ??= ECPrivKey.Create(RandomUtils.GetBytes(32));

        _logger.LogInformation($"Sending credential payment {Payment.Id}");
        await _communicationApi.SendMessageAsync(Pay());
        await foreach (var receivedMessage in _communicationApi.Messages(cancellationToken))
        {
            if (receivedMessage.Length != 64)
            {
                continue;
            }

            if (!ECXOnlyPubKey.TryCreate(receivedMessage[..32], out var pkey4) || (XPubKey) pkey4 != P4)
            {
                continue;
            }

            if (ECXOnlyPubKey.TryCreate(receivedMessage[32..], out var pkey5))
            {
                P5 = pkey5;
                _logger.LogInformation($"Received cred ack {Payment.Id}");
                return;
            }
        }
    }

    public async Task WaitForReadyToSignal(CancellationToken cancellationToken)
    {
        if (P5 is null)
        {
            return;
        }

        _logger.LogInformation($"Waiting for ready to sign {Payment.Id}");
        await foreach (var receivedMessage in _communicationApi.Messages(cancellationToken))
        {
            if (receivedMessage.Length != 64)
            {
                continue;
            }

            if (!ECXOnlyPubKey.TryCreate(receivedMessage[..32], out var p5) || P5 != p5)
            {
                continue;
            }

            _logger.LogInformation($"Received ready {Payment.Id}");
            if (ECXOnlyPubKey.TryCreate(receivedMessage[32..], out var pkey6))
            {
                P6 = pkey6;
                return;
            }
        }
    }

    public async Task WaitUntilOkToSign(CancellationToken cancellationToken)
    {
        await TxIdSet.Task.WithCancellation(cancellationToken);
        if (P6 is null)
        {
            return;
        }

        if (TxId is null)
        {
            return;
        }

        _logger.LogInformation($"Sender:waiting for proof {Payment.Id}");

        await foreach (var receivedMessage in _communicationApi.Messages(cancellationToken))
        {
            if (receivedMessage.Length != 96)
            {
                continue;
            }

            if (!ECXOnlyPubKey.TryCreate(receivedMessage[..32], out var pkey6) || pkey6 != P6)
            {
                continue;
            }

            if (SecpSchnorrSignature.TryCreate(receivedMessage[32..], out var sig))
            {
                var proof = new KompaktorOffchainPaymentProof(TxId, Payment.Amount.Satoshi, P1, sig);
                _logger.LogInformation(
                    $"Received proof for payment {Payment.Id} \n {Convert.ToHexString(proof.ProofMessage)}");
                if (proof.Verify())
                {
                    _logger.LogInformation($"Received proof for payment {Payment.Id}");
                    Proof = sig;
                    return;
                }
            }
        }
    }


    private byte[] IntentPay()
    {
        return P1!.ToBytes().Concat(((XPubKey) P2).ToBytes()).ToArray();
    }

    private byte[] Pay()
    {
        return P3.ToBytes().Concat(((XPubKey) P4).ToBytes())
            .Concat(AssignedCredentials.SelectMany(credential => credential.ToBytes())).ToArray();
    }
}