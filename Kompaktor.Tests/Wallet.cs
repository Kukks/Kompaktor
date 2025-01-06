using System.Collections.Concurrent;
using System.Security.Cryptography;
using Kompaktor.Behaviors.InteractivePayments;
using Kompaktor.Contracts;
using Kompaktor.Mapper;
using Kompaktor.Models;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.BIP322;
using NBitcoin.Secp256k1;

namespace Kompaktor.Tests;

public class Wallet : IOutboundPaymentManager, IKompaktorWalletInterface, IInboundPaymentManager
{
    private readonly Network _network;
    internal readonly ILogger _logger;
    public Mnemonic Mnemonic { get; }
    private uint Index { get; set; }
    private Dictionary<BitcoinAddress, uint> Addresses { get; } = new();


    ConcurrentDictionary<string, PendingPayment> PendingOutboundPayments = new();
    ConcurrentDictionary<string, PendingPayment> PendingInboundPayments = new();
    public readonly ConcurrentDictionary<string, KompaktorOffchainPaymentProof> PaymentProofs = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<uint256, Transaction> History { get; } = new();

    public Wallet(Network network, Mnemonic mnemonic, ILogger logger)
    {
        _network = network;
        _logger = logger;
        Mnemonic = mnemonic;
    }


    public BitcoinAddress GetAddress()
    {
        var address = Mnemonic.DeriveExtKey().Derive(new KeyPath(Index)).PrivateKey
            .GetAddress(ScriptPubKeyType.Segwit, _network);
        Addresses.Add(address, (Index));
        Index++;
        return address;
    }

    public List<Transaction> GetTransactions()
    {
        return History.Values.ToList();
    }

    private Coin[] GetAllCreatedCoins()
    {
        return History.Values
            .SelectMany(t => t.Outputs.AsIndexedOutputs().Select(@out => new Coin(@out)))
            .Where(o => Addresses.ContainsKey(o.ScriptPubKey.GetDestinationAddress(_network))).ToArray();
    }

    private Coin[] GetAllSpentCoins()
    {
        var createdCoins = GetAllCreatedCoins();
        return History.Values.SelectMany(t => t.Inputs)
            .Select(i => createdCoins.FirstOrDefault(c => c.Outpoint == i.PrevOut)).Where(coin => coin != null)
            .ToArray()!;
    }

    private Coin[] GetUnspentCoins()
    {
        var createdCoins = GetAllCreatedCoins();
        var spentCoins = GetAllSpentCoins();
        return createdCoins.Where(c => spentCoins.All(coin => coin.Outpoint != c.Outpoint)).ToArray();
    }

    public Key GetKeyForCoin(Coin coin)
    {
        return Mnemonic.DeriveExtKey().Derive(new KeyPath(Addresses[coin.ScriptPubKey.GetDestinationAddress(_network)]))
            .PrivateKey;
    }


    public void AddTransaction(Transaction transaction)
    {
        var matched = //check if ins/outs match to our addresses
            transaction.Outputs.Any(o => Addresses.ContainsKey(o.ScriptPubKey.GetDestinationAddress(_network))) ||
            GetUnspentCoins().Any(c => transaction.Inputs.Any(i => i.PrevOut == c.Outpoint));
        try
        {
            _lock.Wait();
            foreach (var unused in transaction.Outputs.Where(output => PendingOutboundPayments
                         .OrderByDescending(pair => pair.Value.Reserved).Where(pending =>
                             pending.Value.Destination.ScriptPubKey == output.ScriptPubKey &
                             pending.Value.Amount >= output.Value)
                         .Any(pending => PendingOutboundPayments.Remove(pending.Key, out _))))
            {
                matched = true;
            }

            foreach (var unused in transaction.Outputs.Where(output => PendingInboundPayments
                         .OrderByDescending(pair => pair.Value.Reserved).Where(pending =>
                             pending.Value.Destination.ScriptPubKey == output.ScriptPubKey &
                             pending.Value.Amount <= output.Value)
                         .Any(pending => PendingInboundPayments.Remove(pending.Key, out _))))
            {
                matched = true;
            }

            var completedPayments = PaymentProofs.Where(proof => proof.Value.TxId == transaction.GetHash())
                .Select(pair => pair.Key).ToArray();
            if (completedPayments.Any())
            {
                foreach (var key in completedPayments)
                {
                    if (PendingInboundPayments.TryRemove(key, out _))
                    {
                        matched = true;
                    }

                    if (PendingOutboundPayments.TryRemove(key, out _))
                    {
                        matched = true;
                    }

                    ;
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        if (matched)
        {
            History.TryAdd(transaction.GetHash(), transaction);
        }
    }


    public async Task SchedulePayment(BitcoinAddress destination, Money amount)
    {
        var id = Guid.NewGuid().ToString();
        PendingOutboundPayments.TryAdd(id, new PendingPayment(id, amount, destination, false));
    }

    public async Task SchedulePayment(BitcoinAddress destination, Money amount, XPubKey kompaktorKey, bool urgent,
        string id = null)
    {
        id ??= Guid.NewGuid().ToString();
        PendingOutboundPayments.TryAdd(id,
            new InteractivePendingPayment(id, amount, destination, false, kompaktorKey, urgent));
    }

    public async Task<InteractiveReceiverPendingPayment> RequestPayment(Money amount, string id = null)
    {
        id ??= Guid.NewGuid().ToString();
        var key = ECPrivKey.Create(RandomNumberGenerator.GetBytes(32));
        var interactiveReceiverPendingPayment =
            new InteractiveReceiverPendingPayment(id, amount, GetAddress(), false, key);
        PendingInboundPayments.TryAdd(id, interactiveReceiverPendingPayment);
        return interactiveReceiverPendingPayment;
    }


    public async Task<PendingPayment[]> GetOutboundPendingPayments(bool includeReserved)
    {
        try
        {
            await _lock.WaitAsync();

            return PendingOutboundPayments.Values.Where(tuple => includeReserved || !tuple.Reserved).ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }


    public async Task<PendingPayment[]> GetInboundPendingPayments(bool includeReserved)
    {
        try
        {
            await _lock.WaitAsync();

            return PendingInboundPayments.Values.Where(tuple => includeReserved || !tuple.Reserved).ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> Commit(string pendingPaymentId)
    {
        try
        {
            await _lock.WaitAsync();

            if (PendingInboundPayments.TryGetValue(pendingPaymentId, out var p1))
            {
                return !p1.Reserved &&
                       PendingInboundPayments.TryUpdate(pendingPaymentId, p1 with {Reserved = true}, p1);
            }
            else if (PendingOutboundPayments.TryGetValue(pendingPaymentId, out var p2))
            {
                return !p2.Reserved &&
                       PendingOutboundPayments.TryUpdate(pendingPaymentId, p2 with {Reserved = true}, p2);
            }

            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task BreakCommitment(string pendingPaymentId)
    {
        try
        {
            await _lock.WaitAsync();
            if (PendingOutboundPayments.TryGetValue(pendingPaymentId, out var pending))
            {
                PendingOutboundPayments.TryUpdate(pendingPaymentId, pending with {Reserved = false}, pending);
            }
        }
        finally
        {
            _lock.Release();
        }
    }


    public async Task AddProof(string pendingPaymentId, KompaktorOffchainPaymentProof proof)
    {
        
        _logger.LogInformation("Added proof: {0}", proof);
        if(proof == null) throw new ArgumentNullException(nameof(proof));
        PaymentProofs.TryAdd(pendingPaymentId, proof);
    }

    public async Task<Coin[]> GetCoins()
    {
        return GetUnspentCoins();
    }

    public async Task<BIP322Signature.Full> GenerateOwnershipProof(string message, Coin[] coins)
    {
        if (coins == null) throw new ArgumentNullException(nameof(coins));
        var addressToSignWith = coins.First().ScriptPubKey.GetDestinationAddress(_network);
        var psbt = addressToSignWith.CreateBIP322PSBT(message, fundProofOutputs: coins);
        psbt = psbt.AddCoins(coins);
        psbt = psbt.SignWithKeys(coins.Select(GetKeyForCoin).ToArray());
        return (BIP322Signature.Full) BIP322Signature.FromPSBT(psbt, SignatureType.Full);
    }

    public async Task<WitScript> GenerateWitness(Coin coin, Transaction tx, IEnumerable<Coin> txCoins)

    {
        tx = tx.Clone();
        var txBuilder = _network.CreateTransactionBuilder();
        txBuilder.AddCoins(coin);
        txBuilder.AddCoins(txCoins);
        txBuilder.AddKeys(GetKeyForCoin(coin));

        txBuilder.SignTransactionInPlace(tx);


        var input = tx.Inputs.FindIndexedInput(coin.Outpoint);

        _logger.LogDebug($"client:Signing input {input?.Index} of {coin.Outpoint} for id: {tx?.GetHash()}");
        return input.WitScript;
    }
}