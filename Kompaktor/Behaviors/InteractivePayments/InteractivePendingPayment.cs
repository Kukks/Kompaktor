using Kompaktor.Mapper;
using Kompaktor.Models;
using NBitcoin;

namespace Kompaktor.Behaviors.InteractivePayments;

public record InteractivePendingPayment(
    string Id,
    Money Amount,
    BitcoinAddress Destination,
    bool Reserved,
    XPubKey? KompaktorPubKey,
    bool Urgent) : PendingPayment(Id, Amount, Destination, Reserved)
{
}public record InteractiveReceiverPendingPayment(
    string Id,
    Money Amount,
    BitcoinAddress Destination,
    bool Reserved,
    PrivKey? KompaktorKey) : PendingPayment(Id, Amount, Destination, Reserved)
{
}