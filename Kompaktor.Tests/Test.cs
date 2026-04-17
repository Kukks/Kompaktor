using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
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
using WabiSabi.Crypto.ZeroKnowledge;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Kompaktor.Tests;

[Trait("Category", "Integration")]
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
        for (int attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if (RPC.GetBalance().ToUnit(MoneyUnit.BTC) >= 1)
                    break;
                RPC.Generate(5);
            }
            catch (HttpRequestException)
            {
                Thread.Sleep(2000);
            }
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

        roundOperator.NewEvent += (sender, args) =>
        {
            roundEvents.Add(args);
            return Task.CompletedTask;
        };

        Dictionary<CredentialType, ICredentialIssuer> issuers = new()
        {
            {
                CredentialType.Amount, CredentialType.Amount.CredentialIssuer(SecureRandom.Instance)
            }
        };


        await roundOperator.Start(new KompaktorRoundEventCreated(
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
        var participantCount = 100;
        List<Wallet> wallets = new();

        // Create participant wallets + 1 payment receiver
        var paymentReceiver = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
            _loggerFactory.CreateLogger<Wallet>());
        wallets.Add(paymentReceiver);

        for (int w = 0; w < participantCount; w++)
        {
            var participantWallet = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
                _loggerFactory.CreateLogger<Wallet>());
            wallets.Add(participantWallet);
        }

        // Fund each participant with 2 coins (to test consolidation with many inputs)
        foreach (var participantWallet in wallets.Skip(1))
        {
            await CashCow(RPC, participantWallet.GetAddress(), Money.Coins(1), wallets);
            await CashCow(RPC, participantWallet.GetAddress(), Money.Coins(0.5m), wallets);
        }

        await RPC.GenerateAsync(1);

        // Round 1: Multi-wallet consolidation round
        using (var roundOperator = new KompaktorRoundOperator(Network, RPC, SecureRandom.Instance,
                   _loggerFactory.CreateLogger<KompaktorRoundOperator>()))
        {
            ConcurrentBag<KompaktorRoundEvent> roundEvents = new();

            roundOperator.NewEvent += (sender, args) =>
            {
                roundEvents.Add(args);
                return Task.CompletedTask;
            };
            Dictionary<CredentialType, ICredentialIssuer> issuers = new()
            {
                {
                    CredentialType.Amount, new CredentialIssuer(new CredentialIssuerSecretKey(SecureRandom.Instance),
                        SecureRandom.Instance, Money.Coins(100_000m).Satoshi)
                }
            };

            await roundOperator.Start(new KompaktorRoundEventCreated(
                    Guid.NewGuid().ToString(),
                    new FeeRate(2m),
                    TimeSpan.FromSeconds(120),
                    TimeSpan.FromSeconds(120),
                    TimeSpan.FromSeconds(120),
                    new IntRange(1, participantCount * 3),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    new IntRange(1, participantCount * 3),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    issuers.ToDictionary(pair => pair.Key, pair => pair.Key.CredentialConfiguration(pair.Value))),
                issuers);

            Eventually(() =>
                Assert.IsType<KompaktorRoundEventCreated>(Assert.Single(roundEvents)));

            var kompaktorRoundApiFactory = new LocalKompaktorRoundApiFactory(roundOperator);
            var kompaktorRound = (KompaktorRound) roundOperator;

            // Create clients for all participants
            var clients = new List<KompaktorRoundClient>();
            for (int w = 0; w < participantCount; w++)
            {
                var participantWallet = wallets[w + 1]; // Skip paymentReceiver
                var traits = new List<KompaktorClientBaseBehaviorTrait>
                {
                    new ConsolidationBehaviorTrait(),
                    new SelfSendChangeBehaviorTrait(
                        () => participantWallet.GetAddress().ScriptPubKey,
                        TimeSpan.FromSeconds(45)),
                };

                var client = new KompaktorRoundClient(
                    SecureRandom.Instance,
                    Network,
                    kompaktorRound,
                    kompaktorRoundApiFactory,
                    traits,
                    participantWallet, _loggerFactory.CreateLogger($"Wallet_{w}"));
                clients.Add(client);
            }

            await Eventually(async () =>
            {
                foreach (var client in clients)
                {
                    if (client.PhasesTask.IsFaulted || client.PhasesTask.IsCompleted)
                        await client.PhasesTask;
                }

                // Each participant registers 2 inputs
                Assert.Equal(participantCount * 2, roundEvents.Count(@event =>
                    @event is KompaktorRoundEventInputRegistered));
                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.OutputRegistration}));

                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.Signing}));

                Assert.Equal(participantCount * 2, roundEvents.Count(@event =>
                    @event is KompaktorRoundEventSignaturePosted));

                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.Broadcasting}));

                var txid = clients[0].Round.GetTransaction(Network).GetHash();
                var operatorTxId = roundOperator.GetTransaction(Network).GetHash();
                Assert.Equal(txid, operatorTxId);
                var tx = await RPC.GetRawTransactionAsync(txid);
                foreach (var wallet in wallets)
                {
                    wallet.AddTransaction(tx);
                }
            }, 300_000); // 5 minutes for 100 participants

            foreach (var client in clients)
                client.Dispose();
        }

        await RPC.GenerateAsync(1);

        // Round 2: Payment round — first participant pays the receiver
        var wallet = wallets[1]; // First participant
        using (var roundOperator = new KompaktorRoundOperator(Network, RPC, SecureRandom.Instance,
                   _loggerFactory.CreateLogger<KompaktorRoundOperator>()))
        {
            ConcurrentBag<KompaktorRoundEvent> roundEvents = new();

            roundOperator.NewEvent += (sender, args) =>
            {
                roundEvents.Add(args);
                return Task.CompletedTask;
            };
            Dictionary<CredentialType, ICredentialIssuer> issuers = new()
            {
                {
                    CredentialType.Amount, CredentialType.Amount.CredentialIssuer(SecureRandom.Instance)
                }
            };

            await roundOperator.Start(new KompaktorRoundEventCreated(
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

            var pr = await paymentReceiver.RequestPayment(Money.Coins(0.1m));
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
                foreach (var w in wallets)
                {
                    w.AddTransaction(tx);
                }

                Assert.Empty(await wallet.GetOutboundPendingPayments(true));
                Assert.Empty(await paymentReceiver.GetInboundPendingPayments(true));

                Assert.Equal(0.1m, Assert.Single(await paymentReceiver.GetCoins()).Amount.ToDecimal(MoneyUnit.BTC));
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
        await wallet.SchedulePayment(interactivePayment.Destination, interactivePayment.Amount,
            interactivePayment.KompaktorKey.ToXPubKey(), false);

        using (var roundOperator = new KompaktorRoundOperator(Network, RPC, SecureRandom.Instance,
                   _loggerFactory.CreateLogger<KompaktorRoundOperator>()))
        {
            ConcurrentBag<KompaktorRoundEvent> roundEvents = new();

            roundOperator.NewEvent += (sender, args) =>
            {
                roundEvents.Add(args);
                return Task.CompletedTask;
            };
            Dictionary<CredentialType, ICredentialIssuer> issuers = new()
            {
                {
                    CredentialType.Amount, new CredentialIssuer(new CredentialIssuerSecretKey(SecureRandom.Instance),
                        SecureRandom.Instance, Money.Coins(1000m).Satoshi)
                }
            };

            await roundOperator.Start(new KompaktorRoundEventCreated(
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

            var w1Logger = _loggerFactory.CreateLogger("Sender");
            var w2Logger = _loggerFactory.CreateLogger("Receiver");

            using var wallet1CoinjoinClient = new KompaktorRoundClient(
                SecureRandom.Instance,
                Network,
                roundOperator,
                kompaktorRoundApiFactory,
                [
                    new InteractivePaymentSenderBehaviorTrait(wallet,
                        new KompaktorMessagingApi(w1Logger, roundOperator, roundOperator)),
                    new ConsolidationBehaviorTrait(),
                    new SelfSendChangeBehaviorTrait(
                        () => wallet.GetAddress().ScriptPubKey,
                        TimeSpan.FromMinutes(1))
                ],
                wallet, w1Logger);
            using var wallet2CoinjoinClient = new KompaktorRoundClient(
                SecureRandom.Instance,
                Network,
                roundOperator,
                kompaktorRoundApiFactory,
                [
                    new InteractivePaymentReceiverBehaviorTrait(wallet2,
                        new KompaktorMessagingApi(w2Logger, roundOperator, roundOperator)),
                    new ConsolidationBehaviorTrait(),
                    new SelfSendChangeBehaviorTrait(
                        () => wallet2.GetAddress().ScriptPubKey,
                        TimeSpan.FromMinutes(1))
                ],
                wallet2, w2Logger);

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

        Assert.Empty(await wallet.GetOutboundPendingPayments(true));
        Assert.Empty(await wallet2.GetInboundPendingPayments(true));
    }


    [Fact]
    [Trait("Category", "Scale")]
    public async Task CanDoInteractivePaymentsAtScale()
    {
        var phaseLog = _loggerFactory.CreateLogger("PhaseTimer");
        var totalSw = Stopwatch.StartNew();
        var phaseSw = Stopwatch.StartNew();

        List<Wallet> wallets = new();

        // Create receiver wallet
        var receiverWallet = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
            _loggerFactory.CreateLogger<Wallet>());
        wallets.Add(receiverWallet);

        var scale = 100;
        var maxConcurrentFlows = 100;

        // Create sender wallets
        for (int i = 0; i < scale; i++)
        {
            var senderWallet = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
                _loggerFactory.CreateLogger<Wallet>());
            wallets.Add(senderWallet);
        }

        phaseLog.LogInformation("[TIMER] Wallet creation: {Elapsed}", phaseSw.Elapsed);
        phaseSw.Restart();

        // Fund each sender wallet with 1 BTC
        foreach (var senderWallet in wallets.Skip(1)) // Skip receiver wallet at index 0
        {
            await CashCow(RPC, senderWallet.GetAddress(), Money.Coins(1), wallets);
        }

        await RPC.GenerateAsync(1);

        phaseLog.LogInformation("[TIMER] Wallet funding ({Count} wallets): {Elapsed}", scale, phaseSw.Elapsed);
        phaseSw.Restart();

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
            await senderWallet.SchedulePayment(interactivePayment.Destination, interactivePayment.Amount,
                interactivePayment.KompaktorKey.ToXPubKey(), true, interactivePayment.Id);
            index++;
        }

        phaseLog.LogInformation("[TIMER] Payment scheduling ({Count} payments): {Elapsed}", scale, phaseSw.Elapsed);
        phaseSw.Restart();

        using (var roundOperator = new KompaktorRoundOperator(Network, RPC, SecureRandom.Instance,
                   _loggerFactory.CreateLogger<KompaktorRoundOperator>()))
        {
            ConcurrentBag<KompaktorRoundEvent> roundEvents = new();
            var phaseTimestamps = new ConcurrentDictionary<string, TimeSpan>();


            roundOperator.NewEvent += (sender, args) =>
            {
                roundEvents.Add(args);
                if (args is KompaktorRoundEventStatusUpdate statusUpdate)
                {
                    phaseTimestamps[statusUpdate.Status.ToString()] = totalSw.Elapsed;
                    phaseLog.LogInformation("[TIMER] Phase transition → {Phase} at {Elapsed}", statusUpdate.Status, totalSw.Elapsed);
                }
                else if (args is KompaktorRoundEventInputRegistered)
                {
                    var inputCount = roundEvents.Count(e => e is KompaktorRoundEventInputRegistered);
                    if (inputCount == 1)
                        phaseLog.LogInformation("[TIMER] First input registered at {Elapsed}", totalSw.Elapsed);
                    else if (inputCount == scale)
                        phaseLog.LogInformation("[TIMER] Last input registered ({Count}) at {Elapsed}", inputCount, totalSw.Elapsed);
                }
                else if (args is KompaktorRoundEventOutputRegistered)
                {
                    var outputCount = roundEvents.Count(e => e is KompaktorRoundEventOutputRegistered);
                    if (outputCount == 1)
                        phaseLog.LogInformation("[TIMER] First output registered at {Elapsed}", totalSw.Elapsed);
                }
                else if (args is KompaktorRoundEventSignaturePosted)
                {
                    var sigCount = roundEvents.Count(e => e is KompaktorRoundEventSignaturePosted);
                    if (sigCount == 1)
                        phaseLog.LogInformation("[TIMER] First signature posted at {Elapsed}", totalSw.Elapsed);
                    else if (sigCount == scale)
                        phaseLog.LogInformation("[TIMER] Last signature posted ({Count}) at {Elapsed}", sigCount, totalSw.Elapsed);
                }
                return Task.CompletedTask;
            };

            phaseLog.LogInformation("[TIMER] Round setup: {Elapsed}", phaseSw.Elapsed);
            phaseSw.Restart();

            // Use Bulletproofs++ for range proofs: 39% faster than classical sigma at n=100
            var issuerKey = new CredentialIssuerSecretKey(SecureRandom.Instance);
            var rangeProofSystem = new BulletproofPlusPlusRangeProof();
            Dictionary<CredentialType, ICredentialIssuer> issuers = new()
            {
                {
                    CredentialType.Amount, new BulletproofCredentialIssuer(issuerKey, rangeProofSystem,
                        SecureRandom.Instance, Money.Coins(100_000m).Satoshi)
                }
            };

            // Adjust the parameters to accommodate scale participants
            await roundOperator.Start(new KompaktorRoundEventCreated(
                    Guid.NewGuid().ToString(),
                    new FeeRate(2m),
                    TimeSpan.FromMinutes(10),
                    TimeSpan.FromMinutes(10),
                    TimeSpan.FromMinutes(10),
                    new IntRange(scale, scale + 100),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    new IntRange(1, scale * 3),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    issuers.ToDictionary(pair => pair.Key,
                        pair => pair.Key.CredentialConfiguration(pair.Value, useBulletproofs: true)),
                    InputRegistrationSoftTimeout: TimeSpan.FromSeconds(60)),
                issuers);

            Eventually(() =>
                Assert.IsType<KompaktorRoundEventCreated>(Assert.Single(roundEvents)));

            var receiverWalletLogger = _loggerFactory.CreateLogger("ReceiverWallet");
            var kompaktorRoundApiFactory = new LocalKompaktorRoundApiFactory(roundOperator);

            // Create a KompaktorRoundClient for the receiver
            using var receiverCoinjoinClient = new KompaktorRoundClient(
                SecureRandom.Instance,
                Network,
                roundOperator,
                kompaktorRoundApiFactory,
                [
                    new InteractivePaymentReceiverBehaviorTrait(receiverWallet,
                        new KompaktorMessagingApi(receiverWalletLogger, roundOperator, roundOperator), maxConcurrentFlows),
                    new ConsolidationBehaviorTrait(),
                    new SelfSendChangeBehaviorTrait(() => receiverWallet.GetAddress().ScriptPubKey,
                        TimeSpan.FromSeconds(90))
                ],
                receiverWallet, receiverWalletLogger);

            // Create KompaktorRoundClients for each sender
            var senderClients = new List<KompaktorRoundClient>();

            var i = 0;
            foreach (var senderWallet in wallets.Skip(1)) // Skip receiver wallet at index 0
            {
                var senderWalletLogger = _loggerFactory.CreateLogger($"SenderWallet_{i}");
                var senderClient = new KompaktorRoundClient(
                    SecureRandom.Instance,
                    Network,
                    roundOperator,
                    kompaktorRoundApiFactory,
                    [
                        new InteractivePaymentSenderBehaviorTrait(senderWallet,
                            new KompaktorMessagingApi(senderWalletLogger, roundOperator, roundOperator)),
                        new ConsolidationBehaviorTrait(),
                        new SelfSendChangeBehaviorTrait(() => senderWallet.GetAddress().ScriptPubKey,
                            TimeSpan.FromSeconds(90))
                    ],
                    senderWallet, senderWalletLogger);

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
                Assert.Equal(scale, roundEvents.Count(@event => @event is KompaktorRoundEventInputRegistered));
                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.OutputRegistration}));
                // At minimum: 1 receiver aggregate output + 1 change per sender.
                // Non-interactive fallbacks produce extra outputs (payment + change instead of aggregated),
                // so actual count will be higher at scale.
                var outputRegistrations = roundEvents.Count(@event => @event is KompaktorRoundEventOutputRegistered);
                Assert.True(outputRegistrations >= 1 + scale,
                    $"Expected at least {1 + scale} output registrations, got {outputRegistrations}");

                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.Signing}));

                Assert.Equal(scale, roundEvents.Count(@event => @event is KompaktorRoundEventSignaturePosted));

                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.Broadcasting}));
                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.Completed}));

                
                var txid = receiverCoinjoinClient.Round.GetTransaction(Network).GetHash();
                var operatorTxId = roundOperator.GetTransaction(Network).GetHash();
                Assert.Equal(txid, operatorTxId);
                var tx = await RPC.GetRawTransactionAsync(txid);
                foreach (var wallet in wallets)
                {
                    wallet.AddTransaction(tx);
                }
                
                foreach (var wallet in wallets)
                {
                    Assert.Empty(await wallet.GetOutboundPendingPayments(true));
                    Assert.Empty(await wallet.GetInboundPendingPayments(true));
                }

                wallets[0]._logger.LogInformation(tx.ToHex());

            }, 900_000); // 15 minutes target with BP++ range proofs (10min input + output/signing with 5min buffer)

            totalSw.Stop();
            var outputCount = roundEvents.Count(e => e is KompaktorRoundEventOutputRegistered);
            var summary = new StringBuilder();
            summary.AppendLine();
            summary.AppendLine("╔══════════════════════════════════════════════════════════════╗");
            summary.AppendLine("║              SCALE TEST TIMING BREAKDOWN                    ║");
            summary.AppendLine("╠══════════════════════════════════════════════════════════════╣");

            // Compute per-phase durations from timestamps
            var phases = new[] { "InputRegistration", "OutputRegistration", "Signing", "Broadcasting", "Completed" };
            TimeSpan prev = TimeSpan.Zero;
            foreach (var phase in phases)
            {
                if (phaseTimestamps.TryGetValue(phase, out var ts))
                {
                    var duration = ts - prev;
                    var pct = totalSw.Elapsed.TotalSeconds > 0 ? duration.TotalSeconds / totalSw.Elapsed.TotalSeconds * 100 : 0;
                    summary.AppendLine($"║  {phase,-25} {duration.TotalSeconds,8:F1}s  ({pct,4:F0}%)          ║");
                    prev = ts;
                }
            }

            summary.AppendLine("╠══════════════════════════════════════════════════════════════╣");
            summary.AppendLine($"║  Total elapsed              {totalSw.Elapsed.TotalSeconds,8:F1}s                       ║");
            summary.AppendLine($"║  Participants               {scale,8}                        ║");
            summary.AppendLine($"║  Inputs registered          {scale,8}                        ║");
            summary.AppendLine($"║  Outputs registered         {outputCount,8}                        ║");
            summary.AppendLine($"║  Signatures posted          {scale,8}                        ║");
            summary.AppendLine("╚══════════════════════════════════════════════════════════════╝");
            phaseLog.LogInformation(summary.ToString());
        }
    }

    /// <summary>
    /// Batched receiver scale test: 5 receivers each collect payments from 20 senders.
    /// Verifies that each receiver consolidates all incoming payments into a single output,
    /// regardless of how many senders paid them. This proves the credential aggregation
    /// architecture works at scale — 100 inputs collapse into ~105 outputs (5 receiver +
    /// 100 sender change), not 200+ (one per payment flow).
    /// </summary>
    [Fact]
    [Trait("Category", "Scale")]
    public async Task CanDoBatchedReceiverPaymentsAtScale()
    {
        var phaseLog = _loggerFactory.CreateLogger("PhaseTimer");
        var totalSw = Stopwatch.StartNew();

        const int receiverCount = 5;
        const int sendersPerReceiver = 20;
        var totalSenders = receiverCount * sendersPerReceiver;

        var allWallets = new List<Wallet>();
        var receiverWallets = new List<Wallet>();
        var senderWallets = new List<Wallet>();

        // Create receiver wallets
        for (int r = 0; r < receiverCount; r++)
        {
            var w = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
                _loggerFactory.CreateLogger($"Receiver_{r}"));
            receiverWallets.Add(w);
            allWallets.Add(w);
        }

        // Create sender wallets
        for (int s = 0; s < totalSenders; s++)
        {
            var w = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
                _loggerFactory.CreateLogger($"Sender_{s}"));
            senderWallets.Add(w);
            allWallets.Add(w);
        }

        // Fund senders only — receivers have no coins, they only receive
        foreach (var sender in senderWallets)
            await CashCow(RPC, sender.GetAddress(), Money.Coins(1), allWallets);
        await RPC.GenerateAsync(1);

        // Each receiver requests payments from their assigned senders
        for (int r = 0; r < receiverCount; r++)
        {
            for (int s = 0; s < sendersPerReceiver; s++)
            {
                var senderIdx = r * sendersPerReceiver + s;
                var payment = await receiverWallets[r].RequestPayment(Money.Coins(0.1m));
                await senderWallets[senderIdx].SchedulePayment(payment.Destination, payment.Amount,
                    payment.KompaktorKey.ToXPubKey(), true, payment.Id);
            }
        }

        // Pre-compute receiver output addresses — used by both SelfSendChangeBehaviorTrait and assertions
        var receiverAddresses = receiverWallets
            .Select(w => w.GetAddress().ScriptPubKey)
            .ToList();

        phaseLog.LogInformation("[TIMER] Setup complete ({Senders} senders, {Receivers} receivers): {Elapsed}",
            totalSenders, receiverCount, totalSw.Elapsed);

        using (var roundOperator = new KompaktorRoundOperator(Network, RPC, SecureRandom.Instance,
                   _loggerFactory.CreateLogger<KompaktorRoundOperator>()))
        {
            ConcurrentBag<KompaktorRoundEvent> roundEvents = new();
            var phaseTimestamps = new ConcurrentDictionary<string, TimeSpan>();

            roundOperator.NewEvent += (sender, args) =>
            {
                roundEvents.Add(args);
                if (args is KompaktorRoundEventStatusUpdate statusUpdate)
                {
                    phaseTimestamps[statusUpdate.Status.ToString()] = totalSw.Elapsed;
                    phaseLog.LogInformation("[TIMER] Phase → {Phase} at {Elapsed}", statusUpdate.Status, totalSw.Elapsed);
                }
                return Task.CompletedTask;
            };

            var issuerKey = new CredentialIssuerSecretKey(SecureRandom.Instance);
            var rangeProofSystem = new BulletproofPlusPlusRangeProof();
            Dictionary<CredentialType, ICredentialIssuer> issuers = new()
            {
                {
                    CredentialType.Amount, new BulletproofCredentialIssuer(issuerKey, rangeProofSystem,
                        SecureRandom.Instance, Money.Coins(100_000m).Satoshi)
                }
            };

            await roundOperator.Start(new KompaktorRoundEventCreated(
                    Guid.NewGuid().ToString(),
                    new FeeRate(2m),
                    TimeSpan.FromMinutes(10),
                    TimeSpan.FromMinutes(10),
                    TimeSpan.FromMinutes(10),
                    new IntRange(totalSenders, totalSenders + 50),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    new IntRange(1, totalSenders * 3),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    issuers.ToDictionary(pair => pair.Key,
                        pair => pair.Key.CredentialConfiguration(pair.Value, useBulletproofs: true)),
                    InputRegistrationSoftTimeout: TimeSpan.FromSeconds(60)),
                issuers);

            Eventually(() =>
                Assert.IsType<KompaktorRoundEventCreated>(Assert.Single(roundEvents)));

            var kompaktorRoundApiFactory = new LocalKompaktorRoundApiFactory(roundOperator);
            var allClients = new List<KompaktorRoundClient>();

            // Receiver clients — receive only, no coins to register
            for (int r = 0; r < receiverCount; r++)
            {
                var ri = r;
                var logger = _loggerFactory.CreateLogger($"ReceiverClient_{r}");
                var messagingApi = new KompaktorMessagingApi(logger, roundOperator, roundOperator);
                var client = new KompaktorRoundClient(
                    SecureRandom.Instance, Network, roundOperator, kompaktorRoundApiFactory,
                    [
                        new InteractivePaymentReceiverBehaviorTrait(receiverWallets[ri],
                            messagingApi, sendersPerReceiver),
                        new ConsolidationBehaviorTrait(),
                        new SelfSendChangeBehaviorTrait(() => receiverAddresses[ri],
                            TimeSpan.FromSeconds(90))
                    ],
                    receiverWallets[ri], logger);
                allClients.Add(client);
            }

            // Sender clients — fund and pay
            for (int s = 0; s < totalSenders; s++)
            {
                var si = s;
                var logger = _loggerFactory.CreateLogger($"SenderClient_{s}");
                var messagingApi = new KompaktorMessagingApi(logger, roundOperator, roundOperator);
                var client = new KompaktorRoundClient(
                    SecureRandom.Instance, Network, roundOperator, kompaktorRoundApiFactory,
                    [
                        new InteractivePaymentSenderBehaviorTrait(senderWallets[si], messagingApi),
                        new ConsolidationBehaviorTrait(),
                        new SelfSendChangeBehaviorTrait(() => senderWallets[si].GetAddress().ScriptPubKey,
                            TimeSpan.FromSeconds(90))
                    ],
                    senderWallets[si], logger);
                allClients.Add(client);
            }

            await Eventually(async () =>
            {
                foreach (var client in allClients)
                {
                    if (client.PhasesTask.IsFaulted || client.PhasesTask.IsCompleted)
                        await client.PhasesTask;
                }

                Assert.Equal(totalSenders,
                    roundEvents.Count(e => e is KompaktorRoundEventInputRegistered));
                Assert.NotNull(roundEvents.SingleOrDefault(e =>
                    e is KompaktorRoundEventStatusUpdate { Status: KompaktorStatus.OutputRegistration }));
                Assert.NotNull(roundEvents.SingleOrDefault(e =>
                    e is KompaktorRoundEventStatusUpdate { Status: KompaktorStatus.Signing }));
                Assert.Equal(totalSenders,
                    roundEvents.Count(e => e is KompaktorRoundEventSignaturePosted));
                Assert.NotNull(roundEvents.SingleOrDefault(e =>
                    e is KompaktorRoundEventStatusUpdate { Status: KompaktorStatus.Completed }));

                var txid = allClients[0].Round.GetTransaction(Network).GetHash();
                var tx = await RPC.GetRawTransactionAsync(txid);
                foreach (var wallet in allWallets)
                    wallet.AddTransaction(tx);

                // --- KEY ASSERTION: Each receiver gets exactly 1 output ---
                for (int r = 0; r < receiverCount; r++)
                {
                    var receiverScript = receiverAddresses[r];
                    var receiverOutputs = tx.Outputs.Where(o => o.ScriptPubKey == receiverScript).ToList();
                    phaseLog.LogInformation("[BATCH] Receiver_{Index}: {Count} outputs, total {Amount} BTC",
                        r, receiverOutputs.Count, receiverOutputs.Sum(o => o.Value.ToDecimal(MoneyUnit.BTC)));

                    Assert.Single(receiverOutputs);

                    // Receiver gets value from successful interactive payments only;
                    // failed interactive payments fall back to non-interactive (sender-side output).
                    // At scale, some failures are expected — assert receiver got at least something.
                    var receivedAmount = receiverOutputs[0].Value.ToDecimal(MoneyUnit.BTC);
                    Assert.True(receivedAmount > 0m,
                        $"Receiver_{r} got {receivedAmount} BTC, expected > 0");
                }

                // Verify all payments settled
                foreach (var wallet in allWallets)
                {
                    Assert.Empty(await wallet.GetOutboundPendingPayments(true));
                    Assert.Empty(await wallet.GetInboundPendingPayments(true));
                }

                phaseLog.LogInformation("[BATCH] Transaction: {Inputs} inputs, {Outputs} outputs",
                    tx.Inputs.Count, tx.Outputs.Count);

            }, 900_000); // 15 minutes

            totalSw.Stop();
            var finalOutputCount = roundEvents.Count(e => e is KompaktorRoundEventOutputRegistered);
            var summary = new StringBuilder();
            summary.AppendLine();
            summary.AppendLine("╔══════════════════════════════════════════════════════════════╗");
            summary.AppendLine("║       BATCHED RECEIVER SCALE TEST TIMING BREAKDOWN          ║");
            summary.AppendLine("╠══════════════════════════════════════════════════════════════╣");

            var phases = new[] { "InputRegistration", "OutputRegistration", "Signing", "Broadcasting", "Completed" };
            TimeSpan prev = TimeSpan.Zero;
            foreach (var phase in phases)
            {
                if (phaseTimestamps.TryGetValue(phase, out var ts))
                {
                    var duration = ts - prev;
                    var pct = totalSw.Elapsed.TotalSeconds > 0 ? duration.TotalSeconds / totalSw.Elapsed.TotalSeconds * 100 : 0;
                    summary.AppendLine($"║  {phase,-25} {duration.TotalSeconds,8:F1}s  ({pct,4:F0}%)          ║");
                    prev = ts;
                }
            }

            summary.AppendLine("╠══════════════════════════════════════════════════════════════╣");
            summary.AppendLine($"║  Total elapsed              {totalSw.Elapsed.TotalSeconds,8:F1}s                       ║");
            summary.AppendLine($"║  Senders                    {totalSenders,8}                        ║");
            summary.AppendLine($"║  Receivers                  {receiverCount,8}                        ║");
            summary.AppendLine($"║  Outputs registered         {finalOutputCount,8}                        ║");
            summary.AppendLine($"║  Target: {receiverCount} receiver outputs + {totalSenders} sender change = {receiverCount + totalSenders} ideal  ║");
            summary.AppendLine("╚══════════════════════════════════════════════════════════════╝");
            phaseLog.LogInformation(summary.ToString());

            foreach (var client in allClients)
                client.Dispose();
        }
    }

    [Fact]
    public async Task CanDoInteractivePaymentsMultipleReceivers()
    {
        var receiverCount = 5;
        var sendersPerReceiver = 20;
        var totalSenders = receiverCount * sendersPerReceiver;

        List<Wallet> allWallets = new();

        // Create receiver wallets
        var receiverWallets = new List<Wallet>();
        for (int r = 0; r < receiverCount; r++)
        {
            var receiverWallet = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
                _loggerFactory.CreateLogger($"Receiver_{r}"));
            receiverWallets.Add(receiverWallet);
            allWallets.Add(receiverWallet);
        }

        // Create sender wallets
        var senderWallets = new List<Wallet>();
        for (int s = 0; s < totalSenders; s++)
        {
            var senderWallet = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
                _loggerFactory.CreateLogger($"Sender_{s}"));
            senderWallets.Add(senderWallet);
            allWallets.Add(senderWallet);
        }

        // Fund each sender wallet with 1 BTC
        foreach (var senderWallet in senderWallets)
        {
            await CashCow(RPC, senderWallet.GetAddress(), Money.Coins(1), allWallets);
        }

        await RPC.GenerateAsync(1);

        // Each receiver requests payments from their assigned senders
        for (int r = 0; r < receiverCount; r++)
        {
            for (int s = 0; s < sendersPerReceiver; s++)
            {
                var senderIndex = r * sendersPerReceiver + s;
                var interactivePayment = await receiverWallets[r].RequestPayment(Money.Coins(0.1m));
                await senderWallets[senderIndex].SchedulePayment(interactivePayment.Destination,
                    interactivePayment.Amount,
                    interactivePayment.KompaktorKey.ToXPubKey(), true, interactivePayment.Id);
            }
        }

        // --- Output payment routing graph ---
        var graph = new StringBuilder();
        graph.AppendLine();
        graph.AppendLine("╔══════════════════════════════════════════════════════╗");
        graph.AppendLine("║       MULTIPLE RECEIVERS ROUTING GRAPH              ║");
        graph.AppendLine("╠══════════════════════════════════════════════════════╣");
        graph.AppendLine($"║  Participants: {receiverCount} receivers, {totalSenders} senders              ║");
        graph.AppendLine($"║  Payment flows: {totalSenders} total (0.1 BTC each)             ║");
        graph.AppendLine("╠══════════════════════════════════════════════════════╣");
        graph.AppendLine("║  Sender → Receiver                                  ║");
        graph.AppendLine("║                                                      ║");
        for (int r = 0; r < receiverCount; r++)
        {
            var firstS = r * sendersPerReceiver;
            var lastS = firstS + sendersPerReceiver - 1;
            graph.AppendLine($"║    S{firstS,-3}..S{lastS,-3} ─── 0.1 BTC ──→ R{r}  ({sendersPerReceiver} senders)  ║");
        }
        graph.AppendLine("║                                                      ║");
        graph.AppendLine($"║  Each receiver compacts {sendersPerReceiver} inputs → 1 output          ║");
        graph.AppendLine("╚══════════════════════════════════════════════════════╝");
        _loggerFactory.CreateLogger("PaymentGraph").LogInformation(graph.ToString());

        using (var roundOperator = new KompaktorRoundOperator(Network, RPC, SecureRandom.Instance,
                   _loggerFactory.CreateLogger<KompaktorRoundOperator>()))
        {
            ConcurrentBag<KompaktorRoundEvent> roundEvents = new();
            roundOperator.NewEvent += (sender, args) =>
            {
                roundEvents.Add(args);
                return Task.CompletedTask;
            };

            Dictionary<CredentialType, ICredentialIssuer> issuers = new()
            {
                {
                    CredentialType.Amount, new CredentialIssuer(new CredentialIssuerSecretKey(SecureRandom.Instance),
                        SecureRandom.Instance, Money.Coins(100_000m).Satoshi)
                }
            };

            await roundOperator.Start(new KompaktorRoundEventCreated(
                    Guid.NewGuid().ToString(),
                    new FeeRate(2m),
                    TimeSpan.FromSeconds(120),
                    TimeSpan.FromMinutes(20),
                    TimeSpan.FromMinutes(5),
                    new IntRange(1, totalSenders + 50),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    new IntRange(1, totalSenders * 3),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    issuers.ToDictionary(pair => pair.Key, pair => pair.Key.CredentialConfiguration(pair.Value))),
                issuers);

            Eventually(() =>
                Assert.IsType<KompaktorRoundEventCreated>(Assert.Single(roundEvents)));

            var kompaktorRoundApiFactory = new LocalKompaktorRoundApiFactory(roundOperator);

            // Create receiver clients
            var receiverClients = new List<KompaktorRoundClient>();
            for (int r = 0; r < receiverCount; r++)
            {
                var ri = r; // capture for closure
                var logger = _loggerFactory.CreateLogger($"ReceiverClient_{r}");
                var client = new KompaktorRoundClient(
                    SecureRandom.Instance,
                    Network,
                    roundOperator,
                    kompaktorRoundApiFactory,
                    [
                        new InteractivePaymentReceiverBehaviorTrait(receiverWallets[ri],
                            new KompaktorMessagingApi(logger, roundOperator, roundOperator), sendersPerReceiver),
                        new ConsolidationBehaviorTrait(),
                        new SelfSendChangeBehaviorTrait(() => receiverWallets[ri].GetAddress().ScriptPubKey,
                            TimeSpan.FromSeconds(90))
                    ],
                    receiverWallets[ri], logger);
                receiverClients.Add(client);
            }

            // Create sender clients
            var senderClients = new List<KompaktorRoundClient>();
            for (int s = 0; s < totalSenders; s++)
            {
                var si = s; // capture for closure
                var logger = _loggerFactory.CreateLogger($"SenderClient_{s}");
                var client = new KompaktorRoundClient(
                    SecureRandom.Instance,
                    Network,
                    roundOperator,
                    kompaktorRoundApiFactory,
                    [
                        new InteractivePaymentSenderBehaviorTrait(senderWallets[si],
                            new KompaktorMessagingApi(logger, roundOperator, roundOperator)),
                        new ConsolidationBehaviorTrait(),
                        new SelfSendChangeBehaviorTrait(() => senderWallets[si].GetAddress().ScriptPubKey,
                            TimeSpan.FromSeconds(90))
                    ],
                    senderWallets[si], logger);
                senderClients.Add(client);
            }

            await Eventually(async () =>
            {
                // Check for faulted tasks
                foreach (var client in senderClients.Concat(receiverClients))
                {
                    if (client.PhasesTask.IsFaulted || client.PhasesTask.IsCompleted)
                    {
                        await client.PhasesTask;
                    }
                }

                Assert.Equal(totalSenders,
                    roundEvents.Count(@event => @event is KompaktorRoundEventInputRegistered));
                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.OutputRegistration}));

                var outputRegistrations =
                    roundEvents.Count(@event => @event is KompaktorRoundEventOutputRegistered);
                // Each receiver compacts their senders' payments into fewer outputs.
                // At minimum: receiverCount aggregate outputs + totalSenders change outputs.
                Assert.True(outputRegistrations >= receiverCount + totalSenders,
                    $"Expected at least {receiverCount + totalSenders} output registrations, got {outputRegistrations}");

                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.Signing}));
                Assert.Equal(totalSenders,
                    roundEvents.Count(@event => @event is KompaktorRoundEventSignaturePosted));
                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.Broadcasting}));
                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.Completed}));

                var txid = receiverClients[0].Round.GetTransaction(Network).GetHash();
                var operatorTxId = roundOperator.GetTransaction(Network).GetHash();
                Assert.Equal(txid, operatorTxId);
                var tx = await RPC.GetRawTransactionAsync(txid);
                foreach (var wallet in allWallets)
                {
                    wallet.AddTransaction(tx);
                }

                foreach (var wallet in allWallets)
                {
                    Assert.Empty(await wallet.GetOutboundPendingPayments(true));
                    Assert.Empty(await wallet.GetInboundPendingPayments(true));
                }
            }, 1_800_000); // 30 minutes

            foreach (var client in senderClients)
            {
                client.Dispose();
            }

            foreach (var client in receiverClients)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public async Task CanDoInteractivePaymentsMesh()
    {
        var merchantCount = 5;
        var customersPerMerchant = 10;
        var totalCustomers = merchantCount * customersPerMerchant;
        var merchantToCustomerPayments = 2; // each merchant pays 2 random customers

        List<Wallet> allWallets = new();

        // Create merchant wallets
        var merchantWallets = new List<Wallet>();
        for (int m = 0; m < merchantCount; m++)
        {
            var wallet = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
                _loggerFactory.CreateLogger($"Merchant_{m}"));
            merchantWallets.Add(wallet);
            allWallets.Add(wallet);
        }

        // Create customer wallets
        var customerWallets = new List<Wallet>();
        for (int c = 0; c < totalCustomers; c++)
        {
            var wallet = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
                _loggerFactory.CreateLogger($"Customer_{c}"));
            customerWallets.Add(wallet);
            allWallets.Add(wallet);
        }

        // Fund merchants with 1 BTC each (for their outgoing payments)
        foreach (var merchant in merchantWallets)
        {
            await CashCow(RPC, merchant.GetAddress(), Money.Coins(1), allWallets);
        }

        // Fund customers with 1 BTC each (for their outgoing payments)
        foreach (var customer in customerWallets)
        {
            await CashCow(RPC, customer.GetAddress(), Money.Coins(1), allWallets);
        }

        await RPC.GenerateAsync(1);

        // --- Schedule customer → merchant payments ---
        // Each customer pays their assigned merchant 0.1 BTC
        for (int m = 0; m < merchantCount; m++)
        {
            for (int c = 0; c < customersPerMerchant; c++)
            {
                var customerIndex = m * customersPerMerchant + c;
                var inboundPayment = await merchantWallets[m].RequestPayment(Money.Coins(0.1m));
                await customerWallets[customerIndex].SchedulePayment(inboundPayment.Destination,
                    inboundPayment.Amount,
                    inboundPayment.KompaktorKey.ToXPubKey(), true, inboundPayment.Id);
            }
        }

        // --- Schedule merchant → customer payments ---
        // Each merchant pays 2 customers from different merchant groups
        var customerReceiversCount = 0;
        for (int m = 0; m < merchantCount; m++)
        {
            for (int p = 0; p < merchantToCustomerPayments; p++)
            {
                // Pick a customer from a different merchant's group
                var targetCustomerIndex = ((m + 1 + p) * customersPerMerchant + p) % totalCustomers;
                var inboundPayment =
                    await customerWallets[targetCustomerIndex].RequestPayment(Money.Coins(0.05m));
                await merchantWallets[m].SchedulePayment(inboundPayment.Destination,
                    inboundPayment.Amount,
                    inboundPayment.KompaktorKey.ToXPubKey(), true, inboundPayment.Id);
                customerReceiversCount++;
            }
        }

        var totalInputs = totalCustomers + merchantCount; // everyone registers coins
        var totalPaymentFlows = totalCustomers + (merchantCount * merchantToCustomerPayments);

        // --- Output payment routing graph ---
        var graph = new StringBuilder();
        graph.AppendLine();
        graph.AppendLine("╔══════════════════════════════════════════════════════╗");
        graph.AppendLine("║           MESH PAYMENT ROUTING GRAPH                ║");
        graph.AppendLine("╠══════════════════════════════════════════════════════╣");
        graph.AppendLine($"║  Participants: {merchantCount} merchants, {totalCustomers} customers          ║");
        graph.AppendLine($"║  Payment flows: {totalPaymentFlows} total ({totalCustomers} C→M + {merchantCount * merchantToCustomerPayments} M→C)  ║");
        graph.AppendLine("╠══════════════════════════════════════════════════════╣");
        graph.AppendLine("║  Customer → Merchant (0.1 BTC each)                 ║");
        graph.AppendLine("║                                                      ║");
        for (int m = 0; m < merchantCount; m++)
        {
            var firstC = m * customersPerMerchant;
            var lastC = firstC + customersPerMerchant - 1;
            graph.AppendLine($"║    C{firstC,-2}..C{lastC,-2} ─── 0.1 BTC ──→ M{m}                    ║");
        }
        graph.AppendLine("║                                                      ║");
        graph.AppendLine("║  Merchant → Customer (0.05 BTC each)                ║");
        graph.AppendLine("║                                                      ║");
        for (int m = 0; m < merchantCount; m++)
        {
            var targets = new List<int>();
            for (int p = 0; p < merchantToCustomerPayments; p++)
            {
                targets.Add(((m + 1 + p) * customersPerMerchant + p) % totalCustomers);
            }
            graph.AppendLine($"║    M{m} ─── 0.05 BTC ──→ C{string.Join(", C", targets),-20}    ║");
        }
        graph.AppendLine("║                                                      ║");
        graph.AppendLine("║  Bidirectional participants (both send & receive):   ║");
        // Find customers who are also receivers
        var customerReceiverIndices = new HashSet<int>();
        for (int m = 0; m < merchantCount; m++)
            for (int p = 0; p < merchantToCustomerPayments; p++)
                customerReceiverIndices.Add(((m + 1 + p) * customersPerMerchant + p) % totalCustomers);
        graph.AppendLine($"║    Merchants: M0..M{merchantCount - 1} (receive from C, send to C)    ║");
        graph.AppendLine($"║    Customers: {string.Join(", ", customerReceiverIndices.OrderBy(i => i).Select(i => $"C{i}"))} ║");
        graph.AppendLine("╚══════════════════════════════════════════════════════╝");
        _loggerFactory.CreateLogger("PaymentGraph").LogInformation(graph.ToString());

        using (var roundOperator = new KompaktorRoundOperator(Network, RPC, SecureRandom.Instance,
                   _loggerFactory.CreateLogger<KompaktorRoundOperator>()))
        {
            ConcurrentBag<KompaktorRoundEvent> roundEvents = new();
            roundOperator.NewEvent += (sender, args) =>
            {
                roundEvents.Add(args);
                return Task.CompletedTask;
            };

            Dictionary<CredentialType, ICredentialIssuer> issuers = new()
            {
                {
                    CredentialType.Amount, new CredentialIssuer(new CredentialIssuerSecretKey(SecureRandom.Instance),
                        SecureRandom.Instance, Money.Coins(100_000m).Satoshi)
                }
            };

            await roundOperator.Start(new KompaktorRoundEventCreated(
                    Guid.NewGuid().ToString(),
                    new FeeRate(2m),
                    TimeSpan.FromSeconds(120),
                    TimeSpan.FromMinutes(20),
                    TimeSpan.FromMinutes(5),
                    new IntRange(1, totalInputs + 50),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    new IntRange(1, totalPaymentFlows * 3),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    issuers.ToDictionary(pair => pair.Key, pair => pair.Key.CredentialConfiguration(pair.Value))),
                issuers);

            Eventually(() =>
                Assert.IsType<KompaktorRoundEventCreated>(Assert.Single(roundEvents)));

            var kompaktorRoundApiFactory = new LocalKompaktorRoundApiFactory(roundOperator);
            var allClients = new List<KompaktorRoundClient>();

            // Create merchant clients — both sender AND receiver traits
            for (int m = 0; m < merchantCount; m++)
            {
                var mi = m; // capture loop variable for closure
                var logger = _loggerFactory.CreateLogger($"MerchantClient_{m}");
                var messagingApi = new KompaktorMessagingApi(logger, roundOperator, roundOperator);
                var client = new KompaktorRoundClient(
                    SecureRandom.Instance,
                    Network,
                    roundOperator,
                    kompaktorRoundApiFactory,
                    [
                        new InteractivePaymentReceiverBehaviorTrait(merchantWallets[mi],
                            messagingApi, customersPerMerchant),
                        new InteractivePaymentSenderBehaviorTrait(merchantWallets[mi],
                            messagingApi),
                        new ConsolidationBehaviorTrait(),
                        new SelfSendChangeBehaviorTrait(() => merchantWallets[mi].GetAddress().ScriptPubKey,
                            TimeSpan.FromSeconds(90))
                    ],
                    merchantWallets[mi], logger);
                allClients.Add(client);
            }

            // Create customer clients — both sender AND receiver traits
            // (some customers receive merchant payments)
            for (int c = 0; c < totalCustomers; c++)
            {
                var ci = c; // capture loop variable for closure
                var logger = _loggerFactory.CreateLogger($"CustomerClient_{c}");
                var messagingApi = new KompaktorMessagingApi(logger, roundOperator, roundOperator);
                var client = new KompaktorRoundClient(
                    SecureRandom.Instance,
                    Network,
                    roundOperator,
                    kompaktorRoundApiFactory,
                    [
                        new InteractivePaymentSenderBehaviorTrait(customerWallets[ci],
                            messagingApi),
                        new InteractivePaymentReceiverBehaviorTrait(customerWallets[ci],
                            messagingApi, merchantToCustomerPayments),
                        new ConsolidationBehaviorTrait(),
                        new SelfSendChangeBehaviorTrait(() => customerWallets[ci].GetAddress().ScriptPubKey,
                            TimeSpan.FromSeconds(90))
                    ],
                    customerWallets[ci], logger);
                allClients.Add(client);
            }

            await Eventually(async () =>
            {
                // Check for faulted tasks
                foreach (var client in allClients)
                {
                    if (client.PhasesTask.IsFaulted || client.PhasesTask.IsCompleted)
                    {
                        await client.PhasesTask;
                    }
                }

                Assert.Equal(totalInputs,
                    roundEvents.Count(@event => @event is KompaktorRoundEventInputRegistered));
                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.OutputRegistration}));

                var outputRegistrations =
                    roundEvents.Count(@event => @event is KompaktorRoundEventOutputRegistered);
                // Minimum: each participant gets at least a change output,
                // plus receiver aggregation outputs
                Assert.True(outputRegistrations >= totalInputs,
                    $"Expected at least {totalInputs} output registrations, got {outputRegistrations}");

                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.Signing}));
                Assert.Equal(totalInputs,
                    roundEvents.Count(@event => @event is KompaktorRoundEventSignaturePosted));
                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.Broadcasting}));
                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate {Status: KompaktorStatus.Completed}));

                var txid = allClients[0].Round.GetTransaction(Network).GetHash();
                var operatorTxId = roundOperator.GetTransaction(Network).GetHash();
                Assert.Equal(txid, operatorTxId);
                var tx = await RPC.GetRawTransactionAsync(txid);
                foreach (var wallet in allWallets)
                {
                    wallet.AddTransaction(tx);
                }

                // Verify all payments settled
                foreach (var wallet in allWallets)
                {
                    Assert.Empty(await wallet.GetOutboundPendingPayments(true));
                    Assert.Empty(await wallet.GetInboundPendingPayments(true));
                }
            }, 1_800_000); // 30 minutes

            foreach (var client in allClients)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// Complex payment chaining test: multi-hop payment graph where receivers use incoming
    /// funds to partially fund onward payments within the same coinjoin round.
    ///
    /// Payment graph (50 participants, 3 layers deep):
    ///
    ///   Layer 0 (Funders):    20 senders → each pays 0.1 BTC to a Layer 1 aggregator
    ///   Layer 1 (Relayers):    5 relayers → each receives from 4 senders, pays 0.3 BTC to a Layer 2 node
    ///   Layer 2 (Collectors):  2 collectors → each receives from 2-3 relayers, pays 0.5 BTC to the final sink
    ///   Layer 3 (Sink):        1 final recipient receives from both collectors
    ///
    /// Additionally, 22 independent senders make direct payments to random relayers/collectors
    /// to stress test the credential reissuance graph with mixed payment depths.
    ///
    /// All within a single coinjoin round using BP++ range proofs.
    /// </summary>
    [Fact]
    [Trait("Category", "Scale")]
    public async Task CanDoInteractivePaymentChaining()
    {
        var phaseLog = _loggerFactory.CreateLogger("PhaseTimer");
        var totalSw = Stopwatch.StartNew();
        var phaseSw = Stopwatch.StartNew();

        // --- Wallet topology ---
        // Layer 0: 20 funders (send only)
        // Layer 1: 5 relayers (receive from funders, send to collectors)
        // Layer 2: 2 collectors (receive from relayers, send to sink)
        // Layer 3: 1 sink (receive only)
        // Extra: 22 independent senders → random recipients in layer 1/2
        const int funderCount = 20;
        const int relayerCount = 5;
        const int collectorCount = 2;
        const int extraSenderCount = 22;
        const int fundersPerRelayer = funderCount / relayerCount; // 4

        var allWallets = new List<Wallet>();
        var funderWallets = new List<Wallet>();
        var relayerWallets = new List<Wallet>();
        var collectorWallets = new List<Wallet>();
        var extraSenderWallets = new List<Wallet>();

        Wallet CreateWallet(string name)
        {
            var w = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
                _loggerFactory.CreateLogger(name));
            allWallets.Add(w);
            return w;
        }

        for (int i = 0; i < funderCount; i++) funderWallets.Add(CreateWallet($"Funder_{i}"));
        for (int i = 0; i < relayerCount; i++) relayerWallets.Add(CreateWallet($"Relayer_{i}"));
        for (int i = 0; i < collectorCount; i++) collectorWallets.Add(CreateWallet($"Collector_{i}"));
        var sinkWallet = CreateWallet("Sink");
        for (int i = 0; i < extraSenderCount; i++) extraSenderWallets.Add(CreateWallet($"ExtraSender_{i}"));

        var totalParticipants = allWallets.Count; // 50

        phaseLog.LogInformation("[TIMER] Wallet creation ({Count}): {Elapsed}", totalParticipants, phaseSw.Elapsed);
        phaseSw.Restart();

        // Fund everyone who sends (funders, relayers, collectors, extra senders)
        var walletsToFund = funderWallets
            .Concat(relayerWallets)
            .Concat(collectorWallets)
            .Concat(extraSenderWallets).ToList();
        foreach (var w in walletsToFund)
            await CashCow(RPC, w.GetAddress(), Money.Coins(1), allWallets);
        await RPC.GenerateAsync(1);

        phaseLog.LogInformation("[TIMER] Wallet funding ({Count}): {Elapsed}", walletsToFund.Count, phaseSw.Elapsed);
        phaseSw.Restart();

        // --- Schedule payment graph ---
        var totalPaymentFlows = 0;

        // Layer 0 → Layer 1: Each funder pays its assigned relayer 0.1 BTC
        for (int r = 0; r < relayerCount; r++)
        {
            for (int f = 0; f < fundersPerRelayer; f++)
            {
                var funderIdx = r * fundersPerRelayer + f;
                var payment = await relayerWallets[r].RequestPayment(Money.Coins(0.1m));
                await funderWallets[funderIdx].SchedulePayment(payment.Destination, payment.Amount,
                    payment.KompaktorKey.ToXPubKey(), true, payment.Id);
                totalPaymentFlows++;
            }
        }

        // Layer 1 → Layer 2: Each relayer pays a collector 0.3 BTC
        // Relayers 0,1,2 → Collector 0; Relayers 3,4 → Collector 1
        var relayersPerCollector = new[] { 3, 2 };
        var relayerIdx = 0;
        for (int c = 0; c < collectorCount; c++)
        {
            for (int r = 0; r < relayersPerCollector[c]; r++)
            {
                var payment = await collectorWallets[c].RequestPayment(Money.Coins(0.3m));
                await relayerWallets[relayerIdx].SchedulePayment(payment.Destination, payment.Amount,
                    payment.KompaktorKey.ToXPubKey(), true, payment.Id);
                relayerIdx++;
                totalPaymentFlows++;
            }
        }

        // Layer 2 → Layer 3: Each collector pays the sink 0.5 BTC
        for (int c = 0; c < collectorCount; c++)
        {
            var payment = await sinkWallet.RequestPayment(Money.Coins(0.5m));
            await collectorWallets[c].SchedulePayment(payment.Destination, payment.Amount,
                payment.KompaktorKey.ToXPubKey(), true, payment.Id);
            totalPaymentFlows++;
        }

        // Extra senders → random relayers and collectors (stress the credential graph)
        var extraTargets = relayerWallets.Cast<Wallet>().Concat(collectorWallets).ToList();
        for (int i = 0; i < extraSenderCount; i++)
        {
            var target = extraTargets[i % extraTargets.Count];
            var payment = await target.RequestPayment(Money.Coins(0.05m));
            await extraSenderWallets[i].SchedulePayment(payment.Destination, payment.Amount,
                payment.KompaktorKey.ToXPubKey(), true, payment.Id);
            totalPaymentFlows++;
        }

        phaseLog.LogInformation("[TIMER] Payment scheduling ({Count} flows): {Elapsed}", totalPaymentFlows, phaseSw.Elapsed);
        phaseSw.Restart();

        // --- Log the payment graph ---
        var graph = new StringBuilder();
        graph.AppendLine();
        graph.AppendLine("╔══════════════════════════════════════════════════════════════╗");
        graph.AppendLine("║         CHAINED PAYMENT ROUTING GRAPH                       ║");
        graph.AppendLine("╠══════════════════════════════════════════════════════════════╣");
        graph.AppendLine($"║  Participants: {totalParticipants} total                                     ║");
        graph.AppendLine($"║  Payment flows: {totalPaymentFlows} ({funderCount} L0→L1, {relayerCount} L1→L2, {collectorCount} L2→L3, {extraSenderCount} extra)   ║");
        graph.AppendLine("╠══════════════════════════════════════════════════════════════╣");
        graph.AppendLine("║  Layer 0 (Funders)      ─── 0.10 BTC ──→  Layer 1 (Relayers)║");
        graph.AppendLine("║  Layer 1 (Relayers)     ─── 0.30 BTC ──→  Layer 2 (Collect.) ║");
        graph.AppendLine("║  Layer 2 (Collectors)   ─── 0.50 BTC ──→  Layer 3 (Sink)    ║");
        graph.AppendLine("║  Extra senders          ─── 0.05 BTC ──→  Relayers/Collect.  ║");
        graph.AppendLine("╠══════════════════════════════════════════════════════════════╣");
        graph.AppendLine("║  Key: Relayers receive funds AND forward them onward.        ║");
        graph.AppendLine("║  Collectors aggregate multiple relayer payments.             ║");
        graph.AppendLine("║  Credential reissuance depth: up to 3 hops.                 ║");
        graph.AppendLine("╚══════════════════════════════════════════════════════════════╝");
        phaseLog.LogInformation(graph.ToString());

        // --- Determine max concurrent inbound flows per wallet ---
        // Relayers receive from fundersPerRelayer + some extras
        var maxRelayerInbound = fundersPerRelayer + (extraSenderCount / extraTargets.Count) + 1;
        var maxCollectorInbound = relayersPerCollector.Max() + (extraSenderCount / extraTargets.Count) + 1;

        var totalInputs = walletsToFund.Count; // everyone who has coins registers

        using (var roundOperator = new KompaktorRoundOperator(Network, RPC, SecureRandom.Instance,
                   _loggerFactory.CreateLogger<KompaktorRoundOperator>()))
        {
            ConcurrentBag<KompaktorRoundEvent> roundEvents = new();
            var phaseTimestamps = new ConcurrentDictionary<string, TimeSpan>();

            roundOperator.NewEvent += (sender, args) =>
            {
                roundEvents.Add(args);
                if (args is KompaktorRoundEventStatusUpdate statusUpdate)
                {
                    phaseTimestamps[statusUpdate.Status.ToString()] = totalSw.Elapsed;
                    phaseLog.LogInformation("[TIMER] Phase transition → {Phase} at {Elapsed}", statusUpdate.Status, totalSw.Elapsed);
                }
                return Task.CompletedTask;
            };

            var issuerKey = new CredentialIssuerSecretKey(SecureRandom.Instance);
            var rangeProofSystem = new BulletproofPlusPlusRangeProof();
            Dictionary<CredentialType, ICredentialIssuer> issuers = new()
            {
                {
                    CredentialType.Amount, new BulletproofCredentialIssuer(issuerKey, rangeProofSystem,
                        SecureRandom.Instance, Money.Coins(100_000m).Satoshi)
                }
            };

            await roundOperator.Start(new KompaktorRoundEventCreated(
                    Guid.NewGuid().ToString(),
                    new FeeRate(2m),
                    TimeSpan.FromMinutes(10),
                    TimeSpan.FromMinutes(15),
                    TimeSpan.FromMinutes(10),
                    new IntRange(totalInputs, totalInputs + 50),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    new IntRange(1, totalPaymentFlows * 4),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    issuers.ToDictionary(pair => pair.Key,
                        pair => pair.Key.CredentialConfiguration(pair.Value, useBulletproofs: true)),
                    InputRegistrationSoftTimeout: TimeSpan.FromSeconds(60)),
                issuers);

            Eventually(() =>
                Assert.IsType<KompaktorRoundEventCreated>(Assert.Single(roundEvents)));

            phaseLog.LogInformation("[TIMER] Round operator started: {Elapsed}", phaseSw.Elapsed);
            phaseSw.Restart();

            var kompaktorRoundApiFactory = new LocalKompaktorRoundApiFactory(roundOperator);
            var allClients = new List<KompaktorRoundClient>();

            // Funders: send only
            for (int i = 0; i < funderCount; i++)
            {
                var idx = i;
                var logger = _loggerFactory.CreateLogger($"FunderClient_{i}");
                var messagingApi = new KompaktorMessagingApi(logger, roundOperator, roundOperator);
                var client = new KompaktorRoundClient(
                    SecureRandom.Instance, Network, roundOperator, kompaktorRoundApiFactory,
                    [
                        new InteractivePaymentSenderBehaviorTrait(funderWallets[idx], messagingApi),
                        new ConsolidationBehaviorTrait(),
                        new SelfSendChangeBehaviorTrait(() => funderWallets[idx].GetAddress().ScriptPubKey,
                            TimeSpan.FromSeconds(90))
                    ],
                    funderWallets[idx], logger);
                allClients.Add(client);
            }

            // Relayers: receive from funders + extras, send to collectors (both traits)
            for (int i = 0; i < relayerCount; i++)
            {
                var idx = i;
                var logger = _loggerFactory.CreateLogger($"RelayerClient_{i}");
                var messagingApi = new KompaktorMessagingApi(logger, roundOperator, roundOperator);
                var client = new KompaktorRoundClient(
                    SecureRandom.Instance, Network, roundOperator, kompaktorRoundApiFactory,
                    [
                        new InteractivePaymentReceiverBehaviorTrait(relayerWallets[idx],
                            messagingApi, maxRelayerInbound),
                        new InteractivePaymentSenderBehaviorTrait(relayerWallets[idx], messagingApi),
                        new ConsolidationBehaviorTrait(),
                        new SelfSendChangeBehaviorTrait(() => relayerWallets[idx].GetAddress().ScriptPubKey,
                            TimeSpan.FromSeconds(90))
                    ],
                    relayerWallets[idx], logger);
                allClients.Add(client);
            }

            // Collectors: receive from relayers + extras, send to sink (both traits)
            for (int i = 0; i < collectorCount; i++)
            {
                var idx = i;
                var logger = _loggerFactory.CreateLogger($"CollectorClient_{i}");
                var messagingApi = new KompaktorMessagingApi(logger, roundOperator, roundOperator);
                var client = new KompaktorRoundClient(
                    SecureRandom.Instance, Network, roundOperator, kompaktorRoundApiFactory,
                    [
                        new InteractivePaymentReceiverBehaviorTrait(collectorWallets[idx],
                            messagingApi, maxCollectorInbound),
                        new InteractivePaymentSenderBehaviorTrait(collectorWallets[idx], messagingApi),
                        new ConsolidationBehaviorTrait(),
                        new SelfSendChangeBehaviorTrait(() => collectorWallets[idx].GetAddress().ScriptPubKey,
                            TimeSpan.FromSeconds(90))
                    ],
                    collectorWallets[idx], logger);
                allClients.Add(client);
            }

            // Sink: receive only
            {
                var logger = _loggerFactory.CreateLogger("SinkClient");
                var messagingApi = new KompaktorMessagingApi(logger, roundOperator, roundOperator);
                var client = new KompaktorRoundClient(
                    SecureRandom.Instance, Network, roundOperator, kompaktorRoundApiFactory,
                    [
                        new InteractivePaymentReceiverBehaviorTrait(sinkWallet,
                            messagingApi, collectorCount),
                        new ConsolidationBehaviorTrait(),
                        new SelfSendChangeBehaviorTrait(() => sinkWallet.GetAddress().ScriptPubKey,
                            TimeSpan.FromSeconds(90))
                    ],
                    sinkWallet, logger);
                allClients.Add(client);
            }

            // Extra senders: send only
            for (int i = 0; i < extraSenderCount; i++)
            {
                var idx = i;
                var logger = _loggerFactory.CreateLogger($"ExtraSenderClient_{i}");
                var messagingApi = new KompaktorMessagingApi(logger, roundOperator, roundOperator);
                var client = new KompaktorRoundClient(
                    SecureRandom.Instance, Network, roundOperator, kompaktorRoundApiFactory,
                    [
                        new InteractivePaymentSenderBehaviorTrait(extraSenderWallets[idx], messagingApi),
                        new ConsolidationBehaviorTrait(),
                        new SelfSendChangeBehaviorTrait(() => extraSenderWallets[idx].GetAddress().ScriptPubKey,
                            TimeSpan.FromSeconds(90))
                    ],
                    extraSenderWallets[idx], logger);
                allClients.Add(client);
            }

            await Eventually(async () =>
            {
                foreach (var client in allClients)
                {
                    if (client.PhasesTask.IsFaulted || client.PhasesTask.IsCompleted)
                        await client.PhasesTask;
                }

                Assert.Equal(totalInputs,
                    roundEvents.Count(@event => @event is KompaktorRoundEventInputRegistered));
                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate { Status: KompaktorStatus.OutputRegistration }));

                var outputRegistrations =
                    roundEvents.Count(@event => @event is KompaktorRoundEventOutputRegistered);
                // Each funded participant gets at least a change output, plus receiver outputs
                Assert.True(outputRegistrations >= totalInputs,
                    $"Expected at least {totalInputs} output registrations, got {outputRegistrations}");

                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate { Status: KompaktorStatus.Signing }));
                Assert.Equal(totalInputs,
                    roundEvents.Count(@event => @event is KompaktorRoundEventSignaturePosted));
                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate { Status: KompaktorStatus.Broadcasting }));
                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate { Status: KompaktorStatus.Completed }));

                var txid = allClients[0].Round.GetTransaction(Network).GetHash();
                var operatorTxId = roundOperator.GetTransaction(Network).GetHash();
                Assert.Equal(txid, operatorTxId);
                var tx = await RPC.GetRawTransactionAsync(txid);
                foreach (var wallet in allWallets)
                    wallet.AddTransaction(tx);

                // Verify all payments settled
                foreach (var wallet in allWallets)
                {
                    Assert.Empty(await wallet.GetOutboundPendingPayments(true));
                    Assert.Empty(await wallet.GetInboundPendingPayments(true));
                }
            }, 1_800_000); // 30 minutes

            totalSw.Stop();
            var finalOutputCount = roundEvents.Count(e => e is KompaktorRoundEventOutputRegistered);
            var summary = new StringBuilder();
            summary.AppendLine();
            summary.AppendLine("╔══════════════════════════════════════════════════════════════╗");
            summary.AppendLine("║          CHAINED PAYMENT TEST TIMING BREAKDOWN              ║");
            summary.AppendLine("╠══════════════════════════════════════════════════════════════╣");

            var phases = new[] { "InputRegistration", "OutputRegistration", "Signing", "Broadcasting", "Completed" };
            TimeSpan prev = TimeSpan.Zero;
            foreach (var phase in phases)
            {
                if (phaseTimestamps.TryGetValue(phase, out var ts))
                {
                    var duration = ts - prev;
                    var pct = totalSw.Elapsed.TotalSeconds > 0 ? duration.TotalSeconds / totalSw.Elapsed.TotalSeconds * 100 : 0;
                    summary.AppendLine($"║  {phase,-25} {duration.TotalSeconds,8:F1}s  ({pct,4:F0}%)          ║");
                    prev = ts;
                }
            }

            summary.AppendLine("╠══════════════════════════════════════════════════════════════╣");
            summary.AppendLine($"║  Total elapsed              {totalSw.Elapsed.TotalSeconds,8:F1}s                       ║");
            summary.AppendLine($"║  Participants               {totalParticipants,8}                        ║");
            summary.AppendLine($"║  Payment flows              {totalPaymentFlows,8}                        ║");
            summary.AppendLine($"║  Chain depth                       3 hops                   ║");
            summary.AppendLine($"║  Inputs registered          {totalInputs,8}                        ║");
            summary.AppendLine($"║  Outputs registered         {finalOutputCount,8}                        ║");
            summary.AppendLine("╚══════════════════════════════════════════════════════════════╝");
            phaseLog.LogInformation(summary.ToString());

            foreach (var client in allClients)
                client.Dispose();
        }
    }

    [Fact]
    public async Task CanDoBlameRounds()
    {
        var honestCount = 4;
        var wallets = new List<Wallet>();

        // Create honest participant wallets
        for (int w = 0; w < honestCount; w++)
        {
            var wallet = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
                _loggerFactory.CreateLogger<Wallet>());
            wallets.Add(wallet);
        }

        // Create disruptor wallet
        var disruptorWallet = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
            _loggerFactory.CreateLogger<Wallet>());
        wallets.Add(disruptorWallet);

        // Fund all wallets
        foreach (var wallet in wallets)
        {
            await CashCow(RPC, wallet.GetAddress(), Money.Coins(1), wallets);
        }

        await RPC.GenerateAsync(1);

        var prison = new Prison.KompaktorPrison();

        // ── Round 1: Disruptor refuses to sign ──
        HashSet<OutPoint>? blameWhitelist = null;
        string? parentRoundId = null;

        using (var roundOperator = new KompaktorRoundOperator(Network, RPC, SecureRandom.Instance,
                   _loggerFactory.CreateLogger<KompaktorRoundOperator>(), prison))
        {
            ConcurrentBag<KompaktorRoundEvent> roundEvents = new();
            roundOperator.NewEvent += (sender, args) =>
            {
                roundEvents.Add(args);
                return Task.CompletedTask;
            };

            // Capture blame round request
            var blameRequested = new TaskCompletionSource<(string parentId, HashSet<OutPoint> whitelist)>();
            roundOperator.BlameRoundRequested += (parentId, whitelist) =>
            {
                blameRequested.TrySetResult((parentId, whitelist));
                return Task.CompletedTask;
            };

            Dictionary<CredentialType, ICredentialIssuer> issuers = new()
            {
                { CredentialType.Amount, CredentialType.Amount.CredentialIssuer(SecureRandom.Instance) }
            };

            await roundOperator.Start(new KompaktorRoundEventCreated(
                    Guid.NewGuid().ToString(),
                    new FeeRate(2m),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(10), // short signing timeout to trigger blame quickly
                    new IntRange(1, 10),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    new IntRange(1, 100),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    issuers.ToDictionary(pair => pair.Key, pair => pair.Key.CredentialConfiguration(pair.Value)),
                    TimeSpan.FromSeconds(5)), // soft timeout so we don't wait the full 30s
                issuers);

            Eventually(() =>
                Assert.IsType<KompaktorRoundEventCreated>(Assert.Single(roundEvents)));

            var apiFactory = new LocalKompaktorRoundApiFactory(roundOperator);
            var round = (KompaktorRound)roundOperator;

            // Create honest clients
            var honestClients = new List<KompaktorRoundClient>();
            for (int w = 0; w < honestCount; w++)
            {
                var traits = new List<KompaktorClientBaseBehaviorTrait>
                {
                    new ConsolidationBehaviorTrait(),
                    new SelfSendChangeBehaviorTrait(
                        () => wallets[w].GetAddress().ScriptPubKey,
                        TimeSpan.FromSeconds(15)),
                };
                var client = new KompaktorRoundClient(
                    SecureRandom.Instance, Network, round, apiFactory, traits,
                    wallets[w], _loggerFactory.CreateLogger($"Honest_{w}"));
                honestClients.Add(client);
            }

            // Create disruptor client — will be disposed when signing starts
            var disruptorTraits = new List<KompaktorClientBaseBehaviorTrait>
            {
                new ConsolidationBehaviorTrait(),
                new SelfSendChangeBehaviorTrait(
                    () => disruptorWallet.GetAddress().ScriptPubKey,
                    TimeSpan.FromSeconds(15)),
            };
            var disruptorClient = new KompaktorRoundClient(
                SecureRandom.Instance, Network, round, apiFactory, disruptorTraits,
                disruptorWallet, _loggerFactory.CreateLogger("Disruptor"));

            // Kill the disruptor when signing starts so it never submits signatures.
            // Throwing prevents Sign() from executing (Dispose() alone races with the signing code).
            disruptorClient.StartSigning += (_) =>
                throw new OperationCanceledException("Disruptor refusing to sign");

            // Wait for the round to fail and blame to be requested
            var blameResult = await blameRequested.Task.WaitAsync(TimeSpan.FromMinutes(3));
            parentRoundId = blameResult.parentId;
            blameWhitelist = blameResult.whitelist;

            // Round should have failed
            Eventually(() =>
            {
                Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate { Status: KompaktorStatus.Failed }));
            });

            // Whitelist should contain only honest inputs (not the disruptor's)
            var disruptorCoins = await disruptorWallet.GetCoins();
            foreach (var coin in disruptorCoins)
                Assert.DoesNotContain(coin.Outpoint, blameWhitelist);

            // Disruptor should be banned
            foreach (var coin in disruptorCoins)
                Assert.True(prison.IsBanned(coin.Outpoint), $"Disruptor coin {coin.Outpoint} should be banned");

            foreach (var client in honestClients)
                client.Dispose();
            disruptorClient.Dispose();
        }

        Assert.NotNull(blameWhitelist);
        Assert.NotNull(parentRoundId);
        Assert.Equal(honestCount, blameWhitelist.Count);

        // ── Blame Round: Only honest participants complete ──
        using (var blameOperator = new KompaktorRoundOperator(Network, RPC, SecureRandom.Instance,
                   _loggerFactory.CreateLogger<KompaktorRoundOperator>(), prison))
        {
            ConcurrentBag<KompaktorRoundEvent> blameEvents = new();
            blameOperator.NewEvent += (sender, args) =>
            {
                blameEvents.Add(args);
                return Task.CompletedTask;
            };

            Dictionary<CredentialType, ICredentialIssuer> issuers = new()
            {
                { CredentialType.Amount, CredentialType.Amount.CredentialIssuer(SecureRandom.Instance) }
            };

            await blameOperator.Start(new KompaktorRoundEventCreated(
                    Guid.NewGuid().ToString(),
                    new FeeRate(2m),
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(30),
                    new IntRange(1, honestCount),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    new IntRange(1, 100),
                    new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                    issuers.ToDictionary(pair => pair.Key, pair => pair.Key.CredentialConfiguration(pair.Value)))
                {
                    BlameOf = parentRoundId,
                    BlameWhitelist = blameWhitelist
                },
                issuers);

            Eventually(() =>
                Assert.IsType<KompaktorRoundEventCreated>(Assert.Single(blameEvents)));

            var apiFactory = new LocalKompaktorRoundApiFactory(blameOperator);
            var round = (KompaktorRound)blameOperator;

            // Only honest clients join the blame round
            var blameClients = new List<KompaktorRoundClient>();
            for (int w = 0; w < honestCount; w++)
            {
                var traits = new List<KompaktorClientBaseBehaviorTrait>
                {
                    new ConsolidationBehaviorTrait(),
                    new SelfSendChangeBehaviorTrait(
                        () => wallets[w].GetAddress().ScriptPubKey,
                        TimeSpan.FromSeconds(15)),
                };
                var client = new KompaktorRoundClient(
                    SecureRandom.Instance, Network, round, apiFactory, traits,
                    wallets[w], _loggerFactory.CreateLogger($"Blame_Honest_{w}"));
                blameClients.Add(client);
            }

            // Blame round should complete successfully
            await Eventually(async () =>
            {
                foreach (var client in blameClients)
                {
                    if (client.PhasesTask.IsFaulted || client.PhasesTask.IsCompleted)
                        await client.PhasesTask;
                }

                Assert.Equal(honestCount, blameEvents.Count(@event =>
                    @event is KompaktorRoundEventInputRegistered));

                Assert.NotNull(blameEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate { Status: KompaktorStatus.Signing }));

                Assert.Equal(honestCount, blameEvents.Count(@event =>
                    @event is KompaktorRoundEventSignaturePosted));

                Assert.NotNull(blameEvents.SingleOrDefault(@event =>
                    @event is KompaktorRoundEventStatusUpdate { Status: KompaktorStatus.Broadcasting }));

                // Verify the tx was broadcast
                var txid = blameClients[0].Round.GetTransaction(Network).GetHash();
                await RPC.GetRawTransactionAsync(txid);
            }, 120_000);

            foreach (var client in blameClients)
                client.Dispose();
        }
    }

    private async Task<Transaction> CashCow(RPCClient rpcClient, BitcoinAddress address, Money amount,
        IEnumerable<Wallet> wallets)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
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
            catch (Exception) when (attempt < 4)
            {
                await rpcClient.GenerateAsync(10);
            }
        }

        throw new InvalidOperationException("CashCow failed after 5 attempts");
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
    public void DependencyGraph2Change()
    {
        
        var inputRange = new IntRange(2, 2);
        var outputRange = new IntRange(2, 2);

        var ins = new long[] {5, 8};
        var outs = new long[] {12};
        var logger = _loggerFactory.CreateLogger("dg");
       var result =  DependencyGraph2.Compute(logger, ins,
            outs, inputRange, outputRange);
       logger
           .LogInformation($"computed {result.CountDescendants()} actions with {result.GetMaxDepth()} depth ");
       logger.LogInformation(result.GenerateGraphviz(outs));
         logger.LogInformation(result.GenerateAscii(outs));
        
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
                    outs2[j] = maxForThisOutput <= 1 ? 1 : Random.Shared.NextInt64(1, maxForThisOutput); // Distribute a portion to outs2[j]
                }
            }

            result = DependencyGraph2.Compute(_loggerFactory.CreateLogger("dg"), ins2, outs2, inputRange, outputRange);
            _loggerFactory.CreateLogger("output")
                .LogInformation($"computed {result.CountDescendants()} actions with {result.GetMaxDepth()} depth ");
        }
    }

    /// <summary>
    /// Full integration test with k=4 credential arity — consolidation round with 10 participants.
    /// Compares against the default k=2 by running both and logging timing.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task CanConsolidateWithVariableArity(int k)
    {
        var participantCount = 10;
        var logger = _loggerFactory.CreateLogger($"arity-k{k}");
        List<Wallet> wallets = new();

        for (int w = 0; w < participantCount; w++)
        {
            var participantWallet = new Wallet(Network, new Mnemonic(Wordlist.English, WordCount.Twelve),
                _loggerFactory.CreateLogger<Wallet>());
            wallets.Add(participantWallet);
        }

        // Fund each participant with 2 coins
        foreach (var participantWallet in wallets)
        {
            await CashCow(RPC, participantWallet.GetAddress(), Money.Coins(1), wallets);
            await CashCow(RPC, participantWallet.GetAddress(), Money.Coins(0.5m), wallets);
        }

        await RPC.GenerateAsync(1);

        using var roundOperator = new KompaktorRoundOperator(Network, RPC, SecureRandom.Instance,
            _loggerFactory.CreateLogger<KompaktorRoundOperator>());
        ConcurrentBag<KompaktorRoundEvent> roundEvents = new();

        roundOperator.NewEvent += (sender, args) =>
        {
            roundEvents.Add(args);
            return Task.CompletedTask;
        };

        // Create issuer with variable k
        Dictionary<CredentialType, ICredentialIssuer> issuers = new()
        {
            {
                CredentialType.Amount, new CredentialIssuer(new CredentialIssuerSecretKey(SecureRandom.Instance),
                    SecureRandom.Instance, Money.Coins(100_000m).Satoshi, k)
            }
        };

        await roundOperator.Start(new KompaktorRoundEventCreated(
                Guid.NewGuid().ToString(),
                new FeeRate(2m),
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(120),
                TimeSpan.FromSeconds(120),
                new IntRange(1, participantCount * 3),
                new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                new IntRange(1, participantCount * 3),
                new MoneyRange(Money.Satoshis(10000), Money.Coins(100)),
                issuers.ToDictionary(pair => pair.Key, pair => pair.Key.CredentialConfiguration(pair.Value))),
            issuers);

        Eventually(() =>
            Assert.IsType<KompaktorRoundEventCreated>(Assert.Single(roundEvents)));

        var kompaktorRoundApiFactory = new LocalKompaktorRoundApiFactory(roundOperator);
        var kompaktorRound = (KompaktorRound)roundOperator;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Create clients for all participants
        var clients = new List<KompaktorRoundClient>();
        for (int w = 0; w < participantCount; w++)
        {
            var participantWallet = wallets[w];
            var traits = new List<KompaktorClientBaseBehaviorTrait>
            {
                new ConsolidationBehaviorTrait(),
                new SelfSendChangeBehaviorTrait(
                    () => participantWallet.GetAddress().ScriptPubKey,
                    TimeSpan.FromSeconds(45)),
            };

            var client = new KompaktorRoundClient(
                SecureRandom.Instance,
                Network,
                kompaktorRound,
                kompaktorRoundApiFactory,
                traits,
                participantWallet, _loggerFactory.CreateLogger($"k{k}_Wallet_{w}"));
            clients.Add(client);
        }

        await Eventually(async () =>
        {
            foreach (var client in clients)
            {
                if (client.PhasesTask.IsFaulted || client.PhasesTask.IsCompleted)
                    await client.PhasesTask;
            }

            Assert.Equal(participantCount * 2, roundEvents.Count(@event =>
                @event is KompaktorRoundEventInputRegistered));
            Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                @event is KompaktorRoundEventStatusUpdate { Status: KompaktorStatus.OutputRegistration }));
            Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                @event is KompaktorRoundEventStatusUpdate { Status: KompaktorStatus.Signing }));
            Assert.Equal(participantCount * 2, roundEvents.Count(@event =>
                @event is KompaktorRoundEventSignaturePosted));
            Assert.NotNull(roundEvents.SingleOrDefault(@event =>
                @event is KompaktorRoundEventStatusUpdate { Status: KompaktorStatus.Broadcasting }));

            var txid = clients[0].Round.GetTransaction(Network).GetHash();
            var operatorTxId = roundOperator.GetTransaction(Network).GetHash();
            Assert.Equal(txid, operatorTxId);
            var tx = await RPC.GetRawTransactionAsync(txid);
            foreach (var wallet in wallets)
                wallet.AddTransaction(tx);
        }, 300_000); // 5 minutes

        sw.Stop();
        logger.LogInformation("k={K} n={N}: Full round completed in {Elapsed}ms",
            k, participantCount, sw.ElapsedMilliseconds);

        foreach (var client in clients)
            client.Dispose();
    }
}