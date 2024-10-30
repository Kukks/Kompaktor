using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Kompaktor.Behaviors;
using Kompaktor.Contracts;
using Kompaktor.Credentials;
using Kompaktor.Mapper;
using Kompaktor.Models;
using Kompaktor.Utils;
using Microsoft.Extensions.Logging;
using NBitcoin;
using WabiSabi.CredentialRequesting;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Randomness;
using Range = Kompaktor.Models.Range;
using TaskScheduler = Kompaktor.Utils.TaskScheduler;

namespace Kompaktor;

//TODO: Allow multiple input registration in one call
//TODO: Allow multiple output registration in one call
//TODO: Allow multiple signing in one call
//TODO: Introduce ready to sign 

public class KompaktorRoundClient : IDisposable
{
    private readonly List<KompaktorClientBaseBehaviorTrait> _behaviorTraits;
    private readonly IKompaktorWalletInterface _walletInterface;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly IKompaktorRoundApiFactory _factory;
    private readonly WasabiRandom _random;
    private readonly Network _network;
    public readonly KompaktorRound Round;
    private readonly Channel<KompaktorStatus> _statusChannel;

    public KompaktorRoundClient(
        WasabiRandom random,
        Network network,
        KompaktorRound round,
        IKompaktorRoundApiFactory factory,
        List<KompaktorClientBaseBehaviorTrait> behaviorTraits,
        IKompaktorWalletInterface walletInterface,
        ILogger logger)
    {
        _random = random;
        _network = network;
        Round = round;
        _factory = factory;
        _behaviorTraits = behaviorTraits;
        _walletInterface = walletInterface;
        _logger = logger;


        foreach (var behaviorTrait in behaviorTraits) behaviorTrait.Start(this);
        _logger.LogInformation($"{_behaviorTraits.Count} behavior traits started");
        _statusChannel =
            Channel.CreateBounded<KompaktorStatus>(Enum.GetValues<KompaktorStatus>().Length);

        round.NewEvent += OnNewEvent;
        StatusChanged += OnStatusChanged;
        PhasesTask = ProcessStatuses();
    }

    public Task PhasesTask { get; }

    // public ConcurrentMultiDictionary<OutPoint, MAC> CoinToCredentials { get; set; } = new();

    public ConcurrentDictionary<string, BlindedCredential> AllCredentials { get; set; } = new();

    public HashSet<string> SpentCredentials => Identities
        .SelectMany(identity => identity.SpentCredentials.Select(mac => mac.Serial())).ToHashSet();

    public BlindedCredential[] AvailableCredentials => AllCredentials.Where(kvp => !SpentCredentials.Contains(kvp.Key))
        .Select(kvp => kvp.Value).ToArray();

    public ConcurrentHashSet<KompaktorIdentity> Identities { get; set; } = new();


    public List<Coin>? CoinCandidates { get; private set; }
    public List<Coin> RejectedCoins { get; } = [];

    public ImmutableArray<Coin>? RemainingCoinCandidates => CoinCandidates?
        .Except(AllocatedSelectedCoins.Keys)
        .ExceptBy(RegisteredInputs, coin => coin.Outpoint)
        .Except(RejectedCoins)
        .ToImmutableArray();

    public ConcurrentDictionary<Coin, KompaktorClientBaseBehaviorTrait?> AllocatedSelectedCoins { get; } = new();

    public OutPoint[] RegisteredInputs => Identities.Where(identity => identity.RegisteredInputs?.Any() is true)
        .SelectMany(identity => identity.RegisteredInputs).ToArray()!;

    public ImmutableArray<Coin>? RegisteredCoins =>
        CoinCandidates?.Where(coin => RegisteredInputs.Contains(coin.Outpoint)).ToImmutableArray();

    public BlindedCredential[] AvailableCredentialsForTrait(KompaktorClientBaseBehaviorTrait trait)
    {
        var traitCoins = AllocatedSelectedCoins.Where(kvp => kvp.Value == trait || kvp.Value is null)
            .Select(kvp => kvp.Key.Outpoint!).ToArray();

        Dictionary<Coin, BlindedCredential> CoinToCredentials = new();
        foreach (var traitCoin in traitCoins)
        {
            var creds = Identities.FirstOrDefault(identity => identity.RegisteredInputs?.Contains(traitCoin) is true);
            if (creds is not null)
            {
                var outpoint = creds.RegisteredInputs.First(input => input == traitCoin);
                var coin = RegisteredCoins.Value.First(coin => coin.Outpoint == outpoint);
                var closestCredAmt = coin.EstimateEffectiveValue(Round.RoundEventCreated.FeeRate);
                var createdCredentialsSerials = creds.CreatedCredentials.Select(mac => mac.Serial()).ToArray();
                var createdCredentials = AllCredentials.Where(pair => createdCredentialsSerials.Contains(pair.Key))
                    .Select(pair => pair.Value).ToArray();
                var closestCred = //select a credential from createdCredentials with the closest value to closestCredAmt
                    createdCredentials.OrderBy(credential => Math.Abs(credential.Value - closestCredAmt)).First();

                CoinToCredentials.Add(coin, closestCred);
            }
        }

        return CoinToCredentials.Values.ToArray();
    }

    public ConcurrentDictionary<TxOut, KompaktorClientBaseBehaviorTrait?> AllocatedPlannedOutputs { get; set; } = new();

    public TxOut[] RegisteredOutputs => Identities.Where(identity => identity.RegisteredOutputs is not null)
        .SelectMany(identity => identity.RegisteredOutputs!).ToArray()!;

    public TxOut[] RemainingPlannedOutputs()
    {
        var remaining = AllocatedPlannedOutputs.Keys.ToList();
        var exclude = RegisteredOutputs.Concat(FailedOutputs).ToArray();
        foreach (var txOut in exclude)
        {
            var item = remaining.FirstOrDefault(@out =>
                @out.ScriptPubKey == txOut.ScriptPubKey && @out.Value == txOut.Value);
            if (item != null)
            {
                remaining.Remove(item);
            }
        }

        return remaining.ToArray();
    }


    public void Dispose()
    {
        foreach (var behaviorTrait in _behaviorTraits) behaviorTrait.Dispose();

        _cts.Cancel();
        Round.NewEvent -= OnNewEvent;
        StatusChanged = null;
    }


    private readonly ConcurrentDictionary<CredentialType, WabiSabiClient> _createdClients = new();

    private WabiSabiClient GetWabiSabiClient(CredentialType credentialType)
    {
        if (_createdClients.TryGetValue(credentialType, out var client))
        {
            return client;
        }

        if (!Round.RoundEventCreated.CredentialIssuerParameters.TryGetValue(credentialType, out var issuerParameters))
        {
            throw new InvalidOperationException($"No issuer parameters for credential type {credentialType}");
        }

        var amt = Round.RoundEventCreated.CredentialAmount[credentialType];
        return _createdClients[credentialType] =
            new WabiSabiClient(issuerParameters, _random, amt);
    }

    private async Task OnStatusChanged(object sender, KompaktorStatus e)
    {
        await _statusChannel.Writer.WriteAsync(e);
    }

    private void OnNewEvent(object? sender, KompaktorRoundEvent e)
    {
        if (e is KompaktorRoundEventStatusUpdate statusUpdate) StatusChanged?.Invoke(this, statusUpdate.Status);
    }

    public event AsyncEventHandler<KompaktorStatus>? StatusChanged;
    public event AsyncEventHandler? StartCoinSelection;
    public event AsyncEventHandler? StartOutputRegistration;
    public event AsyncEventHandler? StartSigning;
    public event AsyncEventHandler? FinishedCoinSelection;
    public event AsyncEventHandler? FinishedOutputRegistration;
    public event AsyncEventHandler? FinishedCoinRegistration;

    public Dictionary<KompaktorClientBaseBehaviorTrait, bool> DoNotSignKillSwitches { get; } = new();

    private async Task ProcessStatuses()
    {
        await _statusChannel.Writer.WriteAsync(Round.Status);

        var exit = false;
        while (!_cts.IsCancellationRequested && !exit)
        {
            if (!await _statusChannel.Reader.WaitToReadAsync(_cts.Token))
            {
                continue;
            }

            var phase = await _statusChannel.Reader.ReadAsync(_cts.Token);

            _logger.LogInformation($"Processing phase {phase}");
            if (Round.Status != phase)
                continue;
            switch (phase)
            {
                case KompaktorStatus.InputRegistration:
                    CoinCandidates = (await _walletInterface.GetCoins())
                        .Where(coin => Round.RoundEventCreated.InputAmount.Contains(coin.Amount)).ToList();
                    await StartCoinSelection.InvokeIfNotNullAsync(this);
                    await FinishedCoinSelection.InvokeIfNotNullAsync(this);
                    _logger.LogInformation($"Finished coin selection. Selected {AllocatedSelectedCoins.Count} coins");
                    await RegisterCoins();
                    await FinishedCoinRegistration.InvokeIfNotNullAsync(this);

                    _logger.LogInformation($"Finished coin registration. Registered {Identities.Count} identities");
                    break;
                case KompaktorStatus.OutputRegistration:
                    _logger.LogInformation($"Starting output registration");
                    await StartOutputRegistration.InvokeIfNotNullAsync(this);
                    await RegisterOutputs();
                    await FinishedOutputRegistration.InvokeIfNotNullAsync(this);

                    _logger.LogInformation($"Finished intital output registration");
                    _ = Loop(RegisterOutputs, () => Round.Status != KompaktorStatus.OutputRegistration,
                        nameof(RegisterOutputs));
                    _ = Loop(ReadyToSign, () => Round.Status != KompaktorStatus.OutputRegistration,
                        nameof(ReadyToSign));
                    break;
                case KompaktorStatus.Signing:
                    await StartSigning.InvokeIfNotNullAsync(this);
                    await Sign();
                    break;
                case KompaktorStatus.Broadcasting:

                    _logger.LogInformation($"Tx fully signed, broadcasting {Round.GetTransaction(_network).GetHash()}");
                    break;
                case KompaktorStatus.Completed:

                    exit = true;
                    break;
                case KompaktorStatus.Failed:
                    exit = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    // private string lstMsg = "";
    public bool ShouldSign()
    {
        if (DoNotSignKillSwitches.Any(kvp => kvp.Value))
        {
            return false;
        }

        var identities = Identities.ToArray();
        // var outputs = identities.SelectMany(identity => identity.RegisteredOutputs).ToArray();
        // var inputs = identities.SelectMany(identity => identity.RegisteredInputs).ToArray();
        // var signalled = identities.Count(identity => identity.SignalledReady);

        //  var msg = $"Should sign: {signalled} signalled, {outputs.Length} outputs, {inputs.Length} inputs cred left: {AvailableCredentials.Sum(credential => credential.Value)}";
        // if (msg != lstMsg) 
        //  _logger.LogInformation(lstMsg);
        //  lstMsg = msg;
        return RemainingPlannedOutputs().Length == 0 && AvailableCredentials.Sum(credential => credential.Value) == 0;
        // _logger.LogInformation("SIGN!");
    }

    private async Task Sign()
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var subsetSign = !ShouldSign();
        var inputIdentities = Identities
            .Where(identity => identity.RegisteredInputs?.Any() is true).ToList();
        if (subsetSign)
        {
            _logger.LogInformation("Subset signing");
            var notSignalled = inputIdentities.Where(identity => !identity.SignalledReady).ToList();
            if (notSignalled.Count == 0)
            {
                inputIdentities.RemoveAt(Random.Shared.Next(inputIdentities.Count - 1));
            }
            else
            {
                inputIdentities.Remove(notSignalled[Random.Shared.Next(notSignalled.Count - 1)]);
            }
        }

        var tx = Round.GetTransaction(_network);
        var coins = Round.Inputs;
        var expiry = Round.SigningPhaseEnd - TimeSpan.FromSeconds(10);
        var signRequests = await Task.WhenAll(inputIdentities.SelectMany(identity =>
        {
            return identity.RegisteredInputs.Select(async input =>
            {
                var coin = RegisteredCoins.Value.First(coin => coin.Outpoint == input);
                var witness = await _walletInterface.GenerateWitness(coin, tx, coins);
                return (identity.Api, new SignRequest()
                {
                    OutPoint = coin.Outpoint,
                    Witness = witness
                });
            });
        }));
        var tasks = signRequests.Select<(IKompaktorRoundApi Api, SignRequest), Func<Task>>(signRequest =>
        {
            return async () => { await signRequest.Item1.Sign(signRequest.Item2); };
        }).ToArray();
        await TaskScheduler.Schedule("signing", tasks, expiry, _random, cts.Token, _logger);
    }

    private async Task Loop(Func<Task> task, Func<bool> stop, string name)
    {
        while (!stop() && !_cts.IsCancellationRequested)
        {
            await CatchException($"Error with loop task: {name}", async () =>
            {
                await task();
                await Task.Delay(100, _cts.Token);
            });
        }
    }

    private async Task RegisterCoin(Coin coin)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            var api = _factory.Create();

            var ownershipProof = await _walletInterface.GenerateOwnershipProof(Round.RoundEventCreated.RoundId, [coin]);
            var credentialClient = GetWabiSabiClient(CredentialType.Amount);


            var inputFee = ownershipProof.FundProofs.Sum(@in => @in.GetFee(Round.RoundEventCreated.FeeRate));
            var expectedCredentialAmount = coin.Amount - inputFee;


            var zeroAmountCredentialRequestData = credentialClient.CreateRequestForZeroAmount();
            var quote = await api.PreRegisterInput(new RegisterInputQuoteRequest
            {
                Signature = ownershipProof,
                CredentialsRequest = zeroAmountCredentialRequestData.CredentialsRequest
            });

            if (quote.CredentialAmount != expectedCredentialAmount)
                throw new InvalidOperationException("Expected credential amount does not match quote");

            var zeroCredentials = credentialClient.HandleResponse(quote.CredentialsResponse,
                zeroAmountCredentialRequestData.CredentialsResponseValidation).ToArray();

            var realCredentialsRequest =
                credentialClient.CreateRequest([expectedCredentialAmount, 0], zeroCredentials, cts.Token);

            var registered =
                await api.RegisterInput(new RegisterInputRequest(quote.Secret,
                    realCredentialsRequest.CredentialsRequest));
            var credentials = credentialClient.HandleResponse(registered.CredentialsResponse,
                realCredentialsRequest.CredentialsResponseValidation).ToArray();

            var macs = credentials.Select(credential => credential.Mac).ToArray();
            foreach (var credential in credentials)
            {
                AllCredentials.TryAdd(credential.Mac.Serial(), new BlindedCredential(credential));
            }


            var newIdentity = new KompaktorIdentity(api, new[] {coin.Outpoint}, null,
                zeroCredentials.Select(credential => credential.Mac).ToArray(),
                macs, quote.Secret, false);

            // CoinToCredentials.Add(coin.Outpoint, macs);

            Identities.Add(newIdentity);
        }
        catch (Exception e)
        {
            AllocatedSelectedCoins.Remove(coin, out _);
            RejectedCoins.Add(coin);
            await cts.CancelAsync();
            throw;
        }
    }

    private ConcurrentHashSet<TxOut> FailedOutputs { get; } = new();

    // private async Task RegisterOutput(TxOut txOut, BlindedCredential[] credentials)
    // {
    //     try
    //     {
    //         var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
    //
    //         var api = _factory.Create();
    //         var credentialClient = GetWabiSabiClient(CredentialType.Amount);
    //
    //         var amtBack = credentials.Sum(credential => credential.Value) -
    //                       txOut.EffectiveCost(Round.RoundEventCreated.FeeRate).Satoshi;
    //         var credentialRequest = credentialClient.CreateRequest([amtBack, 0],
    //             credentials, cts.Token);
    //
    //         var response =
    //             await api.RegisterOutput(new RegisterOutputRequest(credentialRequest.CredentialsRequest, txOut));
    //
    //         var newCredentials = credentialClient.HandleResponse(response.CredentialsResponse,
    //                 credentialRequest.CredentialsResponseValidation)
    //             .Select(credential => new BlindedCredential(credential))
    //             .ToArray();
    //         foreach (var credential in credentials)
    //         {
    //             SpentCredentials.Add(credential.Mac.Serial());
    //             
    //             _logger.LogInformation($"Removing credential {credential.Value}");
    //         }
    //
    //         foreach (var credential in newCredentials)
    //         {
    //             _logger.LogInformation($"Adding credential {credential.Value}");
    //             AllCredentials.TryAdd(credential.Mac.Serial(), credential);
    //         }
    //
    //         
    //
    //         var identity = new KompaktorIdentity(api, null, [txOut],
    //             newCredentials.Select(credential => credential.Mac).ToArray(), null, true);
    //         Identities.Add(identity);
    //         ShouldSign();
    //     }
    //     catch (Exception e)
    //     {
    //         _logger.LogException("Failed to register output", e);
    //         FailedOutputs.Add(txOut);
    //     }
    // }

    private async Task RegisterCoins()
    {
        await Task.WhenAll(AllocatedSelectedCoins.Keys.Select(RegisterCoin));
    }

    private async Task RegisterOutputs()
    {
        if (Round.Status != KompaktorStatus.OutputRegistration)
            return;
        if (RemainingPlannedOutputs().Length == 0)
            return;
        _logger.LogInformation($"Registering {RemainingPlannedOutputs().Length} outputs ");
        var currentAmounts = AvailableCredentials.Select(credential => credential.Value).ToList();

        var plannedAmounts = RemainingPlannedOutputs()
            .Select(txOut => (txOut.EffectiveCost(Round.RoundEventCreated.FeeRate).Satoshi, txOut)).ToList();
        //if there is change, add to plannedAmounts
        var targetValues = plannedAmounts.Select(x => x.Item1).ToList();
        var originalPlannedAmounts = plannedAmounts.Count;
        var change = currentAmounts.Sum() - targetValues.Sum();
        if (change > 0)
        {
            targetValues.Add(change);
        }

        var dependencyGraph =
            DependencyGraph2.Compute(_logger, currentAmounts.ToArray(), targetValues.ToArray(), new Range(2, 2),
                new Range(2, 2));

        _logger.LogInformation(
            $"computed {dependencyGraph.CountDescendants()} actions with {dependencyGraph.GetMaxDepth()} depth ");
        _logger.LogDebug(dependencyGraph.GenerateAscii(targetValues.ToArray()));

        HashSet<string> reserved = new();

        async Task IssuanceTask(DependencyGraph2.Node node, CancellationToken cancellationToken)
        {
            var api = _factory.Create();
            var requiredIns = node.Ins.Select(output => output.Amount).ToList();
            var creds = new List<BlindedCredential>();
            while (requiredIns.Any() && !cancellationToken.IsCancellationRequested)
            {
                var cred = AvailableCredentials.First(credential =>
                    requiredIns.Contains(credential.Value) &&
                    !reserved.Contains(credential.Mac.Serial()));
                if (reserved.Add(cred.Mac.Serial()))
                {
                    creds.Add(cred);
                    requiredIns.Remove(cred.Value);
                }
            }

            var credentialClient = GetWabiSabiClient(CredentialType.Amount);

            var credentialsRequest =
                credentialClient.CreateRequest(node.Outs.Select(output => output.Amount), creds, cancellationToken);
            var credRequest = new System.Collections.Generic.Dictionary<CredentialType, ICredentialsRequest>()
            {
                {CredentialType.Amount, credentialsRequest.CredentialsRequest}
            };
            if (node.OutputRegistered is not null)
            {
                var txOut = plannedAmounts.First(kvp => kvp.Satoshi == node.OutputRegistered.Amount);
                plannedAmounts.Remove(txOut);
                _logger.LogInformation(
                    $"Issuing {node.Id} through output registration of [{txOut.txOut.ScriptPubKey.GetDestinationAddress(_network)} {txOut.txOut.Value}] ({string.Join(", ", creds.Select(cred => cred.Value))} to {string.Join(", ", node.Outs.Select(output => output.Amount))}");

                var response = await api.RegisterOutput(new RegisterOutputRequest(credRequest, txOut.txOut));

                var credentialResponse = credentialClient.HandleResponse(response.Credentials[CredentialType.Amount],
                        credentialsRequest.CredentialsResponseValidation)
                    .Select(credential => new BlindedCredential(credential)).ToArray();

                foreach (var blindedCredential in credentialResponse)
                {
                    AllCredentials.TryAdd(blindedCredential.Mac.Serial(), blindedCredential);
                }

                Identities.Add(new KompaktorIdentity(api,
                    null,
                    new[] {response.Request.Output},
                    creds.Select(credential => credential.Mac).ToArray(),
                    credentialResponse.Select(credential => credential.Mac).ToArray(),
                    null, false));
            }
            else
            {
                _logger.LogInformation(
                    $"Issuing {string.Join(", ", creds.Select(cred => cred.Value))} to {string.Join(", ", node.Outs.Select(output => output.Amount))}");

                var response = await api.ReissueCredentials(new CredentialReissuanceRequest(credRequest));

                var credentialResponse = credentialClient.HandleResponse(response.Credentials[CredentialType.Amount],
                        credentialsRequest.CredentialsResponseValidation)
                    .Select(credential => new BlindedCredential(credential)).ToArray();

                foreach (var blindedCredential in credentialResponse)
                {
                    AllCredentials.TryAdd(blindedCredential.Mac.Serial(), blindedCredential);
                }

                Identities.Add(new KompaktorIdentity(api,
                    null,
                    null,
                    creds.Select(credential => credential.Mac).ToArray(),
                    credentialResponse.Select(credential => credential.Mac).ToArray(),
                    null, false));
            }
        }

        var timeLeft = Round.OutputPhaseEnd - TimeSpan.FromSeconds(20) - DateTimeOffset.UtcNow;
        var reissuanceExpiration = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(timeLeft.TotalMilliseconds / 2);


        await dependencyGraph.Reissue(reissuanceExpiration, _random, _logger, IssuanceTask, _cts.Token);
        _logger.LogInformation($"Finished registering {originalPlannedAmounts-plannedAmounts.Count}/{originalPlannedAmounts} outputs ({RegisteredOutputs.Count()}total registered outputs");
    }

    private async Task ReadyToSign()
    {
        var toSignal = Identities.Where(identity => identity.RegisteredInputs.Any() && !identity.SignalledReady)
            .ToList();
        if (toSignal.Count == 0)
            return;

        var subset = !ShouldSign();
        var expiry = Round.OutputPhaseEnd - TimeSpan.FromSeconds(10);
        if (subset)
        {
            if (toSignal.Count == 1)
                return;
            _logger.LogInformation("signalling to sign all but one");
            toSignal.RemoveAt(Random.Shared.Next(toSignal.Count - 1));
            var timeLeft = Round.OutputPhaseEnd - TimeSpan.FromSeconds(20) - DateTimeOffset.UtcNow;
            var reissuanceExpiration =
                DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(timeLeft.TotalMilliseconds / 2);


            expiry = reissuanceExpiration;
        }
        else
        {
            _logger.LogInformation("Signalling to sign all");
        }

        var tasks = toSignal.Select<KompaktorIdentity, Func<Task>>(
            identity =>
            {
                return async () =>
                {
                    _logger.LogInformation($"Signalling {identity.Secret}");
                    await identity.Api.ReadyToSign(new ReadyToSignRequest(identity.Secret));
                    identity.SignalledReady = true;

                    _logger.LogInformation($"Signalled {identity.Secret}");
                };
            }).ToArray();

        await TaskScheduler.Schedule("signal ready to sign", tasks
            ,
            expiry, _random, _cts.Token, _logger);
        _logger.LogInformation("Finished signalling ready to sign");
    }

    private async Task CatchException(string name, Func<Task> task)
    {
        try
        {
            await task();
        }
        catch (Exception e) when (e is not TaskCanceledException || !_cts.IsCancellationRequested)
        {
            _logger.LogException(name, e);
        }
    }
}