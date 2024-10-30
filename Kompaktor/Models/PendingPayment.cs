using Kompaktor.Utils;
using NBitcoin;
using NBitcoin.Secp256k1;
using WabiSabi.Crypto.ZeroKnowledge;

namespace Kompaktor.Models;


//interactive flow for sending a pending payment
// step1: get a bip21 containing an address, amount and a pubkey (P1)
// step2: generate a private key (P2)
// step3: Send a message signalling to P1 the public key of P2
// if the payment 
// step4: P1 sends a message to P2 with a public key (P3) which acts as an acknowledgement to engage
// step5: generate a private key (P4)
// step6: Get a set of credentials equal to the amount of the payment
// step6: Send a message signalling to P3 the public key of P4 and the credentials
// step7: P3 sends a message to P4 with a public key (P5) which acts as an acknowledgement of accepting the credentials
// step8: P5 sends a message to P5 with the transaction id and a proof of the transaction




public class InteractivePendingPaymentSenderFlow
{
    private readonly PendingPayment _payment;

    public InteractivePendingPaymentSenderFlow(PendingPayment payment)
    {
        _payment = payment;
    }

    public ECXOnlyPubKey P1 { get; set; }
    public ECPrivKey P2 { get; set; }
    public ECXOnlyPubKey P3 { get; set; }
    public ECPrivKey P4 { get; set; }
    public ECXOnlyPubKey P5 { get; set; }
    public uint256 TxId { get; set; }
    public SecpSchnorrSignature Proof { get; set; }
    public byte[] ProofMessage => TxId.ToBytes().Concat(_payment.TxOut().ToBytes()).ToArray();
    
    
}




public record InteractivePendingPayment(
    string Id,
    Money Amount,
    BitcoinAddress Destination,
    bool Reserved, 
    ECXOnlyPubKey KompaktorPubKey,
    bool Urgent) : PendingPayment(Id, Amount, Destination, Reserved)
{
    
    public byte[] IntentPay(ECXOnlyPubKey pkey2)
    {
        return KompaktorPubKey.ToBytes().Concat(pkey2.ToBytes()).ToArray();
    }

    public byte[] IntentPayAck(ECXOnlyPubKey pkey2, ECXOnlyPubKey pkey3)
    {
        return pkey2.ToBytes().Concat(pkey3.ToBytes()).ToArray();
    }
    
    public byte[] Pay(ECXOnlyPubKey pkey3, ECXOnlyPubKey pkey4, Credential[] credentials)
    {
        return pkey3.ToBytes().Concat(pkey4.ToBytes()).Concat(credentials.SelectMany(credential => credential.ToBytes())).ToArray();
    }
    
    public byte[] PayAck(ECXOnlyPubKey pkey4, ECXOnlyPubKey pkey5)
    {
        return pkey4.ToBytes().Concat(pkey5.ToBytes()).ToArray();
        
    }
    
    public byte[] Ready(ECXOnlyPubKey pkey5, uint256 txId)
    {
        return pkey5.ToBytes().Concat(KompaktorPubKey.ToBytes()).Concat(txId.ToBytes()).Concat(TxOut().ToBytes()).ToArray();
    }
}

public record PendingPayment(string Id, Money Amount, BitcoinAddress Destination, bool Reserved)
{
    public TxOut TxOut() => new(Amount, Destination);


    protected virtual Type EqualityContract => typeof(PendingPaymentComparer);

    class PendingPaymentComparer : EqualityComparer<PendingPayment>
    {
        public override bool Equals(PendingPayment? x, PendingPayment? y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;
            return x.GetHashCode() == y.GetHashCode();
        }

        public override int GetHashCode(PendingPayment obj)
        {
            return obj.Id.GetHashCode() ^ obj.Amount.GetHashCode() ^ obj.Destination.GetHashCode() ^
                   obj.Reserved.GetHashCode();
        }
    }
}