using System.Diagnostics;
using System.Security.Cryptography;
using Kompaktor.Contracts;
using Kompaktor.Mapper;
using Kompaktor.Utils;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Secp256k1;
using WabiSabi.Crypto.ZeroKnowledge;

namespace Kompaktor.Behaviors.InteractivePayments;

public class InteractivePendingPaymentReceiverFlow
{
    private readonly ILogger _logger;
    public readonly InteractiveReceiverPendingPayment Payment;
    private readonly IKompaktorPeerCommunicationApi _communicationApi;
    private readonly Func<BlindedCredential[], Task> _reissue;
    private readonly Func<Task<bool>> _waitUntilReady;
    private uint256? _txId;

    public InteractivePendingPaymentReceiverFlow(ILogger logger, InteractiveReceiverPendingPayment payment,
        IKompaktorPeerCommunicationApi communicationApi, Func<BlindedCredential[], Task> reissue,
        Func<Task<bool>> waitUntilReady)
    {
        _logger = logger;
        Payment = payment;
        _communicationApi = communicationApi;
        _reissue = reissue;
        _waitUntilReady = waitUntilReady;
    }

    public PrivKey? P1 => Payment.KompaktorKey;

    public XPubKey? P2 { get; set; }
    public PrivKey? P3 { get; set; }
    public XPubKey? P4 { get; set; }
    public PrivKey? P5 { get; set; }
    public PrivKey? P6 { get; set; }

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

    public KompaktorOffchainPaymentProof? Proof { get; private set; }


    public BlindedCredential[] ReceivedCredentials { get; set; }


    private byte[] IntentPayAck()
    {
        return P2.ToBytes().Concat(P3.ToXPubKey().ToBytes()).ToArray();
    }

    private byte[] CredReceiveAck()
    {
        return P4.ToBytes().Concat(P5.ToXPubKey().ToBytes()).ToArray();
    }

    private byte[] Ready()
    {
        return P5.ToXPubKey().ToBytes().Concat(P6.ToXPubKey().ToBytes()).ToArray();
    }

    private byte[] ProofMsg()
    {
        _logger.LogInformation($"Send Proof message: {Convert.ToHexString(Proof.ProofMessage)}");
        return P6.ToXPubKey().ToBytes().Concat(Proof.Proof.ToBytes()).ToArray();
    }


    public async Task Start(XPubKey p2, CancellationToken ct)
    {
        try
        {
            P2 = p2;
            P3 = ECPrivKey.Create(RandomNumberGenerator.GetBytes(32));

            var msg = await _communicationApi
                .SendAndWaitForMessageAsync(IntentPayAck(), P3.ToXPubKey().ToBytes(), ct,
                    $"creds for payment {Payment.Id}");

            _logger.LogInformation("Received creds for payment {0}", Payment.Id);

            // Validate message has minimum expected length (32 bytes P4 key + at least one credential)
            if (msg.Length < 64 + 105)
            {
                _logger.LogWarning("Payment {Id}: message too short ({Len} bytes), expected at least {Min}",
                    Payment.Id, msg.Length, 64 + 105);
                return;
            }

            // Validate remaining bytes are aligned to credential size
            if ((msg.Length - 64) % 105 != 0)
            {
                _logger.LogWarning("Payment {Id}: message body not aligned to credential size (remainder {Rem})",
                    Payment.Id, (msg.Length - 64) % 105);
                return;
            }

            var p4 = msg[32..64].ToXPubKey();
            var credentials = new List<Credential>();
            var offset = 64;
            while (offset + 105 <= msg.Length)
            {
                var credential = CredentialHelper.CredFromBytes(msg[offset..(offset + 105)]);
                credentials.Add(credential);
                offset += 105;
            }

            if (credentials.Sum(c => c.Value) < Payment.Amount)
            {
                _logger.LogWarning(
                    "Payment {Id}: credential sum {Sum} < required {Amount}",
                    Payment.Id, credentials.Sum(c => c.Value), Payment.Amount);
                return;
            }

            P4 = p4;

            var sw = Stopwatch.StartNew();
            ReceivedCredentials = credentials.Select(credential => new BlindedCredential(credential)).ToArray();

            P5 = ECPrivKey.Create(RandomNumberGenerator.GetBytes(32));
            var credAckTask = _communicationApi.SendMessageAsync(CredReceiveAck(),
                $"creds ack for payment {Payment.Id}");
            var reissueTask = _reissue(ReceivedCredentials);
            _logger.LogInformation("Reissuing credentials for payment {0}", Payment.Id);

            await credAckTask;
            _logger.LogInformation("Sent creds ack for payment {Id} (took {Ms}ms)",
                Payment.Id, sw.ElapsedMilliseconds);
            await reissueTask;

            _logger.LogInformation("Reissued creds of payment {Id} ({Ms}ms)",
                Payment.Id, sw.ElapsedMilliseconds);
            P6 = ECPrivKey.Create(RandomNumberGenerator.GetBytes(32));

            if (!await _waitUntilReady())
            {
                _logger.LogInformation("Payment {Id} timed out waiting to get ready", Payment.Id);
                return;
            }

            await _communicationApi.SendMessageAsync(Ready(), $"ready for payment {Payment.Id}");
            await TxIdSet.Task.WaitAsync(ct);
            _logger.LogInformation("Received txid for payment {Id}", Payment.Id);

            if (!await _waitUntilReady())
            {
                _logger.LogInformation("Payment {Id} timed out waiting to get ready (proof phase)", Payment.Id);
                return;
            }

            var proof = new KompaktorOffchainPaymentProof(TxId, Payment.Amount.Satoshi, P1.ToXPubKey(), null);
            var sig = ((ECPrivKey) P1).SignBIP340(proof.ProofMessage);
            Proof = proof with { Proof = sig };
            await _communicationApi.SendMessageAsync(ProofMsg(), $"proof for payment {Payment.Id}");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Payment {Id} receiver flow cancelled", Payment.Id);
        }
        catch (Exception ex)
        {
            _logger.LogException($"Payment {Payment.Id}: receiver flow failed", ex);
        }
    }
}