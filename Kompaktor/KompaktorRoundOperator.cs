using System.Collections.Concurrent;
using System.Security.Cryptography;
using Kompaktor.Contracts;
using Kompaktor.Credentials;
using Kompaktor.Errors;
using Kompaktor.Models;
using Kompaktor.Prison;
using Kompaktor.Utils;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.RPC;
using NBitcoin.Secp256k1;
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
    private ECPrivKey? _coordinatorSigningKey;

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

    /// <summary>
    /// Sets the coordinator's Schnorr signing key used for transcript signatures.
    /// Must be called before the round starts. The corresponding public key is
    /// published with each transcript signature event, enabling clients to detect
    /// equivocation (non-repudiable proof if the coordinator signs different
    /// transcripts for different clients in the same round).
    /// </summary>
    public void SetCoordinatorSigningKey(ECPrivKey key) => _coordinatorSigningKey = key;

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
            await UpdateStatus(KompaktorStatus.OutputRegistration);
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
        var inputFee = coin.ScriptPubKey.EstimateFee(feeRate);
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

    /// <summary>
    /// Tracks which input secrets have an active persistent connection (WebSocket or in-process).
    /// Populated during RegisterInput, used at phase transitions to prune disconnected inputs.
    /// Key = secret, Value = outpoint.
    /// </summary>
    private readonly ConcurrentDictionary<string, OutPoint> _connectedSecrets = new();

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
                return await AddEvent(new KompaktorRoundEventInputRegistered(quote.quoteRequest, credentialsResponse, quote.coin));
            }
        }

        throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InvalidQuote,
            "Invalid quote");
    }

    /// <summary>
    /// Marks a secret as having an active persistent connection.
    /// Called when a client establishes a WebSocket (or in-process equivalent).
    /// The secret must match one used during RegisterInput.
    /// </summary>
    public bool Connect(string secret)
    {
        if (!NotReadyToSign.ContainsKey(secret))
            return false;
        var outpoint = NotReadyToSign[secret];
        _connectedSecrets[secret] = outpoint;
        _logger.LogDebug("Connection opened for {Outpoint}. Connected: {Count}",
            outpoint, _connectedSecrets.Count);
        return true;
    }

    /// <summary>
    /// Marks a secret as disconnected.
    /// Called when the client's WebSocket closes.
    /// The client may reconnect later — pruning only happens at phase transitions.
    /// </summary>
    public void Disconnect(string secret)
    {
        if (_connectedSecrets.TryRemove(secret, out var outpoint))
            _logger.LogDebug("Connection closed for {Outpoint}. Connected: {Count}",
                outpoint, _connectedSecrets.Count);
    }

    /// <summary>
    /// Removes inputs that have no active connection and bans them.
    /// Called at phase transitions (e.g., InputRegistration → OutputRegistration).
    /// Returns the number of inputs pruned.
    /// </summary>
    private int PruneDisconnectedInputs()
    {
        var disconnected = NotReadyToSign
            .Where(kvp => !_connectedSecrets.ContainsKey(kvp.Key))
            .ToList();

        if (disconnected.Count == 0)
            return 0;

        if (_prison is not null)
        {
            // Safety valve: if too many disconnected, assume coordinator fault
            if (disconnected.Count > NotReadyToSign.Count / 2)
            {
                _logger.LogWarning("Too many disconnected inputs ({Disconnected}/{Total}), applying light safety ban",
                    disconnected.Count, NotReadyToSign.Count);
                foreach (var kvp in disconnected)
                    _prison.Ban(kvp.Value, BanReason.CoordinatorStabilitySafety);
            }
            else
            {
                _logger.LogWarning("Pruning {Count} disconnected inputs", disconnected.Count);
                foreach (var kvp in disconnected)
                    _prison.Ban(kvp.Value, BanReason.FailedToConfirm);
            }
        }

        foreach (var kvp in disconnected)
            NotReadyToSign.TryRemove(kvp.Key, out _);

        return disconnected.Count;
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
        var allowedVsize = inputRegistration.Coin.ScriptPubKey.EstimateInputVsize();

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
                                PruneDisconnectedInputs();
                                await UpdateStatus(KompaktorStatus.OutputRegistration);
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
                        PruneDisconnectedInputs();
                        await UpdateStatus(KompaktorStatus.OutputRegistration);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e) { _logger.LogException("Input phase timeout handler failed", e); }
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

    /// <summary>
    /// Computes a SHA256 hash of all event IDs in the round transcript and signs it
    /// with the coordinator's Schnorr key. Emits a KompaktorRoundEventTranscriptSigned
    /// event before transitioning to the signing phase.
    /// </summary>
    private async Task SignTranscript()
    {
        if (_coordinatorSigningKey is null)
        {
            _logger.LogWarning("No coordinator signing key set — transcript will not be signed. " +
                               "Clients cannot verify round consistency via non-repudiable proof.");
            return;
        }

        var eventIds = Events.Select(e => e.Id).ToArray();
        var concatenated = string.Join("", eventIds);
        var transcriptHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(concatenated));

        var sig = _coordinatorSigningKey.SignBIP340(transcriptHash);
        var pubKey = _coordinatorSigningKey.CreateXOnlyPubKey();
        var pubKeyBytes = new byte[32];
        pubKey.WriteToSpan(pubKeyBytes);
        var sigBytes = new byte[64];
        sig.WriteToSpan(sigBytes);

        await AddEvent(new KompaktorRoundEventTranscriptSigned(transcriptHash, sigBytes, pubKeyBytes));
        _logger.LogInformation("Transcript signed with coordinator key ({EventCount} events)", eventIds.Length);
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

            // Sign the transcript before entering the signing phase
            if (status == KompaktorStatus.Signing)
                await SignTranscript();
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
