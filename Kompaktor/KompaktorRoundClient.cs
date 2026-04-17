using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Threading.Channels;
using Kompaktor.Behaviors;
using Kompaktor.Contracts;
using Kompaktor.Credentials;
using Kompaktor.Mapper;
using Kompaktor.Models;
using Kompaktor.Errors;
using Kompaktor.Utils;
using Microsoft.Extensions.Logging;
using NBitcoin;
using WabiSabi.CredentialRequesting;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Randomness;
using WabiSabi.Crypto.ZeroKnowledge;
using WabiSabi.CredentialRequesting;
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
    public readonly ILogger Logger;
    private readonly CancellationTokenSource _cts;
    private readonly IKompaktorRoundApiFactory _factory;
    private readonly WasabiRandom _random;
    private readonly Network _network;
    public readonly KompaktorRound Round;
    private readonly Channel<KompaktorStatus> _statusChannel;
    private readonly IRoundHistoryTracker? _roundHistoryTracker;

    public KompaktorRoundClient(
        WasabiRandom random,
        Network network,
        KompaktorRound round,
        IKompaktorRoundApiFactory factory,
        List<KompaktorClientBaseBehaviorTrait> behaviorTraits,
        IKompaktorWalletInterface walletInterface,
        ILogger logger,
        IRoundHistoryTracker? roundHistoryTracker = null)
    {
        _random = random;
        _network = network;
        Round = round;
        _factory = factory;
        _behaviorTraits = behaviorTraits;
        _walletInterface = walletInterface;
        Logger = logger;
        _roundHistoryTracker = roundHistoryTracker;


        foreach (var behaviorTrait in behaviorTraits) behaviorTrait.Start(this);
        Logger.LogInformation($"{_behaviorTraits.Count} behavior traits started");
        _statusChannel =
            Channel.CreateBounded<KompaktorStatus>(Enum.GetValues<KompaktorStatus>().Length);
        _cts = new CancellationTokenSource();
        // round.NewEvent
        // _ = HandleEvents(round.Subscribe(_cts.Token));
        round.NewEvent += RoundOnNewEvent;
        StatusChanged += OnStatusChanged;
        PhasesTask = ProcessStatuses();
    }

    private Task RoundOnNewEvent(object sender, KompaktorRoundEvent roundEvent)
    {
        if (roundEvent is KompaktorRoundEventStatusUpdate statusUpdate)
            StatusChanged?.Invoke(this, statusUpdate.Status);
        return Task.CompletedTask;
    }

    public Task PhasesTask { get; }

    // public ConcurrentMultiDictionary<OutPoint, MAC> CoinToCredentials { get; set; } = new();

    public ConcurrentDictionary<string, BlindedCredential> AllCredentials { get; set; } = new();

    public HashSet<string> SpentCredentials => Identities
        .SelectMany(identity => identity.SpentCredentials.Select(mac => mac.Serial()))
        .Concat(AllocatedCredentials.Keys).ToHashSet();

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

    public ImmutableArray<Coin> CoinsToRegister =>
    [
        ..AllocatedSelectedCoins.Keys
            .ExceptBy(RegisteredInputs, coin => coin.Outpoint)
            .Except(RejectedCoins)
    ];

    public ConcurrentDictionary<Coin, KompaktorClientBaseBehaviorTrait?> AllocatedSelectedCoins { get; } = new();
    public ConcurrentDictionary<string, KompaktorClientBaseBehaviorTrait> AllocatedCredentials { get; } = new();

    public OutPoint[] RegisteredInputs => Identities.Where(identity => identity.RegisteredInputs?.Any() is true)
        .SelectMany(identity => identity.RegisteredInputs).ToArray()!;

    public ImmutableArray<Coin>? RegisteredCoins =>
        CoinCandidates?.Where(coin => RegisteredInputs.Contains(coin.Outpoint)).ToImmutableArray();

    public BlindedCredential[] AvailableCredentialsForTrait(KompaktorClientBaseBehaviorTrait trait)
    {
        var outpoints = AllocatedSelectedCoins.Where(pair => pair.Value == trait)
            .Select(pair => pair.Key.Outpoint).ToArray();
        var potentialCredentials = Identities
            .Where(identity => identity.RegisteredInputs.Any(input => outpoints.Contains(input)))
            .SelectMany(identity => identity.CreatedCredentials.Select(mac => mac.Serial())).ToArray();

        return AvailableCredentials.Where(credential => potentialCredentials.Contains(credential.Mac.Serial()))
            .ToArray();

        //
        // var traitCoins = AllocatedSelectedCoins.Where(kvp => kvp.Value == trait || kvp.Value is null)
        //     .Select(kvp => kvp.Key.Outpoint!).ToArray();
        //
        // Dictionary<Coin, BlindedCredential> CoinToCredentials = new();
        // foreach (var traitCoin in traitCoins)
        // {
        //     var creds = Identities.FirstOrDefault(identity => identity.RegisteredInputs?.Contains(traitCoin) is true);
        //     if (creds is not null)
        //     {
        //         var outpoint = creds.RegisteredInputs.First(input => input == traitCoin);
        //         var coin = RegisteredCoins.Value.First(coin => coin.Outpoint == outpoint);
        //         var closestCredAmt = coin.EstimateEffectiveValue(Round.RoundEventCreated.FeeRate);
        //         var createdCredentialsSerials = creds.CreatedCredentials.Select(mac => mac.Serial()).ToArray();
        //         var createdCredentials = AllCredentials.Where(pair => createdCredentialsSerials.Contains(pair.Key))
        //             .Select(pair => pair.Value).ToArray();
        //         var closestCred = //select a credential from createdCredentials with the closest value to closestCredAmt
        //             createdCredentials.OrderBy(credential => Math.Abs(credential.Value - closestCredAmt)).First();
        //         //check if there is a 0 credential we can take as well
        //         
        //
        //         CoinToCredentials.Add(coin, closestCred);
        //     }
        // }

        // return CoinToCredentials.Values.ToArray();
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
        Round.NewEvent -= RoundOnNewEvent;
        StatusChanged = null;
    }


    private readonly ConcurrentDictionary<CredentialType, ICredentialClient> _createdClients = new();

    private ICredentialClient GetCredentialClient(CredentialType credentialType)
    {
        if (_createdClients.TryGetValue(credentialType, out var client))
        {
            return client;
        }

        if (!Round.RoundEventCreated.Credentials.TryGetValue(credentialType, out var credentialConfiguation))
        {
            throw new InvalidOperationException($"No issuer parameters for credential type {credentialType}");
        }

        var k = credentialConfiguation.IssuanceOut.Max;
        ICredentialClient newClient = credentialConfiguation.UseBulletproofs
            ? new BulletproofWabiSabiClient(credentialConfiguation.Parameters, new BulletproofPlusPlusRangeProof(), _random, k)
            : new WabiSabiClient(credentialConfiguation.Parameters, _random, credentialConfiguation.Max, k);

        return _createdClients[credentialType] = newClient;
    }

    private async Task OnStatusChanged(object sender, KompaktorStatus e)
    {
        await _statusChannel.Writer.WriteAsync(e);
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

            Logger.LogInformation($"Processing phase {phase}");
            if (Round.Status != phase)
                continue;
            switch (phase)
            {
                case KompaktorStatus.InputRegistration:
                    // Check if too many consecutive failures suggest a malicious coordinator
                    if (_roundHistoryTracker?.ShouldBackOff() == true)
                    {
                        Logger.LogError(
                            $"Too many consecutive round failures ({_roundHistoryTracker.ConsecutiveFailures}). " +
                            "Possible intersection attack — backing off.");
                        exit = true;
                        break;
                    }

                    var remainingMs = (int)Math.Max(0, (Round.InputPhaseEnd - DateTimeOffset.UtcNow).TotalMilliseconds);
                    var maxPreRegDelay = Math.Min(3000, remainingMs / 4);
                    var preRegDelay = maxPreRegDelay > 0 ? _random.GetInt(0, maxPreRegDelay) : 0;
                    if (preRegDelay > 0)
                    {
                        Logger.LogInformation($"Pre-registration delay: {preRegDelay}ms");
                        await Task.Delay(preRegDelay, _cts.Token);
                        if (Round.Status != KompaktorStatus.InputRegistration)
                            break;
                    }

                    // Verify round parameters over a separate circuit before committing inputs.
                    // Detects coordinator equivocation (serving different params to different clients).
                    await VerifyRoundConsistency();

                    CoinCandidates = (await _walletInterface.GetCoins())
                        .Where(coin => Round.RoundEventCreated.InputAmount.Contains(coin.Amount)).ToList();

                    // Exclude coins that would reveal new wallet cluster pairings
                    if (_roundHistoryTracker != null && CoinCandidates.Count > 0)
                    {
                        var toExclude = _roundHistoryTracker.GetCoinsToExclude(CoinCandidates);
                        if (toExclude.Count > 0)
                        {
                            Logger.LogInformation(
                                $"Excluding {toExclude.Count} coins to limit cross-round cluster disclosure");
                            CoinCandidates.RemoveAll(c => toExclude.Contains(c.Outpoint));
                        }
                    }

                    // Shuffle candidates so behavior traits don't deterministically pick the same
                    // coins across rounds. Without this, a malicious coordinator can predict which
                    // coins will be selected and use round parameter tuning to profile the wallet.
                    ShuffleCoinCandidates();
                    await StartCoinSelection.InvokeIfNotNullAsync(this);
                    await FinishedCoinSelection.InvokeIfNotNullAsync(this);
                    Logger.LogInformation($"Finished coin selection. Selected {AllocatedSelectedCoins.Count} coins");
                    await RegisterCoins();
                    await FinishedCoinRegistration.InvokeIfNotNullAsync(this);

                    Logger.LogInformation($"Finished coin registration. Registered {Identities.Count} identities");
                    // Establish persistent connections for all registered inputs
                    ConnectRegisteredInputs();
                    break;
                case KompaktorStatus.OutputRegistration:
                    Logger.LogInformation($"Starting output registration");
                    await StartOutputRegistration.InvokeIfNotNullAsync(this);
                    await RegisterOutputs();
                    await FinishedOutputRegistration.InvokeIfNotNullAsync(this);

                    Logger.LogInformation($"Finished intital output registration");
                    _ = TaskUtils.Loop(RegisterOutputs, () => Round.Status != KompaktorStatus.OutputRegistration,
                        Logger, "RegisterOutputs", _cts.Token);
                    _ = TaskUtils.Loop(ReadyToSign, () => Round.Status != KompaktorStatus.OutputRegistration,
                        Logger, "ReadyToSign", _cts.Token);
                    break;
                case KompaktorStatus.Signing:
                    await StartSigning.InvokeIfNotNullAsync(this);
                    await Sign();
                    break;
                case KompaktorStatus.Broadcasting:
                    break;
                case KompaktorStatus.Completed:
                    if (RegisteredInputs.Length > 0)
                        _roundHistoryTracker?.RecordSuccess();
                    exit = true;
                    break;
                case KompaktorStatus.Failed:
                    if (RegisteredInputs.Length > 0)
                        _roundHistoryTracker?.RecordFailedRound(RegisteredInputs);
                    exit = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            if (exit)
            {
                // Mark all output scripts we disclosed as burned so the wallet
                // won't reuse them in future rounds (prevents cross-round linking).
                // This applies to ALL terminal statuses — once the coordinator has
                // seen the mapping between input identity and output script, reusing
                // that script in a future round enables intersection attacks.
                var exposedScripts = RegisteredOutputs
                    .Select(o => o.ScriptPubKey)
                    .Distinct()
                    .ToList();
                if (exposedScripts.Count > 0)
                {
                    Logger.LogInformation($"Round ended ({Round.Status}) — marking {exposedScripts.Count} output scripts as exposed");
                    await _walletInterface.MarkScriptsExposed(exposedScripts);
                }
            }
        }
    }

    private string lstMsg = "";

    /// <summary>
    /// Checks if output registration is complete (all outputs registered, all credentials consumed)
    /// without checking kill switches. Used by receiver traits to avoid deadlocks in mesh scenarios
    /// where both sender and receiver traits exist on the same client.
    /// </summary>
    public bool IsOutputRegistrationComplete()
    {
        return RemainingPlannedOutputs().Length == 0 &&
               AvailableCredentials.Sum(credential => credential.Value) == 0;
    }

    public bool ShouldSign(bool verbose = false)
    {
        var ksnames = DoNotSignKillSwitches.Where(kvp => kvp.Value).Select(kvp => kvp.Key.GetType().Name);
        var msg = $"Should sign: " +
                  $"{DoNotSignKillSwitches.Count(kvp => kvp.Value)} kill switches ({string.Join(",", ksnames)}) on" +
                  $"{RemainingPlannedOutputs().Length} O left, {AvailableCredentials.Sum(credential => credential.Value)} C left";
        if (DoNotSignKillSwitches.Any(kvp => kvp.Value))
        {
            if (lstMsg != msg || verbose)
            {
                lstMsg = msg;
                Logger.LogInformation(msg);
            }

            return false;
        }

        // var identities = Identities.ToArray();
        // var outputs = identities.SelectMany(identity => identity.RegisteredOutputs).ToArray();
        // var inputs = identities.SelectMany(identity => identity.RegisteredInputs).ToArray();
        // var signalled = identities.Count(identity => identity.SignalledReady);

        //  var msg = $"Should sign: {signalled} signalled, {outputs.Length} outputs, {inputs.Length} inputs cred left: {AvailableCredentials.Sum(credential => credential.Value)}";
        // if (msg != lstMsg) 
        //  _logger.LogInformation(lstMsg);
        //  lstMsg = msg;
        var res = RemainingPlannedOutputs().Length == 0 &&
                  AvailableCredentials.Sum(credential => credential.Value) == 0;
        if (res)
        {
            msg = "SIGN!";
        }

        if (msg != lstMsg || verbose)
        {
            lstMsg = msg;
            Logger.LogInformation(msg);
        }

        return res;
        // _logger.LogInformation("SIGN!");
    }

    public Transaction? GetTransaction()
    {
        return Round.GetTransaction(_network);
    }

    private async Task Sign()
    {
        // Verify other participants' inputs exist in the UTXO set (if we have full node access)
        await VerifyInputUtxos();

        // Verify fee transparency before signing — ensure no hidden coordinator surplus
        var feeBreakdown = Round.GetFeeBreakdown();
        Logger.LogInformation(
            $"Fee audit: inputs={feeBreakdown.TotalInputs} outputs={feeBreakdown.TotalOutputs} " +
            $"fee={feeBreakdown.ActualFee} expected={feeBreakdown.ExpectedMiningFee} surplus={feeBreakdown.Surplus}");
        // Scale threshold with transaction size: each input/output contributes up to feeRate sats
        // of rounding error from per-element vs whole-transaction fee computation differences.
        var surplusThreshold = Money.Satoshis(546 + (Round.Inputs.Count + Round.Outputs.Count) * 3);
        if (feeBreakdown.HasExcessiveSurplus(surplusThreshold))
        {
            Logger.LogError(
                $"EXCESSIVE FEE SURPLUS: {feeBreakdown.Surplus} sats beyond expected mining fee. " +
                "Possible coordinator fee extraction. Refusing to sign.");
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var subsetSign = !ShouldSign();
        var inputIdentities = Identities
            .Where(identity => identity.RegisteredInputs?.Any() is true).ToList();
        if (subsetSign)
        {
            Logger.LogInformation("Subset signing");
            var notSignalled = inputIdentities.Where(identity => !identity.SignalledReady).ToList();
            switch (notSignalled.Count)
            {
                case 0 when inputIdentities.Count > 1:
                    inputIdentities.RemoveAt(Random.Shared.Next(inputIdentities.Count));
                    break;
                case > 0:
                    inputIdentities.Remove(notSignalled[Random.Shared.Next(notSignalled.Count)]);
                    break;
            }
        }

        var tx = GetTransaction();
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
        await TaskScheduler.Schedule("signing", tasks, expiry, _random, cts.Token, Logger);
    }


    public async Task<BlindedCredential[]> Generate0Credentials()
    {
        Logger.LogInformation("Generating 0 credentials");
        var client = GetCredentialClient(CredentialType.Amount);
        var api = _factory.Create();
        var credReq = client.CreateRequestForZeroAmount();
        var response = await api.ReissueCredentials(new CredentialReissuanceRequest(
            new Dictionary<CredentialType, ICredentialsRequest>()
            {
                {CredentialType.Amount, credReq.CredentialsRequest}
            }));
        var newCredentials = client.HandleResponse(response.Credentials[CredentialType.Amount],
                credReq.CredentialsResponseValidation)
            .Select(credential => new BlindedCredential(credential)).ToArray();
        var identity = new KompaktorIdentity(api, null, null, null,
            newCredentials.Select(credential => credential.Mac).ToArray(), null, false);
        Identities.Add(identity);
        foreach (var credential in newCredentials)
        {
            AllCredentials.TryAdd(credential.Mac.Serial(), credential);
        }

        return newCredentials;
    }

    public async Task<BlindedCredential[]> Reissue(Credential[] ins, long[] outs)
    {
        Logger.LogInformation(
            $"Reissuing {string.Join(", ", ins.Select(credential => credential.Value))} to {string.Join(", ", outs)}");
        var client = GetCredentialClient(CredentialType.Amount);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var api = _factory.Create();
        var credReq = client.CreateRequest(outs, ins, cts.Token);
        var response = await api.ReissueCredentials(new CredentialReissuanceRequest(
            new Dictionary<CredentialType, ICredentialsRequest>()
            {
                {CredentialType.Amount, credReq.CredentialsRequest}
            }));
        var newCredentials = client.HandleResponse(response.Credentials[CredentialType.Amount],
                credReq.CredentialsResponseValidation)
            .Select(credential => new BlindedCredential(credential)).ToArray();
        var identity = new KompaktorIdentity(api, null, null, ins.Select(credential => credential.Mac).ToArray(),
            newCredentials.Select(credential => credential.Mac).ToArray(), null, false);
        Identities.Add(identity);
        foreach (var credential in newCredentials)
        {
            AllCredentials.TryAdd(credential.Mac.Serial(), credential);
        }

        return newCredentials;
    }

    private async Task RegisterCoin(Coin coin)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            await RetryHelper.ExecuteWithRetryAsync(async () =>
            {
                var api = _factory.Create();

                var ownershipProof = await _walletInterface.GenerateOwnershipProof(Round.RoundEventCreated.RoundId, [coin]);

                var inputFee = ownershipProof.FundProofs.Sum(@in => coin.ScriptPubKey.EstimateFee(Round.RoundEventCreated.FeeRate));
                var expectedCredentialAmount = coin.Amount - inputFee;

                var credentialClient = GetCredentialClient(CredentialType.Amount);

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

                foreach (var credential in zeroCredentials)
                {
                    AllCredentials.TryAdd(credential.Mac.Serial(), new BlindedCredential(credential));
                }

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

                Identities.Add(newIdentity);
            }, maxRetries: 5, baseDelay: TimeSpan.FromSeconds(1), logger: Logger,
                operationName: $"RegisterCoin({coin.Outpoint})", cancellationToken: cts.Token);
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
    //         var credentialClient = GetCredentialClient(CredentialType.Amount);
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

    /// <summary>
    /// Verifies other participants' inputs against the UTXO set before signing.
    /// Detects fabricated inputs from a malicious coordinator that could be used
    /// to forge ownership proofs (especially for P2WPKH inputs that don't commit
    /// to all scriptPubKeys at signing time unlike P2TR).
    /// Only runs if the wallet supports UTXO verification (full node clients).
    /// </summary>
    private async Task VerifyInputUtxos()
    {
        var myOutpoints = RegisteredInputs.ToHashSet();
        var otherInputs = Round.Inputs.Where(c => !myOutpoints.Contains(c.Outpoint)).ToList();

        if (otherInputs.Count == 0)
            return;

        // Build lookup from OutPoint to registration event for BIP322 signature verification
        var registrationEvents = Round.GetEventsSince(null)
            .OfType<KompaktorRoundEventInputRegistered>()
            .ToDictionary(e => e.Coin.Outpoint, e => e);

        var invalidInputs = new List<OutPoint>();
        foreach (var coin in otherInputs)
        {
            var result = await _walletInterface.VerifyUtxo(coin.Outpoint, coin.TxOut);
            if (result == false)
            {
                invalidInputs.Add(coin.Outpoint);
                Logger.LogError("UTXO verification failed for input {Outpoint} — claimed {Amount} at {Script}",
                    coin.Outpoint, coin.Amount, coin.ScriptPubKey);
                continue;
            }

            // Verify BIP322 ownership proof
            if (!registrationEvents.TryGetValue(coin.Outpoint, out var regEvent))
            {
                invalidInputs.Add(coin.Outpoint);
                Logger.LogError("No registration event found for input {Outpoint} — cannot verify ownership proof",
                    coin.Outpoint);
                continue;
            }

            var address = coin.ScriptPubKey.GetDestinationAddress(_network);
            if (address is null ||
                !address.VerifyBIP322(Round.RoundEventCreated.RoundId, regEvent.QuoteRequest.Signature, [coin]))
            {
                invalidInputs.Add(coin.Outpoint);
                Logger.LogError("BIP322 ownership proof verification failed for input {Outpoint}",
                    coin.Outpoint);
            }
        }

        if (invalidInputs.Count > 0)
        {
            Logger.LogError(
                "REFUSING TO SIGN: {Count} inputs failed verification. " +
                "Coordinator may be injecting fabricated inputs.", invalidInputs.Count);
            throw new Errors.KompaktorProtocolException(
                Errors.KompaktorProtocolErrorCode.InputNotValid,
                $"{invalidInputs.Count} inputs failed verification",
                Round.RoundEventCreated.RoundId);
        }

        Logger.LogInformation("UTXO and BIP322 ownership verification passed for {Count} other participants' inputs",
            otherInputs.Count);
    }

    /// <summary>
    /// Fisher-Yates shuffle of coin candidates to prevent deterministic selection
    /// that a malicious coordinator could exploit to profile the wallet.
    /// </summary>
    private void ShuffleCoinCandidates()
    {
        if (CoinCandidates is null || CoinCandidates.Count <= 1)
            return;
        for (var i = CoinCandidates.Count - 1; i > 0; i--)
        {
            var j = _random.GetInt(0, i + 1);
            (CoinCandidates[i], CoinCandidates[j]) = (CoinCandidates[j], CoinCandidates[i]);
        }
    }

    /// <summary>
    /// Queries the round info endpoint over a separate isolated circuit and compares
    /// the returned parameters against the local round state. If any fields differ,
    /// the coordinator is equivocating (serving different parameters to different clients)
    /// and the client must abort before registering inputs.
    /// </summary>
    private async Task VerifyRoundConsistency()
    {
        IKompaktorRoundApi? verificationApi = null;
        try
        {
            verificationApi = _factory.Create();
            var remoteInfo = await verificationApi.GetRoundInfo();
            var mismatches = remoteInfo.CompareWith(Round.RoundEventCreated);

            if (mismatches.Count > 0)
            {
                var fields = string.Join(", ", mismatches);
                Logger.LogError(
                    "EQUIVOCATION DETECTED: Round {RoundId} parameters differ across circuits. Mismatched fields: {Fields}",
                    Round.RoundEventCreated.RoundId, fields);
                throw new KompaktorProtocolException(
                    KompaktorProtocolErrorCode.EquivocationDetected,
                    $"Coordinator equivocation detected — round parameters differ across circuits: {fields}",
                    Round.RoundEventCreated.RoundId);
            }

            Logger.LogInformation("Round consistency verified over separate circuit");
        }
        catch (KompaktorProtocolException)
        {
            throw; // Re-throw equivocation errors
        }
        catch (Exception e)
        {
            // Network errors on the verification circuit are suspicious but not conclusive.
            // Log a warning but allow the round to proceed — a truly malicious coordinator
            // would serve plausible-looking (but different) data, not refuse to respond.
            Logger.LogException("Round consistency check failed (network error on verification circuit)", e);
        }
        finally
        {
            if (verificationApi is IDisposable disposable)
                disposable.Dispose();
        }
    }

    /// <summary>
    /// Establishes persistent connections for all identities that registered inputs.
    /// The connection stays open until the round completes or the client disposes.
    /// </summary>
    private void ConnectRegisteredInputs()
    {
        var toConnect = Identities
            .Where(identity => identity.RegisteredInputs?.Any() is true && identity.Secret is not null)
            .ToList();

        if (toConnect.Count == 0)
        {
            Logger.LogInformation("No connections to establish (no registered inputs)");
            return;
        }

        foreach (var identity in toConnect)
        {
            try
            {
                identity.Api.Connect(identity.Secret!);
            }
            catch (Exception e)
            {
                Logger.LogException($"Failed to connect for {identity.Secret}", e);
            }
        }
        Logger.LogInformation($"Established {toConnect.Count} persistent connections");
    }

    private async Task RegisterCoins()
    {
        if (Round.Status != KompaktorStatus.InputRegistration)
            return;
        var coinsToRegister = CoinsToRegister;
        if (coinsToRegister.Length == 0)
            return;

        Logger.LogInformation($"Registering {coinsToRegister.Length} inputs ");
        await Task.WhenAll(AllocatedSelectedCoins.Keys.Select(RegisterCoin));
    }

    
    
    class TxOutWrapper
    {
        public long Satoshi { get; }
        public TxOut TxOut { get; }
        private int _isUsed; // Use an int as a flag (0 = false, 1 = true)

        public TxOutWrapper(long satoshi, TxOut txOut)
        {
            Satoshi = satoshi;
            TxOut = txOut;
            _isUsed = 0;
        }

        public bool TryMarkAsUsed()
        {
            // Atomically set _isUsed to 1 if it's currently 0
            return Interlocked.CompareExchange(ref _isUsed, 1, 0) == 0;
        }

        public bool IsUsed => _isUsed == 1;
    }
    
    private async Task RegisterOutputs()
    {
        
        
        
        try
        {
            await OutputRegistrationLock.WaitAsync(_cts.Token);

            if (Round.Status != KompaktorStatus.OutputRegistration)
                return;
            if (RemainingPlannedOutputs().Length == 0)
            {
                // No planned outputs, but we may still need to drain leftover credentials
                // from interactive payment senders who fell back to non-interactive mode.
                await DrainLeftoverCredentials();
                return;
            }
            Logger.LogInformation($"Registering {RemainingPlannedOutputs().Length} outputs ");
            var currentAmounts = AvailableCredentials.Select(credential => credential.Value).ToList();

            var plannedAmounts = new ConcurrentBag<TxOutWrapper>(
                RemainingPlannedOutputs()
                    .Select(txOut => new TxOutWrapper(txOut.EffectiveCost(Round.RoundEventCreated.FeeRate).Satoshi, txOut))
            );

            //if there is change, add to plannedAmounts
            var targetValues = plannedAmounts.Select(x => x.Satoshi).ToList();
            var originalPlannedAmounts = plannedAmounts.Count;

            var credentialConfiguration = Round.RoundEventCreated.Credentials[CredentialType.Amount];
            if (currentAmounts.Count < credentialConfiguration.IssuanceIn.Min)
            {
                var zeroCredentials = await Generate0Credentials();
                currentAmounts.AddRange(zeroCredentials.Select(credential => credential.Value));
            }

            var dependencyGraph =
                DependencyGraph2.Compute(Logger, currentAmounts.ToArray(), targetValues.ToArray(),
                    credentialConfiguration.IssuanceIn, credentialConfiguration.IssuanceOut);

            Logger.LogInformation(
                $"computed {dependencyGraph.CountDescendants()} actions with {dependencyGraph.GetMaxDepth()} depth ");
            Logger.LogDebug(dependencyGraph.GenerateAscii(targetValues.ToArray()));

            ConcurrentHashSet<string> reserved = new();

            bool TryGetMatchingTxOut(long amt, out TxOut matchingTxOut)
            {
                matchingTxOut = null;

                foreach (var item in plannedAmounts)
                {
                    if (item.Satoshi == amt && item.TryMarkAsUsed())
                    {
                        matchingTxOut = item.TxOut;
                        return true;
                    }
                }

                return false;
            }
            
            async Task IssuanceTask(DependencyGraph2.Node node, CancellationToken cancellationToken)
            {
                var api = _factory.Create();
                var requiredIns = node.Ins.Select(output => output.Amount).ToList();
                var creds = new List<BlindedCredential>();
                // Snapshot available credentials once per node instead of rebuilding
                // SpentCredentials + filtering AllCredentials on every iteration.
                var available = AvailableCredentials;
                foreach (var cred in available)
                {
                    if (!requiredIns.Any())
                        break;
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    if (!requiredIns.Contains(cred.Value))
                        continue;
                    if (!reserved.Add(cred.Mac.Serial()))
                        continue;
                    creds.Add(cred);
                    requiredIns.Remove(cred.Value);
                }

                try
                {
                    if (node.OutputRegistered is not null)
                    {
                        //get and remove the txOut from plannedAmounts as long as it matches the amt, in a concurrent safe way

                        if (!TryGetMatchingTxOut( node.OutputRegistered.Amount, out var txOut))
                        {

                            throw new InvalidOperationException("Could not find matching txOut");
                        }

                    Logger.LogInformation(
                        $"Issuing {node.Id} through output registration of [{txOut.ScriptPubKey.GetDestinationAddress(_network)} {txOut.Value}] ({string.Join(", ", creds.Select(cred => cred.Value))} to {string.Join(", ", node.Outs.Select(output => output.Amount))}");

                    var credentialClient = GetCredentialClient(CredentialType.Amount);
                    Logger.LogInformation(
                        $"Issuing {string.Join(", ", creds.Select(cred => cred.Mac.Serial()))} to {string.Join(", ", node.Outs.Select(output => output.Amount))}");
                    var credentialsRequest =
                        credentialClient.CreateRequest(node.Outs.Select(output => output.Amount), creds, cancellationToken);
                    var credRequest = new System.Collections.Generic.Dictionary<CredentialType, ICredentialsRequest>()
                    {
                        {CredentialType.Amount, credentialsRequest.CredentialsRequest}
                    };

                    var response = await api.RegisterOutput(new RegisterOutputRequest(credRequest, txOut));

                    var credentialResponse = credentialClient.HandleResponse(
                            response.Credentials[CredentialType.Amount],
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
                    Logger.LogInformation(
                        $"Issuing {string.Join(", ", creds.Select(cred => cred.Value))} to {string.Join(", ", node.Outs.Select(output => output.Amount))}");

                    await Reissue(creds.ToArray(), node.Outs.Select(output => output.Amount).ToArray());
                }
                }
                catch (Exception e)
                {
                    Logger.LogException("Failed to issue", e);
                    throw;
                }
                Logger.LogInformation(
                    $"Finished issuing {string.Join(", ", creds.Select(cred => cred.Value))} to {string.Join(", ", node.Outs.Select(output => output.Amount))}");
            }

            var timeLeft = Round.OutputPhaseEnd - TimeSpan.FromSeconds(20) - DateTimeOffset.UtcNow;
            var reissuanceExpiration =
                DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(timeLeft.TotalMilliseconds / 2);


            await dependencyGraph.Reissue(reissuanceExpiration, _random, Logger, IssuanceTask, _cts.Token);

            // Drain any leftover credential value after all planned outputs are handled
            if (RemainingPlannedOutputs().Length == 0)
            {
                await DrainLeftoverCredentials();
            }

            Logger.LogInformation(
                $"Finished registering {plannedAmounts.Count(wrapper =>wrapper.IsUsed)}/{originalPlannedAmounts} outputs ({RegisteredOutputs.Count()}total registered outputs) ss: {ShouldSign(true)}");
            await ReadyToSign();
        }
        finally
        {
            OutputRegistrationLock.Release();
        }
    }

    /// <summary>
    /// Drains leftover non-zero credentials to zero. Called when all planned outputs are registered
    /// but credential value remains (e.g., from interactive payment senders that fell back to
    /// non-interactive mode, leaving the receiver with excess credentials).
    /// </summary>
    private async Task DrainLeftoverCredentials()
    {
        // Only drain if no kill switches are active — otherwise a behavior trait
        // (like SelfSendChangeBehaviorTrait) may still need the credentials for change.
        if (DoNotSignKillSwitches.Any(kvp => kvp.Value))
            return;

        var credentialConfiguration = Round.RoundEventCreated.Credentials[CredentialType.Amount];
        var leftoverCreds = AvailableCredentials.Where(c => c.Value > 0).ToList();
        while (leftoverCreds.Count > 0 && !_cts.Token.IsCancellationRequested)
        {
            var batch = leftoverCreds.Take(credentialConfiguration.IssuanceIn.Max).ToList();
            leftoverCreds.RemoveRange(0, batch.Count);

            while (batch.Count < credentialConfiguration.IssuanceIn.Min)
            {
                var zeros = await Generate0Credentials();
                batch.AddRange(zeros.Take(credentialConfiguration.IssuanceIn.Min - batch.Count));
            }

            var zeroOuts = Enumerable.Range(0, credentialConfiguration.IssuanceOut.Max).Select(_ => 0L).ToArray();
            await Reissue(batch.Select(c => (Credential)c).ToArray(), zeroOuts);
            Logger.LogInformation($"Drained {batch.Sum(c => c.Value)} sats of leftover credentials to zero");

            leftoverCreds = AvailableCredentials.Where(c => c.Value > 0).ToList();
        }
    }

    public readonly SemaphoreSlim ReadyToSignLock = new(1, 1);
    public readonly SemaphoreSlim OutputRegistrationLock = new(1, 1);

    private async Task ReadyToSign()
    {
        try
        {
            await ReadyToSignLock.WaitAsync(_cts.Token);

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
                Logger.LogInformation("signalling to sign all but one");
                toSignal.RemoveAt(Random.Shared.Next(toSignal.Count));
                var timeLeft = Round.OutputPhaseEnd - TimeSpan.FromSeconds(20) - DateTimeOffset.UtcNow;
                var reissuanceExpiration =
                    DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(timeLeft.TotalMilliseconds / 2);


                expiry = reissuanceExpiration;
            }
            else
            {
                Logger.LogInformation("Signalling to sign all");
            }

            var tasks = toSignal.Select<KompaktorIdentity, Func<Task>>(
                identity =>
                {
                    return async () =>
                    {
                        await identity.Api.ReadyToSign(new ReadyToSignRequest(identity.Secret));
                        identity.SignalledReady = true;

                    };
                }).ToArray();

            await TaskScheduler.Schedule("signal ready to sign", tasks
                ,
                expiry, _random, _cts.Token, Logger);
            Logger.LogInformation("Finished signalling ready to sign");
        }
        finally
        {
            ReadyToSignLock.Release();
        }
    }
}