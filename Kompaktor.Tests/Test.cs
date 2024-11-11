using System.Collections.Concurrent;
using System.Net;
using Kompaktor.Behaviors;
using Kompaktor.Behaviors.InteractivePayments;
using Kompaktor.Credentials;
using Kompaktor.Models;
using Kompaktor.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.RPC;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Randomness;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Kompaktor.Tests;

public class Test
{
    private readonly LoggerFactory _loggerFactory;

    public Test(ITestOutputHelper outputHelper)
    {
        _loggerFactory = new LoggerFactory();
        _loggerFactory.AddXUnit(outputHelper);
        _loggerFactory.AddProvider(new ConsoleLoggerProvider(new OptionsMonitor<ConsoleLoggerOptions>(
            new OptionsFactory<ConsoleLoggerOptions>(Array.Empty<IConfigureOptions<ConsoleLoggerOptions>>(),
                Array.Empty<IPostConfigureOptions<ConsoleLoggerOptions>>()),
            new List<IOptionsChangeTokenSource<ConsoleLoggerOptions>>(), new OptionsCache<ConsoleLoggerOptions>())));
        Network = Network.RegTest;
        RPC = new RPCClient(new RPCCredentialString()
        {
            UserPassword = new NetworkCredential("ceiwHEbqWI83", "DwubwWsoo3"),
            Server = "http://localhost:53782"
        }, Network);
        while (RPC.GetBalance().ToUnit(MoneyUnit.BTC) < 1)
        {
            RPC.Generate(5);
        }
    }

    public Network Network { get; }

    public RPCClient RPC { get; }


    [Fact]
    public async Task TestWalletsWork()
    {
        var wallet = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
            _loggerFactory.CreateLogger<Wallet>());

        Assert.Empty(wallet.GetTransactions());
        Assert.Empty(await wallet.GetCoins());
        var address = wallet.GetAddress();
        Assert.NotNull(address);
        var tx = await CashCow(RPC, address, Money.Coins(1), new[] {wallet});
        Assert.Equal(tx.GetHash(), Assert.Single(wallet.GetTransactions()).GetHash());
        var coin = Assert.Single(await wallet.GetCoins());
        Assert.Equal(Money.Coins(1), coin.Amount);
        Assert.Equal(address, coin.ScriptPubKey.GetDestinationAddress(Network));
        var key = wallet.GetKeyForCoin(coin);
        Assert.NotNull(key);
        Assert.Equal(key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network), address);

        var proof = await wallet.GenerateOwnershipProof("test", new[] {coin});
        Assert.NotNull(proof);
        Assert.True(coin.ScriptPubKey.GetDestinationAddress(Network).VerifyBIP322("test", proof, new[] {coin}));

        var nodeAddress = await RPC.GetNewAddressAsync();
        await wallet.SchedulePayment(nodeAddress, Money.Coins(0.1m));
        var pendingPayment = Assert.Single(await wallet.GetOutboundPendingPayments(false));
        Assert.Equal(Money.Coins(0.1m), pendingPayment.Amount);
        Assert.Equal(nodeAddress, pendingPayment.Destination);

        Assert.True(await wallet.Commit(pendingPayment.Id));
        Assert.False(await wallet.Commit(pendingPayment.Id));
        await wallet.BreakCommitment(pendingPayment.Id);

        Assert.True(await wallet.Commit(pendingPayment.Id));
        Assert.False(await wallet.Commit(pendingPayment.Id));

        Assert.Empty(await wallet.GetOutboundPendingPayments(false));
        Assert.Single(await wallet.GetOutboundPendingPayments(true));

        var txToPay = Network.CreateTransactionBuilder()
            .AddCoins(await wallet.GetCoins())
            .AddKeys((await wallet.GetCoins()).Select(wallet.GetKeyForCoin).ToArray())
            .Send(nodeAddress, Money.Coins(0.1m))
            .SendEstimatedFees(new FeeRate(1m))
            .SetChange(address)
            .SendAllRemainingToChange()
            .BuildTransaction(false);

        var witness = await wallet.GenerateWitness(coin, txToPay, await wallet.GetCoins());
        Assert.NotNull(witness);
        txToPay.Inputs[0].WitScript = witness;
        await RPC.SendRawTransactionAsync(txToPay);
        wallet.AddTransaction(txToPay);
        Assert.Empty(await wallet.GetOutboundPendingPayments(true));
        Assert.Empty(await wallet.GetOutboundPendingPayments(false));
        var txs = wallet.GetTransactions();
        Assert.Contains(txToPay.GetHash(), txs.Select(t => t.GetHash()));
        Assert.Single(await wallet.GetCoins());
        Assert.Equal(Money.Coins(0.9m) - txToPay.GetFee([coin]), Assert.Single(await wallet.GetCoins()).Amount);
    }

    [Fact]
    public async Task CanCreateRoundAndFail()
    {
        using var roundOperator = new KompaktorRoundOperator(Network, RPC, SecureRandom.Instance,
            _loggerFactory.CreateLogger<KompaktorRoundOperator>());
        ConcurrentBag<KompaktorRoundEvent> roundEvents = new();

        void NewEvent(object? sender, KompaktorRoundEvent e)
        {
            roundEvents.Add(e);
        }

        roundOperator.NewEvent += NewEvent;

        Dictionary<CredentialType, CredentialIssuer> issuers = new()
        {
            {
                CredentialType.Amount, CredentialType.Amount.CredentialIssuer(SecureRandom.Instance)
            }
        };


        roundOperator.Start(new KompaktorRoundEventCreated(
            Guid.NewGuid().ToString(),
            new FeeRate(1m),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            new IntRange(1, 5),
            new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
            new IntRange(1, 100),
            new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
            issuers.ToDictionary(pair => pair.Key, pair => pair.Key.CredentialConfiguration(pair.Value))),
        issuers);

        Eventually(() =>
            Assert.IsType<KompaktorRoundEventCreated>(Assert.Single(roundEvents)));
        Eventually(() =>
        {
            Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.Failed}));
        });
    }
    
    

    [Fact]
    public async Task CanUseRounds()
    {
        List<Wallet> wallets = new();


        var wallet = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
            _loggerFactory.CreateLogger<Wallet>());
        var wallet2 = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
            _loggerFactory.CreateLogger<Wallet>());
        wallets.Add(wallet);
        wallets.Add(wallet2);
        await CashCow(RPC, wallet.GetAddress(), Money.Coins(1), wallets);
        await CashCow(RPC, wallet.GetAddress(), Money.Coins(0.1m), wallets);


        await RPC.GenerateAsync(1);

        using (var roundOperator = new KompaktorRoundOperator(Network, RPC, SecureRandom.Instance,
                   _loggerFactory.CreateLogger<KompaktorRoundOperator>()))
        {
            ConcurrentBag<KompaktorRoundEvent> roundEvents = new();

            void NewEvent(object? sender, KompaktorRoundEvent e)
            {
                roundEvents.Add(e);
            }

            roundOperator.NewEvent += NewEvent;

            Dictionary<CredentialType, CredentialIssuer> issuers = new()
            {
                {
                    CredentialType.Amount, new CredentialIssuer(new CredentialIssuerSecretKey(SecureRandom.Instance),
                        SecureRandom.Instance, Money.Coins(1000m).Satoshi)
                }
            };

            roundOperator.Start(new KompaktorRoundEventCreated(
                    Guid.NewGuid().ToString(),
                    new FeeRate(2m),
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(30),
                    new IntRange(1, 5),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    new IntRange(1, 100),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    
                    issuers.ToDictionary(pair => pair.Key, pair => pair.Key.CredentialConfiguration(pair.Value))),
                issuers);

            Eventually(() =>
                Assert.IsType<KompaktorRoundEventCreated>(Assert.Single(roundEvents)));

            var kompaktorRoundApiFactory = new LocalKompaktorRoundApiFactory(roundOperator);
            var kompaktorRound = (KompaktorRound) roundOperator;
            var traits = new List<KompaktorClientBaseBehaviorTrait>
            {
                new ConsolidationBehaviorTrait(),
                new SelfSendChangeBehaviorTrait(
                    () => wallet.GetAddress().ScriptPubKey,
                    TimeSpan.FromMinutes(1)),
            };

            using var coinjoinClient = new KompaktorRoundClient(
                SecureRandom.Instance,
                Network,
                kompaktorRound,
                kompaktorRoundApiFactory,
                traits,
                wallet, _loggerFactory.CreateLogger("Wallet1"));

            await Eventually(async () =>
            {
                if (coinjoinClient.PhasesTask.IsFaulted || coinjoinClient.PhasesTask.IsCompleted)
                {
                    await coinjoinClient.PhasesTask;
                }

                Assert.Equal(2, roundEvents.Count(@event =>
                    @event is KompaktorRoundEventInputRegistered { }));
                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.OutputRegistration}));
                Assert.Equal(1, roundEvents.Count(@event =>
                    @event is KompaktorRoundEventOutputRegistered { }));

                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.Signing}));

                Assert.Equal(2, roundEvents.Count(@event =>
                    @event is KompaktorRoundEventSignaturePosted { }));


                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.Broadcasting}));

                var txid = coinjoinClient.Round.GetTransaction(Network).GetHash();
                var operatorTxId = roundOperator.GetTransaction(Network).GetHash();
                Assert.Equal(txid, operatorTxId);
                var tx = await RPC.GetRawTransactionAsync(txid);
                foreach (var wallet in wallets)
                {
                    wallet.AddTransaction(tx);
                }
            }, 60_000);
        }

        await RPC.GenerateAsync(1);


        using (var roundOperator = new KompaktorRoundOperator(Network, RPC, SecureRandom.Instance,
                   _loggerFactory.CreateLogger<KompaktorRoundOperator>()))
        {
            ConcurrentBag<KompaktorRoundEvent> roundEvents = new();

            void NewEvent(object? sender, KompaktorRoundEvent e)
            {
                roundEvents.Add(e);
            }

            roundOperator.NewEvent += NewEvent;

            Dictionary<CredentialType, CredentialIssuer> issuers = new()
            {
                {
                    CredentialType.Amount, CredentialType.Amount.CredentialIssuer(SecureRandom.Instance)
                }
            };

            roundOperator.Start(new KompaktorRoundEventCreated(
                    Guid.NewGuid().ToString(),
                    new FeeRate(2m),
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(30),
                    new IntRange(1, 5),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    new IntRange(1, 100),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    issuers.ToDictionary(pair => pair.Key, pair => pair.Key.CredentialConfiguration(pair.Value))),
                issuers);

            Eventually(() =>
                Assert.IsType<KompaktorRoundEventCreated>(Assert.Single(roundEvents)));

            var kompaktorRoundApiFactory = new LocalKompaktorRoundApiFactory(roundOperator);
            var kompaktorRound = (KompaktorRound) roundOperator;
            var traits = new List<KompaktorClientBaseBehaviorTrait>
            {
                new StaticPaymentBehaviorTrait(wallet),
                new SelfSendChangeBehaviorTrait(
                    () => wallet.GetAddress().ScriptPubKey,
                    TimeSpan.FromMinutes(1)),
            };

            var pr = await wallet2.RequestPayment( Money.Coins(0.1m));
            await wallet.SchedulePayment(pr.Destination, pr.Amount);

            using var coinjoinClient = new KompaktorRoundClient(
                SecureRandom.Instance,
                Network,
                kompaktorRound,
                kompaktorRoundApiFactory,
                traits,
                wallet, _loggerFactory.CreateLogger("Wallet1"));

            await Eventually(async () =>
            {
                if (coinjoinClient.PhasesTask.IsFaulted || coinjoinClient.PhasesTask.IsCompleted)
                {
                    await coinjoinClient.PhasesTask;
                }

                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.Completed}));

                var txid = coinjoinClient.Round.GetTransaction(Network).GetHash();
                var operatorTxId = roundOperator.GetTransaction(Network).GetHash();
                Assert.Equal(txid, operatorTxId);
                var tx = await RPC.GetRawTransactionAsync(txid);
                foreach (var wallet in wallets)
                {
                    wallet.AddTransaction(tx);
                }

                Assert.Empty(await wallet.GetOutboundPendingPayments(true));
                Assert.Empty(await wallet2.GetInboundPendingPayments(true));

                Assert.Equal(0.1m, Assert.Single(await wallet2.GetCoins()).Amount.ToDecimal(MoneyUnit.BTC));
            }, 60_000);
        }
    }

[Fact]
    public async Task CanDoInteractivePayments()
    {
        
         List<Wallet> wallets = new();


        var wallet = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
            _loggerFactory.CreateLogger<Wallet>());
        var wallet2 = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
            _loggerFactory.CreateLogger<Wallet>());
        wallets.Add(wallet);
        wallets.Add(wallet2);
        await CashCow(RPC, wallet.GetAddress(), Money.Coins(1), wallets);
        await CashCow(RPC, wallet.GetAddress(), Money.Coins(0.1m), wallets);


        await RPC.GenerateAsync(1);

        var interactivePayment = await wallet2.RequestPayment(Money.Coins(0.1m));
        await wallet.SchedulePayment(interactivePayment.Destination, interactivePayment.Amount, interactivePayment.KompaktorKey, false);
        
        using (var roundOperator = new KompaktorRoundOperator(Network, RPC, SecureRandom.Instance,
                   _loggerFactory.CreateLogger<KompaktorRoundOperator>()))
        {
            ConcurrentBag<KompaktorRoundEvent> roundEvents = new();

            void NewEvent(object? sender, KompaktorRoundEvent e)
            {
                roundEvents.Add(e);
            }

            roundOperator.NewEvent += NewEvent;

            Dictionary<CredentialType, CredentialIssuer> issuers = new()
            {
                {
                    CredentialType.Amount, new CredentialIssuer(new CredentialIssuerSecretKey(SecureRandom.Instance),
                        SecureRandom.Instance, Money.Coins(1000m).Satoshi)
                }
            };

            roundOperator.Start(new KompaktorRoundEventCreated(
                    Guid.NewGuid().ToString(),
                    new FeeRate(2m),
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(30),
                    new IntRange(1, 5),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    new IntRange(1, 100),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    
                    issuers.ToDictionary(pair => pair.Key, pair => pair.Key.CredentialConfiguration(pair.Value))),
                issuers);

            Eventually(() =>
                Assert.IsType<KompaktorRoundEventCreated>(Assert.Single(roundEvents)));

            var kompaktorRoundApiFactory = new LocalKompaktorRoundApiFactory(roundOperator);
            var messagingApi = new KompaktorMessagingApi(roundOperator, roundOperator);

            using var wallet1CoinjoinClient = new KompaktorRoundClient(
                SecureRandom.Instance,
                Network,
                roundOperator,
                kompaktorRoundApiFactory,
                [
                    new InteractivePaymentSenderBehaviorTrait(wallet, messagingApi),
                    new ConsolidationBehaviorTrait(),
                    new SelfSendChangeBehaviorTrait(
                        () => wallet.GetAddress().ScriptPubKey,
                        TimeSpan.FromMinutes(1))

                ],
                wallet, _loggerFactory.CreateLogger("Wallet1"));
            using var wallet2CoinjoinClient = new KompaktorRoundClient(
                SecureRandom.Instance,
                Network,
                roundOperator,
                kompaktorRoundApiFactory,
                [
                    new InteractivePaymentReceiverBehaviorTrait( wallet2, messagingApi),
                    new ConsolidationBehaviorTrait(),
                    new SelfSendChangeBehaviorTrait(
                        () => wallet2.GetAddress().ScriptPubKey,
                        TimeSpan.FromMinutes(1))

                ],
                wallet2, _loggerFactory.CreateLogger("Wallet2"));

            await Eventually(async () =>
            {
                if (wallet1CoinjoinClient.PhasesTask.IsFaulted || wallet1CoinjoinClient.PhasesTask.IsCompleted)
                {
                    await wallet1CoinjoinClient.PhasesTask;
                }  
                if (wallet2CoinjoinClient.PhasesTask.IsFaulted || wallet2CoinjoinClient.PhasesTask.IsCompleted)
                {
                    await wallet1CoinjoinClient.PhasesTask;
                }

                Assert.Equal(2, roundEvents.Count(@event =>
                    @event is KompaktorRoundEventInputRegistered { }));
                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.OutputRegistration}));
                Assert.Equal(2, roundEvents.Count(@event =>
                    @event is KompaktorRoundEventOutputRegistered { }));

                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.Signing}));

                Assert.Equal(2, roundEvents.Count(@event =>
                    @event is KompaktorRoundEventSignaturePosted { }));


                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.Broadcasting}));

                var txid = wallet1CoinjoinClient.Round.GetTransaction(Network).GetHash();
                var operatorTxId = roundOperator.GetTransaction(Network).GetHash();
                Assert.Equal(txid, operatorTxId);
                var tx = await RPC.GetRawTransactionAsync(txid);
                foreach (var wallet in wallets)
                {
                    wallet.AddTransaction(tx);
                }
            }, 60_000);
        }
        
    }
    
    
    [Fact]
public async Task CanDoInteractivePaymentsAtScale()
{
    List<Wallet> wallets = new();

    // Create receiver wallet
    var receiverWallet = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
        _loggerFactory.CreateLogger<Wallet>());
    wallets.Add(receiverWallet);

    var scale = 10;
    
    // Create 700 sender wallets
    for (int i = 0; i < scale; i++)
    {
        var senderWallet = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
            _loggerFactory.CreateLogger<Wallet>());
        wallets.Add(senderWallet);
    }

    // Fund each sender wallet with 1 BTC
    foreach (var senderWallet in wallets.Skip(1)) // Skip receiver wallet at index 0
    {
        await CashCow(RPC, senderWallet.GetAddress(), Money.Coins(1), wallets);
    }

    await RPC.GenerateAsync(1);

    // Receiver requests payments
    List<InteractiveReceiverPendingPayment> interactivePayments = new();

    for (int i = 0; i < scale; i++)
    {
        var interactivePayment = await receiverWallet.RequestPayment(Money.Coins(0.1m));
        interactivePayments.Add(interactivePayment);
    }

    // Each sender schedules the payment
    int index = 0;
    foreach (var senderWallet in wallets.Skip(1)) // Skip receiver wallet at index 0
    {
        var interactivePayment = interactivePayments[index];
        await senderWallet.SchedulePayment(interactivePayment.Destination, interactivePayment.Amount, interactivePayment.KompaktorKey, false, interactivePayment.Id);
        index++;
    }

    using (var roundOperator = new KompaktorRoundOperator(Network, RPC, SecureRandom.Instance,
               _loggerFactory.CreateLogger<KompaktorRoundOperator>()))
    {
        ConcurrentBag<KompaktorRoundEvent> roundEvents = new();

        void NewEvent(object? sender, KompaktorRoundEvent e)
        {
            roundEvents.Add(e);
        }

        roundOperator.NewEvent += NewEvent;

        Dictionary<CredentialType, CredentialIssuer> issuers = new()
        {
            {
                CredentialType.Amount, new CredentialIssuer(new CredentialIssuerSecretKey(SecureRandom.Instance),
                    SecureRandom.Instance, Money.Coins(1000m).Satoshi)
            }
        };

        // Adjust the parameters to accommodate 700 participants
        roundOperator.Start(new KompaktorRoundEventCreated(
                Guid.NewGuid().ToString(),
                new FeeRate(2m),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30),
                new IntRange(1, 300), // Adjusted for the number of participants
                new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                new IntRange(1, 300), // Adjusted for the number of participants
                new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),

                issuers.ToDictionary(pair => pair.Key, pair => pair.Key.CredentialConfiguration(pair.Value))),
            issuers);

        Eventually(() =>
            Assert.IsType<KompaktorRoundEventCreated>(Assert.Single(roundEvents)));

        var kompaktorRoundApiFactory = new LocalKompaktorRoundApiFactory(roundOperator);
        var messagingApi = new KompaktorMessagingApi(roundOperator, roundOperator);

        // Create a KompaktorRoundClient for the receiver
        using var receiverCoinjoinClient = new KompaktorRoundClient(
            SecureRandom.Instance,
            Network,
            roundOperator,
            kompaktorRoundApiFactory,
            [
                new InteractivePaymentReceiverBehaviorTrait(receiverWallet, messagingApi),
                new ConsolidationBehaviorTrait(),
                new SelfSendChangeBehaviorTrait(() => receiverWallet.GetAddress().ScriptPubKey, TimeSpan.FromSeconds(10))
            ],
            receiverWallet, _loggerFactory.CreateLogger("ReceiverWallet"));

        // Create KompaktorRoundClients for each sender
        var senderClients = new List<KompaktorRoundClient>();

        var i = 0;
        foreach (var senderWallet in wallets.Skip(1)) // Skip receiver wallet at index 0
        {
            var senderClient = new KompaktorRoundClient(
                SecureRandom.Instance,
                Network,
                roundOperator,
                kompaktorRoundApiFactory,
                [
                    new InteractivePaymentSenderBehaviorTrait(senderWallet, messagingApi),
                    new ConsolidationBehaviorTrait(),
                    new SelfSendChangeBehaviorTrait(() => senderWallet.GetAddress().ScriptPubKey, TimeSpan.FromSeconds(10))
                ],
                senderWallet, _loggerFactory.CreateLogger($"SenderWallet_{i}"));

            senderClients.Add(senderClient);
            i++;
        }

        // Wait for the coinjoin process to complete
        await Eventually(async () =>
        {
            // Check if any client has faulted
            foreach (var client in senderClients)
            {
                if (client.PhasesTask.IsFaulted || client.PhasesTask.IsCompleted)
                {
                    await client.PhasesTask; // This will throw if the task is faulted
                }
            }

            if (receiverCoinjoinClient.PhasesTask.IsFaulted || receiverCoinjoinClient.PhasesTask.IsCompleted)
            {
                await receiverCoinjoinClient.PhasesTask;
            }

            // Verify that all inputs and outputs are registered
            Assert.Equal(scale+1, roundEvents.Count(@event => @event is KompaktorRoundEventInputRegistered));
            Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                @event is KompaktorRoundEventStatusUpdate { Status: KompaktorStatus.OutputRegistration }));
            Assert.Equal(scale+1, roundEvents.Count(@event => @event is KompaktorRoundEventOutputRegistered));

            Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                @event is KompaktorRoundEventStatusUpdate { Status: KompaktorStatus.Signing }));

            Assert.Equal(scale+1, roundEvents.Count(@event => @event is KompaktorRoundEventSignaturePosted));

            Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                @event is KompaktorRoundEventStatusUpdate { Status: KompaktorStatus.Broadcasting }));

            var txid = receiverCoinjoinClient.Round.GetTransaction(Network).GetHash();
            var operatorTxId = roundOperator.GetTransaction(Network).GetHash();
            Assert.Equal(txid, operatorTxId);
            var tx = await RPC.GetRawTransactionAsync(txid);
            foreach (var wallet in wallets)
            {
                wallet.AddTransaction(tx);
            }
        }, 120_000); // Increase timeout if necessary
    }
}


    private async Task<Transaction> CashCow(RPCClient rpcClient, BitcoinAddress address, Money amount,
        IEnumerable<Wallet> wallets)
    {
        var attempt = false;
        retry:
        try
        {

            var tx = await rpcClient.SendToAddressAsync(address, amount);
            var result = await rpcClient.GetRawTransactionAsync(tx);
            foreach (var wallet in wallets)
            {
                wallet.AddTransaction(result);
            }

            return result;
        }
        catch (Exception e)
        {
            if (!attempt)
            {
                attempt = true;
                await rpcClient.GenerateAsync(1);
                goto retry;
            }

            throw;
        }

    }

    internal void Eventually(Action act, int ms = 20_000)
    {
        var cts = new CancellationTokenSource(ms);
        while (true)
            try
            {
                act();
                break;
            }
            catch (XunitException) when (!cts.Token.IsCancellationRequested)
            {
                cts.Token.WaitHandle.WaitOne(500);
            }
    }

    internal async Task Eventually(Func<Task> act, int ms = 20_000)
    {
        var cts = new CancellationTokenSource(ms);
        while (true)
            try
            {
                await act();
                break;
            }
            catch (XunitException e) when (!cts.Token.IsCancellationRequested)
            {
                checkCancellation:
                if (cts.Token.IsCancellationRequested)
                    throw;

                try
                {
                    await Task.Delay(500, cts.Token);
                }
                catch (TaskCanceledException)
                {
                    goto checkCancellation;
                }
            }
    }


    [Fact]
    public void DependencyGraph()
    {
        var inputRange = new IntRange(2, 2);
        var outputRange = new IntRange(2, 2);


        var ins = new long[] {5, 8, 65, 4, 6, 8, 9}
            .SelectMany(l => new[] {l}
                .Concat(Enumerable.Repeat((long) 0, outputRange.Max - 1)).ToArray()).ToArray();

        var result = DependencyGraph2.Compute(_loggerFactory.CreateLogger("dg"), ins,
            new long[] {30, 40, 10, 7, 3, 10, 5}, inputRange, outputRange);

        _loggerFactory.CreateLogger("output")
            .LogInformation($"computed {result.CountDescendants()} actions with {result.GetMaxDepth()} depth ");
        _loggerFactory.CreateLogger("graphviz")
            .LogInformation(result.GenerateGraphviz(new long[] {30, 40, 10, 7, 3, 10, 5}));
        _loggerFactory.CreateLogger("ascii").LogInformation(result.GenerateAscii(new long[] {30, 40, 10, 7, 3, 10, 5}));

        
        // generate random sets of inputs and outputs
        for (int i = 0; i < 10; i++)
        {
            var inputs = Random.Shared.Next(1, 15);
            var outputs = Random.Shared.Next(1, 30);
            var ins2 = Enumerable.Range(0, inputs).Select(_ => Random.Shared.NextInt64(1, 100))
                .SelectMany(l => new[] {l}
                    .Concat(Enumerable.Repeat((long) 0, outputRange.Max - 1)).ToArray()).ToArray();
            //come up with a set of outptus (outputs is size) that are summed to ins2
            var outs2 = new long[outputs];
            var totalInputSum = ins2.Sum();
            for (var j = 0; j < outputs; j++)
            {
                var remaining = totalInputSum - outs2.Sum(); // Remaining sum to distribute
                var remainingOutputsToSet = outputs - j; // Number of outputs left to fill

                // If it's the last output, assign the remaining value to it
                if (remainingOutputsToSet == 1)
                {
                    outs2[j] = remaining;
                }
                else
                {
                    // Randomly allocate a value for this output, ensuring we leave enough for the rest
                    var maxForThisOutput = remaining - (remainingOutputsToSet - 1); // Leave space for others
                    outs2[j] = Random.Shared.NextInt64(1, maxForThisOutput); // Distribute a portion to outs2[j]
                }
            }

            result = DependencyGraph2.Compute(_loggerFactory.CreateLogger("dg"), ins2, outs2, inputRange, outputRange);
            _loggerFactory.CreateLogger("output")
                .LogInformation($"computed {result.CountDescendants()} actions with {result.GetMaxDepth()} depth ");
        }
    }
}