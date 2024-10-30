using System.Collections.Concurrent;
using Kompaktor.Contracts;
using Kompaktor.Models;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.BIP322;

namespace Kompaktor.Tests;

public class Wallet : IOutboundPaymentManager, IKompaktorWalletInterface, IInboundPaymentManager
{
    private readonly Network _network;
    private readonly ILogger _logger;
    public Mnemonic Mnemonic { get; }
    private uint Index { get; set; }
    private Dictionary<BitcoinAddress, uint> Addresses { get; } = new();


    ConcurrentDictionary<string, PendingPayment> PendingOutboundPayments = new();
    ConcurrentDictionary<string, PendingPayment> PendingInboundPayments = new();
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
            foreach (var output in transaction.Outputs.Where(output => PendingOutboundPayments
                         .OrderBy(pair => pair.Value.Reserved).Where(pending =>
                             pending.Value.Destination.ScriptPubKey == output.ScriptPubKey &
                             pending.Value.Amount == output.Value)
                         .Any(pending => PendingOutboundPayments.Remove(pending.Key, out _))))
            {
                matched = true;
            }
            
            foreach (var input in transaction.Outputs.Where(output => PendingInboundPayments
                         .OrderBy(pair => pair.Value.Reserved).Where(pending =>
                             pending.Value.Destination.ScriptPubKey == output.ScriptPubKey &
                             pending.Value.Amount == output.Value)
                         .Any(pending => PendingInboundPayments.Remove(pending.Key, out _))))
            {
                matched = true;
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

            return PendingOutboundPayments.TryGetValue(pendingPaymentId, out var pending) && !pending.Reserved &&
                   PendingOutboundPayments.TryUpdate(pendingPaymentId, pending with {Reserved = true}, pending);
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