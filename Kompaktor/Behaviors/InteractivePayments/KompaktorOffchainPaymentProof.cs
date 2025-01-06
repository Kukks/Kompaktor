using Kompaktor.Mapper;
using Kompaktor.Utils;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace Kompaktor.Behaviors.InteractivePayments;

public record KompaktorOffchainPaymentProof(
    uint256 TxId,
    long PaymentAmount,
    XPubKey KompaktorKey,
    SecpSchnorrSignature Proof)
{
    public byte[] ProofMessage =>
        System.Security.Cryptography.SHA256.HashData(TxId.ToBytes().Concat(PaymentAmount.ToBytes()).ToArray());
    
    public bool Verify() => KompaktorKey.Key.SigVerifyBIP340(Proof, ProofMessage);
    
    override public string ToString() => $"TxId: {TxId}, PaymentAmount: {PaymentAmount}, KompaktorKey: {KompaktorKey}, Proof: {Proof}";
}