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
    private readonly Func<Credential[], Task> _reissue;
    private readonly Func<Task<bool>> _waitUntilReady;
    private uint256? _txId;
    private BlindedCredential[] _receivedCredentials;

    public InteractivePendingPaymentReceiverFlow(ILogger logger, InteractiveReceiverPendingPayment payment,
        IKompaktorPeerCommunicationApi communicationApi, Func<Credential[], Task> reissue,
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
    private TaskCompletionSource Reissued { get; } = new();

    public KompaktorOffchainPaymentProof Proof { get; set; }


    public BlindedCredential[] ReceivedCredentials
    {
        get => _receivedCredentials;
        set
        {
            _receivedCredentials = value;
            _logger.LogInformation("Reissuing credentials for payment {0}", Payment.Id);
            _ = _reissue(value).ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    Reissued.SetResult();
                }
                else
                {
                    _logger.LogInformation("Reissuing credentials failed for payment {0}", Payment.Id);

                    Reissued.SetException(task.Exception);
                }
            });
        }
    }


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
        P2 = p2;
        P3 = ECPrivKey.Create(RandomNumberGenerator.GetBytes(32));
        _logger.LogInformation(($"P2={P2.ToString()} P3={ P3.ToXPubKey()}"));
        var tcs = new TaskCompletionSource();
        var msgTask = _communicationApi.WaitForMessage(P3.ToXPubKey().ToBytes(), ct, $"creds for payment {Payment.Id}", tcs);
        await _communicationApi.SendMessageAsync(IntentPayAck(), $"intent to pay ack {Payment.Id}");
    await tcs.Task.WithCancellation(ct);
        var msg = await msgTask;
        
        _logger.LogInformation("Received creds for payment {0}", Payment.Id);
        var p4 = msg[32..64].ToXPubKey();
//grab every slice of 105 bytes from the message
        var credentials = new List<Credential>();
        var offset = 64;
        while (offset < msg.Length)
        {
            var credential = CredentialHelper.CredFromBytes(msg[offset..(offset + 105)]);
            credentials.Add(credential);
            offset += 105;
        }

        // check if sum of all credential values is equal to the amount of the payment
        if (credentials.Sum(c => c.Value) < Payment.Amount)
        {
            _logger.LogInformation(
                $"Received creds for payment {Payment.Id} do not match the amount {credentials.Sum(c => c.Value)} not {Payment.Amount} ");
            throw new Exception("Invalid credentials");
        }

        P4 = p4;

        ReceivedCredentials = credentials.Select(credential => new BlindedCredential(credential)).ToArray();


        var sw = Stopwatch.StartNew();
        P5 = ECPrivKey.Create(RandomNumberGenerator.GetBytes(32));
        await _communicationApi.SendMessageAsync(CredReceiveAck(), $"creds ack for payment {Payment.Id}");

        _logger.LogInformation($"Sent creds ack for payment {Payment.Id} (took {sw.ElapsedMilliseconds}ms)");
        await Reissued.Task.WaitAsync(ct);

        _logger.LogInformation($"Reissued creds of payment {Payment.Id} {sw.ElapsedMilliseconds}ms)");
        P6 = ECPrivKey.Create(RandomNumberGenerator.GetBytes(32));

        if (!await _waitUntilReady())
        {
            _logger.LogInformation($"Payment {Payment.Id} was waiting for too long for us to get ready");
            return;
        }


        await _communicationApi.SendMessageAsync(Ready(), $"ready for payment {Payment.Id}");
        await TxIdSet.Task.WaitAsync(ct);
        _logger.LogInformation($"Received txid for payment {Payment.Id}");
        
        if (!await _waitUntilReady())
        {
            _logger.LogInformation($"Payment {Payment.Id} was waiting for too long for us to get ready");
            return;
        }

        var proof = new KompaktorOffchainPaymentProof(TxId, Payment.Amount.Satoshi, P1.ToXPubKey(), null);
        _logger.LogInformation($"Send Proof message: {Convert.ToHexString(proof.ProofMessage)}");
        var sig = ((ECPrivKey) P1).SignBIP340(proof.ProofMessage);
        Proof = proof with {Proof = sig};
        await _communicationApi.SendMessageAsync(ProofMsg(), $"proof for payment {Payment.Id}");
    }
}