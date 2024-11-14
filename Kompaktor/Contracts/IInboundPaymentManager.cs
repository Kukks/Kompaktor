using Kompaktor.Behaviors.InteractivePayments;
using Kompaktor.Models;

namespace Kompaktor.Contracts;

public interface IInboundPaymentManager
{
    Task<PendingPayment[]> GetInboundPendingPayments(bool includeReserved);
    
    Task<bool> Commit(string pendingPaymentId);
    Task BreakCommitment(string pendingPaymentId);
    
    Task AddProof(string pendingPaymentId, KompaktorOffchainPaymentProof proof);
}