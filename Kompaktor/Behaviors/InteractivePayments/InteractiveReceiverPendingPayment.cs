using Kompaktor.Mapper;
using Kompaktor.Models;
using NBitcoin;

namespace Kompaktor.Behaviors.InteractivePayments;

public record InteractiveReceiverPendingPayment(
    string Id,
    Money Amount,
    BitcoinAddress Destination,
    bool Reserved,
    PrivKey? KompaktorKey) : PendingPayment(Id, Amount, Destination, Reserved)
{
}