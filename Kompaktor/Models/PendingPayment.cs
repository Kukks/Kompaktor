using NBitcoin;

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