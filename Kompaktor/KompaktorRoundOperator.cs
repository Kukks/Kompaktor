using System.Collections.Concurrent;
using Kompaktor.Contracts;
using Kompaktor.Credentials;
using Kompaktor.Errors;
using Kompaktor.Models;
using Kompaktor.Prison;
using Kompaktor.Utils;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.RPC;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Randomness;

namespace Kompaktor;

public class KompaktorRoundOperator : KompaktorRound, IKompaktorRoundApi
{
    private readonly Network _network;
    private readonly WasabiRandom _random;
    private readonly ILogger _logger;
    private readonly RPCClient _rpcClient;
    private readonly KompaktorPrison? _prison;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _statusSemaphore = new(1, 1);
    private volatile bool _inputSoftTimeoutReached;

    public KompaktorRoundOperator(Network network, RPCClient rpcClient, WasabiRandom random, ILogger logger, KompaktorPrison? prison = null)
    {
        _network = network;
        _rpcClient = rpcClient;
        _random = random;
        _logger = logger;
        _prison = prison;
        _cts = new CancellationTokenSource();
        NewEvent += HandleNewEvents;
    }

    private async Task HandleNewEvents(object sender, KompaktorRoundEvent roundEvent)
    {
        if (roundEvent is KompaktorRoundEventStatusUpdate statusUpdate)
        {
            await HandleStatusChange(statusUpdate.Status);
        }
        else if (Status == KompaktorStatus.InputRegistration &&
                 roundEvent is KompaktorRoundEventInputRegistered &&
                 _inputSoftTimeoutReached &&
                 RoundEventCreated.InputCount.Contains(Inputs.Count))
        {
            _logger.LogInformation("Soft timeout reached and minimum inputs met ({InputCount}), transitioning early",
                Inputs.Count);
            await UpdateStatus(KompaktorStatus.ConnectionConfirmation);
        }
        else if (Status == KompaktorStatus.OutputRegistration && NotReadyToSign.IsEmpty)
        {
            await UpdateStatus(KompaktorStatus.Signing);
        }
        else if (Status == KompaktorStatus.Signing && roundEvent is KompaktorRoundEventSignaturePosted &&
                 SignatureCount == Inputs.Count)
        {
            await UpdateStatus(KompaktorStatus.Broadcasting);
        }
    }

    /// <summary>
    /// Fired when signing fails and a blame round should be created.
    /// Contains the outpoints of participants who DID sign (honest participants).
    /// </summary>
    public event Func<string, HashSet<OutPoint>, Task>? BlameRoundRequested;

    public Dictionary<CredentialType, ICredentialIssuer> CredentialIssuers { get; private set; } = new();

    public readonly ConcurrentDictionary<string, (RegisterInputQuoteRequest quoteRequest, InputRegistrationQuoteResponse response, Coin coin)> ActiveQuotes = new();

    public Task<KompaktorRoundEvent> GetEvents(string lastEventId)
    {
        throw new NotImplementedException("GetEvents is not yet implemented");
    }

    public async Task<KompaktorRoundEventMessage> SendMessage(MessageRequest request)
    {
        if (Status > KompaktorStatus.Signing)
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.MessagingNotAllowed,
                "Round status does not allow sending messages");
        return await AddEvent(new KompaktorRoundEventMessage(request));
    }

    public async Task<InputRegistrationQuoteResponse> PreRegisterInput(RegisterInputQuoteRequest quoteRequest)
    {
        if (Status != KompaktorStatus.InputRegistration)
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.WrongPhase,
                "Round is not in input registration phase");

        if (quoteRequest.CredentialsRequest.Delta != 0)
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InvalidCredentialRequest,
                "Amount credential must be 0 for a quote");

        var txIn = quoteRequest.Signature.FundProofs[0];

        // Check if coin is banned
        if (_prison is not null && _prison.IsBanned(txIn.PrevOut))
        {
            var ban = _prison.GetBan(txIn.PrevOut);
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InputBanned,
                $"Coin is banned until {ban?.ExpiresAt:u} (reason: {ban?.Reason})");
        }

        if (Inputs.Any(coin => coin.Outpoint == txIn.PrevOut))
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InputAlreadyRegistered,
                "Coin already registered");

        // Blame round: only whitelisted inputs allowed
        if (RoundEventCreated is { IsBlameRound: true, BlameWhitelist: not null } &&
            !RoundEventCreated.BlameWhitelist.Contains(txIn.PrevOut))
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InputNotWhitelisted,
                "Input not whitelisted for this blame round");

        var txOutStatus = await _rpcClient.GetTxOutAsync(txIn.PrevOut.Hash, (int)txIn.PrevOut.N);

        if (txOutStatus is null or { Confirmations: < 1 } or { Confirmations: < 100, IsCoinBase: true })
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InputNotValid,
                "Coin not valid");

        var coin = new Coin(txIn.PrevOut, new TxOut(txOutStatus.TxOut.Value, txOutStatus.TxOut.ScriptPubKey));

        if (!RoundEventCreated.InputAmount.Contains(coin.Amount))
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InputAmountOutOfRange,
                "Input amount not allowed");
        if (Inputs.Count >= RoundEventCreated.InputCount.Max)
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InputCountExceeded,
                "Too many inputs in this round");

        if (!txOutStatus.TxOut.ScriptPubKey.GetDestinationAddress(_network)!
                .VerifyBIP322(RoundEventCreated.RoundId, quoteRequest.Signature, [coin]))
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InvalidOwnershipProof,
                "Invalid signature");

        var feeRate = RoundEventCreated.FeeRate;
        var inputFee = txIn.GetFee(feeRate);
        var credentialAmount = txOutStatus.TxOut.Value - inputFee;

        if (credentialAmount.Satoshi <= 0)
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InsufficientInputFee,
                "Amount credential is too small to be issued");

        var credentialsResponse =
            await CredentialIssuers[CredentialType.Amount]
                .HandleRequestAsync(quoteRequest.CredentialsRequest, _cts.Token);

        var secret = Convert.ToHexString(_random.GetBytes(32)).ToLower();

        ActiveQuotes[secret] = (quoteRequest,
            new InputRegistrationQuoteResponse(secret, credentialsResponse, credentialAmount), coin);
        return ActiveQuotes[secret].response;
    }

    public ConcurrentDictionary<string, OutPoint> NotReadyToSign { get; set; } = new();

    /// <summary>Tracks which input registration secrets have confirmed their connection.</summary>
    private readonly ConcurrentDictionary<string, OutPoint> _confirmedConnections = new();

    /// <summary>
    /// Maps secrets to their registered outpoints, populated during RegisterInput.
    /// Used during ConnectionConfirmation to look up who needs to confirm.
    /// </summary>
    private readonly ConcurrentDictionary<string, OutPoint> _registeredSecrets = new();

    public async Task<KompaktorRoundEventInputRegistered> RegisterInput(RegisterInputRequest request)
    {
        if (ActiveQuotes.Remove(request.Secret, out var quote))
        {
            if (quote.response.CredentialAmount < request.CredentialsRequest.Delta)
                throw new KompaktorProtocolException(KompaktorProtocolErrorCode.CredentialAmountMismatch,
                    "Amount credential request too high");
            var credentialsResponse = await CredentialIssuers[CredentialType.Amount]
                .HandleRequestAsync(request.CredentialsRequest, _cts.Token);
            if (NotReadyToSign.TryAdd(request.Secret, quote.coin.Outpoint))
            {
                _registeredSecrets[request.Secret] = quote.coin.Outpoint;
                return await AddEvent(new KompaktorRoundEventInputRegistered(quote.quoteRequest, credentialsResponse, quote.coin));
            }
        }

        throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InvalidQuote,
            "Invalid quote");
    }

    public async Task ConfirmConnection(ConfirmConnectionRequest request)
    {
        if (Status != KompaktorStatus.ConnectionConfirmation)
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.WrongPhase,
                "Round is not in connection confirmation phase");

        if (!_registeredSecrets.TryGetValue(request.Secret, out var outpoint))
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.SecretNotFound,
                "Secret not found");

        if (!_confirmedConnections.TryAdd(request.Secret, outpoint))
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InputAlreadyRegistered,
                "Connection already confirmed");

        // Re-validate UTXO is still unspent (double-spend detection)
        var txOutStatus = await _rpcClient.GetTxOutAsync(outpoint.Hash, (int)outpoint.N);
        if (txOutStatus is null)
        {
            _confirmedConnections.TryRemove(request.Secret, out _);
            _prison?.Ban(outpoint, BanReason.DoubleSpend);
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InputNotValid,
                "Input has been spent (double-spend detected)");
        }

        _logger.LogInformation("Connection confirmed for {Outpoint}. Confirmed: {Count}/{Total}",
            outpoint, _confirmedConnections.Count, _registeredSecrets.Count);

        // Check if all inputs confirmed — transition early
        if (_confirmedConnections.Count == _registeredSecrets.Count)
        {
            await UpdateStatus(KompaktorStatus.OutputRegistration);
        }
    }

    protected override Task<T> AddEvent<T>(T @event)
    {
        var roundId = @event is KompaktorRoundEventCreated c ? c.RoundId
            : Events.OfType<KompaktorRoundEventCreated>().FirstOrDefault()?.RoundId ?? "?";
        _logger.LogDebug("[{RoundId}] Event {EventType} at {Timestamp}",
            roundId, @event.GetType().Name, @event.Timestamp);
        return base.AddEvent(@event);
    }

    public async Task<KompaktorRoundCredentialReissuanceResponse> ReissueCredentials(
        CredentialReissuanceRequest request)
    {
        if (Status > KompaktorStatus.OutputRegistration)
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.WrongPhase,
                "Reissuance cannot be done after output registration");

        if (request.CredentialsRequest.Values.Any(credentialsRequest => credentialsRequest.Delta > 0))
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InvalidCredentialRequest,
                "You cannot mint more money");

        if (!request.CredentialsRequest.TryGetValue(CredentialType.Amount, out _))
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InvalidCredentialRequest,
                "Amount credential request missing");

        var creds = (await Task.WhenAll(request.CredentialsRequest.Select(async pair =>
        {
            var issuer = CredentialIssuers[pair.Key];

            try
            {
                var result = await issuer.HandleRequestAsync(pair.Value, _cts.Token);
                return (pair.Key, result);
            }
            catch (Exception e)
            {
                _logger.LogException($"Failed to reissue {pair.Key}", e);
                return (pair.Key, null);
            }
        }))).Where(x => x.Item2 is not null).ToDictionary(x => x.Item1, x => x.Item2!);

        return new KompaktorRoundCredentialReissuanceResponse(creds);
    }

    public async Task<KompaktorRoundEventOutputRegistered> RegisterOutput(RegisterOutputRequest request)
    {
        if (Status != KompaktorStatus.OutputRegistration)
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.WrongPhase,
                "Round is not in output registration phase");

        if (!request.CredentialsRequest.TryGetValue(CredentialType.Amount, out var amtRequest))
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InvalidCredentialRequest,
                "Amount credential request missing");

        if (amtRequest.Delta >= 0)
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.OutputNotNegative,
                "Output must be negative");

        var credentialAmount = -amtRequest.Delta;

        var miningFee = RoundEventCreated.FeeRate.GetFee(request.Output.GetSerializedSize());
        var outputAmount = credentialAmount - miningFee.Satoshi;
        if (outputAmount < Money.Zero)
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.OutputUneconomic,
                "Output uneconomic");

        // Credential issuers are thread-safe (Interlocked balance, locked serial numbers).
        // AddEvent serialization is handled by KompaktorRound._lock.
        var creds = (await Task.WhenAll(request.CredentialsRequest.Select(async pair =>
        {
            var issuer = CredentialIssuers[pair.Key];
            var result = await issuer.HandleRequestAsync(pair.Value, _cts.Token);
            return (pair.Key, result);
        }))).ToDictionary(x => x.Item1, x => x.result);

        return await AddEvent(new KompaktorRoundEventOutputRegistered(request, creds));
    }

    public async Task<KompaktorRoundEventSignaturePosted> Sign(SignRequest request)
    {
        if (Status != KompaktorStatus.Signing)
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.WrongPhase,
                "Round is not in signing phase");

        if (Events.OfType<KompaktorRoundEventSignaturePosted>().Any(x => x.Request.OutPoint == request.OutPoint))
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.SignatureAlreadyProvided,
                "Signature already provided");

        var tx = GetTransaction(_network);
        var inputToSign = tx.Inputs.FindIndexedInput(request.OutPoint);
        if (inputToSign is null)
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InputNotFound,
                "Input not found");

        _logger.LogDebug("api:Signing input {Index} for id: {TxHash}", inputToSign.Index, tx.GetHash());

        var inputRegistration = Events.OfType<KompaktorRoundEventInputRegistered>()
            .Single(input => input.Coin.Outpoint == request.OutPoint);
        var allowedVsize = inputRegistration.QuoteRequest.Signature.FundProofs[0].GetSize();

        inputToSign.WitScript = request.Witness;

        if (inputToSign.TxIn.GetSize() > allowedVsize)
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InputSizeTooLarge,
                "Input size too large");

        var precomputedTransactionData = tx.PrecomputeTransactionData(Inputs.ToArray());
        if (!inputToSign.VerifyScript(inputRegistration.Coin, ScriptVerify.Standard, precomputedTransactionData,
                out var error))
        {
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InvalidSignature,
                $"Invalid signature: {error}");
        }

        return await AddEvent(new KompaktorRoundEventSignaturePosted(request));
    }

    public async Task ReadyToSign(ReadyToSignRequest request)
    {
        if (Status != KompaktorStatus.OutputRegistration)
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.WrongPhase,
                "Round is not in output phase");
        if (!NotReadyToSign.TryRemove(request.Secret, out var coin))
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.SecretNotFound,
                "Secret not found");
        var timeleft = Events.OfType<KompaktorRoundEventStatusUpdate>()
            .First(x => x.Status == KompaktorStatus.OutputRegistration).Timestamp
            .Add(RoundEventCreated.OutputTimeout) - DateTimeOffset.UtcNow;
        _logger.LogInformation("Ready to sign on {Coin}. Remaining: {Count} time left: {TimeLeft}",
            coin, NotReadyToSign.Count, timeleft);
        if (NotReadyToSign.IsEmpty)
        {
            await UpdateStatus(KompaktorStatus.Signing);
        }
    }

    public async Task Start(KompaktorRoundEventCreated created, Dictionary<CredentialType, ICredentialIssuer> issuers)
    {
        if (Events.Count() != 0)
            throw new KompaktorProtocolException(KompaktorProtocolErrorCode.RoundAlreadyStarted,
                "Round already started");
        CredentialIssuers = issuers;
        await AddEvent(created);
    }

    private async Task HandleStatusChange(KompaktorStatus newStatus)
    {
        var created = RoundEventCreated;
        switch (newStatus)
        {
            case KompaktorStatus.InputRegistration:
                // Soft timeout: if set, allows early transition when minimum inputs are met
                if (created.InputRegistrationSoftTimeout is { } softTimeout)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(softTimeout, _cts.Token);
                            if (Status != KompaktorStatus.InputRegistration) return;
                            _inputSoftTimeoutReached = true;
                            _logger.LogInformation("Input registration soft timeout reached ({SoftTimeout}), {InputCount} inputs registered",
                                softTimeout, Inputs.Count);
                            // Check immediately — inputs may already meet minimum
                            if (created.InputCount.Contains(Inputs.Count))
                            {
                                _logger.LogInformation("Minimum inputs met at soft timeout ({InputCount}), transitioning early",
                                    Inputs.Count);
                                await UpdateStatus(KompaktorStatus.ConnectionConfirmation);
                            }
                            // Otherwise, HandleNewEvents will check on each subsequent InputRegistered
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception e) { _logger.LogException("Input soft timeout handler failed", e); }
                    });
                }

                // Hard timeout: backstop that always fires
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(created.InputTimeout, _cts.Token);
                        if (Status != KompaktorStatus.InputRegistration) return;
                        if (!created.InputCount.Contains(Inputs.Count))
                        {
                            _logger.LogDebug("Input registration failed due to input count not met");
                            await UpdateStatus(KompaktorStatus.Failed);
                            return;
                        }
                        _logger.LogDebug("Input registration completed with {InputCount} inputs and {QuoteCount} remaining quotes",
                            Inputs.Count, ActiveQuotes.Count);
                        await UpdateStatus(KompaktorStatus.ConnectionConfirmation);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e) { _logger.LogException("Input phase timeout handler failed", e); }
                });
                break;

            case KompaktorStatus.ConnectionConfirmation:
                _logger.LogDebug("Connection confirmation started (expires in {Timeout})", created.ConnectionConfirmationTimeout);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(created.ConnectionConfirmationTimeout, _cts.Token);
                        if (Status != KompaktorStatus.ConnectionConfirmation) return;

                        // Remove unconfirmed inputs and ban them
                        var unconfirmed = _registeredSecrets
                            .Where(kvp => !_confirmedConnections.ContainsKey(kvp.Key))
                            .Select(kvp => kvp.Value).ToList();

                        if (unconfirmed.Count > 0 && _prison is not null)
                        {
                            // Safety valve: if too many failed, assume coordinator issue
                            if (unconfirmed.Count > _registeredSecrets.Count / 2)
                            {
                                _logger.LogWarning("Too many unconfirmed inputs ({Unconfirmed}/{Total}), applying light safety ban",
                                    unconfirmed.Count, _registeredSecrets.Count);
                                foreach (var outpoint in unconfirmed)
                                    _prison.Ban(outpoint, BanReason.CoordinatorStabilitySafety);
                            }
                            else
                            {
                                _logger.LogWarning("Banning {Count} inputs that failed to confirm connection", unconfirmed.Count);
                                foreach (var outpoint in unconfirmed)
                                    _prison.Ban(outpoint, BanReason.FailedToConfirm);
                            }
                        }

                        // Remove unconfirmed from NotReadyToSign (they can't participate)
                        var unconfirmedSet = unconfirmed.ToHashSet();
                        foreach (var kvp in NotReadyToSign)
                        {
                            if (unconfirmedSet.Contains(kvp.Value))
                                NotReadyToSign.TryRemove(kvp.Key, out _);
                        }

                        var confirmedCount = _confirmedConnections.Count;
                        if (!created.InputCount.Contains(confirmedCount))
                        {
                            _logger.LogWarning("Not enough confirmed inputs ({Confirmed}), round failing", confirmedCount);
                            await UpdateStatus(KompaktorStatus.Failed);
                            return;
                        }

                        _logger.LogInformation("Connection confirmation completed: {Confirmed}/{Total} inputs confirmed",
                            confirmedCount, _registeredSecrets.Count);
                        await UpdateStatus(KompaktorStatus.OutputRegistration);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e) { _logger.LogException("Connection confirmation timeout handler failed", e); }
                });
                break;

            case KompaktorStatus.OutputRegistration:
                _logger.LogDebug("Output registration started (expires in {Timeout})", created.OutputTimeout);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(created.OutputTimeout, _cts.Token);
                        if (Status != KompaktorStatus.OutputRegistration) return;

                        // Fast-signing mode: if some participants didn't signal ready,
                        // ban them with lighter penalty and proceed if enough remain
                        if (!NotReadyToSign.IsEmpty && _prison is not null)
                        {
                            var notReadyOutpoints = NotReadyToSign.Values.ToList();
                            _logger.LogWarning("Fast-signing: {Count} inputs did not signal ready, applying light ban",
                                notReadyOutpoints.Count);
                            foreach (var outpoint in notReadyOutpoints)
                                _prison.Ban(outpoint, BanReason.FailedToSignalReady);

                            // Remove them so the round can proceed
                            NotReadyToSign.Clear();
                        }

                        if (!created.OutputCount.Contains(Outputs.Count))
                        {
                            _logger.LogDebug("Output registration failed due to output count not met");
                            await UpdateStatus(KompaktorStatus.Failed);
                            return;
                        }
                        await UpdateStatus(KompaktorStatus.Signing);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e) { _logger.LogException("Output phase timeout handler failed", e); }
                });
                break;

            case KompaktorStatus.Signing:
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(created.SigningTimeout, _cts.Token);
                        if (Status != KompaktorStatus.Signing) return;

                        var signedOutpoints = Events.OfType<KompaktorRoundEventSignaturePosted>()
                            .Select(s => s.Request.OutPoint).ToHashSet();
                        var disruptors = Inputs
                            .Where(c => !signedOutpoints.Contains(c.Outpoint))
                            .Select(c => c.Outpoint).ToList();

                        // Ban coins that didn't sign (disruptors)
                        if (_prison is not null && disruptors.Count > 0)
                        {
                            // Safety valve: if too many failed, assume coordinator issue
                            if (disruptors.Count > Inputs.Count / 2)
                            {
                                _logger.LogWarning("Too many signing failures ({Disruptors}/{Total}), applying light safety ban",
                                    disruptors.Count, Inputs.Count);
                                foreach (var outpoint in disruptors)
                                    _prison.Ban(outpoint, BanReason.CoordinatorStabilitySafety);
                            }
                            else
                            {
                                _logger.LogWarning("Banning {Count} disruptors that failed to sign", disruptors.Count);
                                _prison.BanDisruptors(disruptors);
                            }
                        }

                        await UpdateStatus(KompaktorStatus.Failed);

                        // Request blame round if enough honest participants remain
                        var minForBlame = Math.Max(created.InputCount.Min, (int)(created.InputCount.Max * 0.4));
                        if (signedOutpoints.Count >= minForBlame && BlameRoundRequested is not null)
                        {
                            _logger.LogInformation("Requesting blame round with {Count} honest participants", signedOutpoints.Count);
                            // Filter out any currently banned outpoints from the whitelist
                            var whitelist = _prison is not null
                                ? signedOutpoints.Where(o => !_prison.IsBanned(o)).ToHashSet()
                                : signedOutpoints;
                            if (whitelist.Count >= minForBlame)
                                await BlameRoundRequested(created.RoundId, whitelist);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e) { _logger.LogException("Signing phase timeout handler failed", e); }
                });
                break;

            case KompaktorStatus.Broadcasting:
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var tx = GetTransaction(_network);
                        var fee = tx.GetFee(Inputs.ToArray());
                        if (fee > Money.Coins(0.01m))
                            _logger.LogWarning("High transaction fee detected: {Fee} on {Inputs} inputs / {Outputs} outputs",
                                fee, Inputs.Count, tx.Outputs.Count);
                        // Pass maxfeerate=0 to skip the per-kVB fee rate check;
                        // the node's -maxtxfee still caps the absolute fee.
                        var resp = await _rpcClient.SendCommandAsync("sendrawtransaction", tx.ToHex(), 0);
                        resp.ThrowIfError();
                        var result = uint256.Parse(resp.Result.ToString());
                        if (Status != KompaktorStatus.Broadcasting) return;
                        await UpdateStatus(result is not null ? KompaktorStatus.Completed : KompaktorStatus.Failed);
                    }
                    catch (Exception e)
                    {
                        _logger.LogException(
                            $"Failed to broadcast transaction (fee:{GetTransaction(_network).GetFee(Inputs.ToArray())}",
                            e);
                        try { await UpdateStatus(KompaktorStatus.Failed); }
                        catch (Exception) { /* Already in terminal state */ }
                    }
                });
                break;
        }
    }

    private async Task UpdateStatus(KompaktorStatus status)
    {
        await _statusSemaphore.WaitAsync();
        try
        {
            if (status == Status) return;
            if (status < Status)
                throw new KompaktorProtocolException(KompaktorProtocolErrorCode.WrongPhase,
                    "Cannot go back in status");
            _logger.LogInformation("[{RoundId}] {OldPhase} -> {NewPhase} | Inputs={InputCount} Outputs={OutputCount} Signatures={SignatureCount}",
                RoundEventCreated?.RoundId ?? "?", Status, status, Inputs.Count, Outputs.Count, SignatureCount);
            await AddEvent(new KompaktorRoundEventStatusUpdate(status));
        }
        finally
        {
            _statusSemaphore.Release();
        }
    }

    public override void Dispose()
    {
        _cts.Cancel();
        base.Dispose();
    }
}
