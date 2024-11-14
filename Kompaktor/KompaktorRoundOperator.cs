using System.Collections.Concurrent;
using System.Threading.Channels;
using Kompaktor.Contracts;
using Kompaktor.Credentials;
using Kompaktor.Models;
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

    private CancellationTokenSource _cts;

    public KompaktorRoundOperator(Network network, RPCClient rpcClient, WasabiRandom random, ILogger logger)
    {
        _network = network;
        _rpcClient = rpcClient;
        _random = random;
        _logger = logger;
        _cts = new CancellationTokenSource();
        NewEvent += HandleNewEvents;
        
    }

    private async Task HandleNewEvents(object sender, KompaktorRoundEvent roundEvent)
    {

        if (roundEvent is KompaktorRoundEventStatusUpdate statusUpdate)
            _ = HandleStatusChange(statusUpdate.Status);
        else if (Status == KompaktorStatus.OutputRegistration && NotReadyToSign.IsEmpty)
            _ =  UpdateStatus(KompaktorStatus.Signing);
        else if (Status == KompaktorStatus.Signing && roundEvent is KompaktorRoundEventSignaturePosted &&
                 Events.OfType<KompaktorRoundEventSignaturePosted>().Count() == Inputs.Count)
            _ =  UpdateStatus(KompaktorStatus.Broadcasting);

    }

    public Dictionary<CredentialType, CredentialIssuer> CredentialIssuers { get; private set; }

    public readonly
        ConcurrentDictionary<string, (RegisterInputQuoteRequest quoteRequest, InputRegistrationQuoteResponse, Coin coin
            )> ActiveQuotes = new();

    public async Task<KompaktorRoundEventMessage> SendMessage(MessageRequest request)
    {
        if (Status > KompaktorStatus.Signing)
            throw new InvalidOperationException("Round status does not allow sending messages");
        return await AddEvent(new KompaktorRoundEventMessage(request));
    }

    public async Task<InputRegistrationQuoteResponse> PreRegisterInput(RegisterInputQuoteRequest quoteRequest)
    {
        if (Status != KompaktorStatus.InputRegistration)
            throw new InvalidOperationException("Round is not in input registration phase");


        if (quoteRequest.CredentialsRequest.Delta != 0)
            throw new InvalidOperationException("Amount credential must be 0 for a quote");

        var txIn = quoteRequest.Signature.FundProofs[0];


        if (Inputs.Any(coin => coin.Outpoint == txIn.PrevOut))
            throw new InvalidOperationException("Coin already registered");


        var txOutStatus = await _rpcClient.GetTxOutAsync(txIn.PrevOut.Hash, (int) txIn.PrevOut.N);

        if (txOutStatus is null or {Confirmations: < 1} or {Confirmations: < 100, IsCoinBase: true})
            throw new InvalidOperationException("Coin not valid");


        var coin = new Coin(txIn.PrevOut, new TxOut(txOutStatus.TxOut.Value, txOutStatus.TxOut.ScriptPubKey));

        if (!RoundEventCreated.InputAmount.Contains(coin.Amount))
            throw new InvalidOperationException("Input amount not allowed");
        if (Inputs.Count >= RoundEventCreated.InputCount.Max)
            throw new InvalidOperationException("Too many inputs in this round");


        if (!txOutStatus.TxOut.ScriptPubKey.GetDestinationAddress(_network)!
                .VerifyBIP322(RoundEventCreated.RoundId, quoteRequest.Signature, [coin]))
            throw new InvalidOperationException("Invalid signature");


        var feeRate = RoundEventCreated.FeeRate;

        var inputFee = txIn.GetFee(feeRate);
        var credentialAmount = txOutStatus.TxOut.Value - inputFee;


        if (credentialAmount.Satoshi <= 0)
        {
            throw new InvalidOperationException("Amount credential is too small to be issued");
        }

        var credentialsResponse =
            await CredentialIssuers[CredentialType.Amount]
                .HandleRequestAsync(quoteRequest.CredentialsRequest, _cts.Token);

        var secret = Convert.ToHexString(_random.GetBytes(32)).ToLower();

        ActiveQuotes[secret] = (quoteRequest,
            new InputRegistrationQuoteResponse(secret, credentialsResponse, credentialAmount), coin);
        return ActiveQuotes[secret].Item2;
    }

    public ConcurrentDictionary<string, OutPoint> NotReadyToSign { get; set; } = new();

    public async Task<KompaktorRoundEventInputRegistered> RegisterInput(RegisterInputRequest request)
    {
        if (ActiveQuotes.Remove(request.Secret, out var quote))
        {
            if (quote.Item2.CredentialAmount < request.CredentialsRequest.Delta)
                throw new InvalidOperationException("Amount credential request too high");
            var credentialsResponse = await CredentialIssuers[CredentialType.Amount]
                .HandleRequestAsync(request.CredentialsRequest, _cts.Token);
            if (NotReadyToSign.TryAdd(request.Secret, quote.coin.Outpoint))
                return await AddEvent(new KompaktorRoundEventInputRegistered(quote.Item1, credentialsResponse, quote.coin));
        }

        throw new InvalidOperationException("Invalid quote");
    }

    // private readonly SemaphoreSlim _eventSemaphore = new SemaphoreSlim(1, 1);

    protected override Task<T> AddEvent<T>(T @event)
    {
        if(@event.ToString() is not null)
         _logger.LogDebug($"Adding event {@event} {@event.Timestamp}");
        return base.AddEvent(@event);
    }


    public async Task<KompaktorRoundCredentialReissuanceResponse> ReissueCredentials(
        CredentialReissuanceRequest request)
    {
        if (Status > KompaktorStatus.OutputRegistration)
            throw new InvalidOperationException("Reissuance cannot be done after output registration");

        if (request.CredentialsRequest.Values.Any(credentialsRequest => credentialsRequest.Delta > 0))
            throw new InvalidOperationException("You cannot mint more money");

        if (!request.CredentialsRequest.TryGetValue(CredentialType.Amount, out var amtRequest))
        {
            throw new InvalidOperationException("Amount credential request missing");
        }

        var creds =  (await Task.WhenAll(request.CredentialsRequest.Select(async pair =>
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
            throw new InvalidOperationException("Round is not in output registration phase");

        if (!request.CredentialsRequest.TryGetValue(CredentialType.Amount, out var amtRequest))
        {
            throw new InvalidOperationException("Amount credential request missing");
        }

        if (amtRequest.Delta >= 0) throw new InvalidOperationException("Output must be negative");

        var credentialAmount = -amtRequest.Delta;


        var miningFee = RoundEventCreated.FeeRate.GetFee(request.Output.GetSerializedSize());
        var outputAmount = credentialAmount - miningFee.Satoshi;
        if (outputAmount < Money.Zero) throw new InvalidOperationException("Output uneconomic");

        var creds =  (await Task.WhenAll(request.CredentialsRequest.Select(async pair =>
        {
            var issuer = CredentialIssuers[pair.Key];
           if( issuer.Balance - outputAmount < 0) throw new InvalidOperationException($"Insufficient balance for {issuer.Balance - outputAmount < 0}");
            try
            {

                var result = await issuer.HandleRequestAsync(pair.Value, _cts.Token);
                return (pair.Key, result);
            }
            catch (Exception e)
            {
                _logger.LogException($"Failed to reissue {pair.Key}", e);
                throw;
                return (pair.Key, null);
            }
        }))).Where(x => x.Item2 is not null).ToDictionary(x => x.Item1, x => x.Item2!);
        
        return await AddEvent(new KompaktorRoundEventOutputRegistered(request, creds));
    }

    public async Task<KompaktorRoundEventSignaturePosted> Sign(SignRequest request)
    {
        if (Status != KompaktorStatus.Signing) throw new InvalidOperationException("Round is not in signing phase");

        // Check if we have this index already
        if (Events.OfType<KompaktorRoundEventSignaturePosted>().Any(x => x.Request.OutPoint == request.OutPoint))
            throw new InvalidOperationException("Signature already provided");

        // verify signature
        var tx = GetTransaction(_network);
        var inputToSign = tx.Inputs.FindIndexedInput(request.OutPoint);
        if (inputToSign is null) throw new InvalidOperationException("Input not found");

        _logger.LogDebug($"api:Signing input {inputToSign.Index} for id: {tx.GetHash()}");

        var inputRegistration = Events.OfType<KompaktorRoundEventInputRegistered>()
            .Single(input => input.Coin.Outpoint == request.OutPoint);
        var allowedVsize = inputRegistration.QuoteRequest.Signature.FundProofs[0].GetSize();

        inputToSign.WitScript = request.Witness;

        if (inputToSign.TxIn.GetSize() > allowedVsize)
            throw new InvalidOperationException("Input size too large");

        var precomputedTransactionData = tx.PrecomputeTransactionData(Inputs.ToArray());
        if (!inputToSign.VerifyScript(inputRegistration.Coin, ScriptVerify.Standard, precomputedTransactionData,
                out var error))
        {
            throw new InvalidOperationException($"Invalid signature: {error}");
        }

        return await AddEvent(new KompaktorRoundEventSignaturePosted(request));
    }

    public async Task ReadyToSign(ReadyToSignRequest request)
    {
        if (Status != KompaktorStatus.OutputRegistration)
            throw new InvalidOperationException("Round is not in output phase");
        if (!NotReadyToSign.TryRemove(request.Secret, out var coin))
            throw new InvalidOperationException("Secret not found");
        var timeleft = this.Events.OfType<KompaktorRoundEventStatusUpdate>().First(x => x.Status == KompaktorStatus.OutputRegistration).Timestamp.Add(RoundEventCreated.OutputTimeout) - DateTimeOffset.UtcNow;
        _logger.LogInformation($"Ready to sign on {coin} . Remaining: {NotReadyToSign.Count} time left: {timeleft}");
        if (NotReadyToSign.IsEmpty)
        {
            await UpdateStatus(KompaktorStatus.Signing);
        }
    }

    public async Task Start(KompaktorRoundEventCreated created, Dictionary<CredentialType, CredentialIssuer> issuers)
    {
        if (Events.Count() != 0)
            throw new InvalidOperationException("Round already started");
        CredentialIssuers = issuers;
        await AddEvent(created);
    }



    private async Task HandleStatusChange(KompaktorStatus newStatus)
    {
        var created = RoundEventCreated;
        switch (newStatus)
        {
            case KompaktorStatus.InputRegistration:
                Task.Delay(created.InputTimeout, _cts.Token)
                    .ContinueWith(async _ =>
                    {
                        if (Status != KompaktorStatus.InputRegistration) return;
                        if (!created.InputCount.Contains(Inputs.Count))
                        {
                            _logger.LogDebug("Input registration failed due to input count not met");
                            _ = UpdateStatus(KompaktorStatus.Failed);
                            return;
                        }

                        _logger.LogDebug("Input registration completed with {0} inputs and {1} remaining quotes", Inputs.Count,ActiveQuotes.Count);
                        _ =  UpdateStatus(KompaktorStatus.OutputRegistration);
                    });
                break;
            case KompaktorStatus.OutputRegistration:
                _logger.LogDebug("Output registration started (expires in {0})", created.OutputTimeout);
                Task.Delay(created.OutputTimeout, _cts.Token)
                    .ContinueWith(async _ =>
                    {
                        if (Status != KompaktorStatus.OutputRegistration) return;
                        if (!created.OutputCount.Contains(Outputs.Count))
                        {
                            _logger.LogDebug("Output registration failed due to output count not met");
                            _ =  UpdateStatus(KompaktorStatus.Failed);
                            return;
                        }

                        if (!NotReadyToSign.IsEmpty)
                        {
                            _logger.LogDebug("Output registration failed due to not all inputs signalled ready");
                            _ =  UpdateStatus(KompaktorStatus.Failed);
                            return;
                        }

                        await UpdateStatus(KompaktorStatus.Signing);
                    });
                break;
            case KompaktorStatus.Signing:
                Task.Delay(created.SigningTimeout, _cts.Token)
                    .ContinueWith(async _ =>
                    {
                        if (Status != KompaktorStatus.Signing) return;

                        _ =  UpdateStatus(KompaktorStatus.Failed);
                    });
                break;
            case KompaktorStatus.Broadcasting:

                _rpcClient.SendRawTransactionAsync(GetTransaction(_network)).ContinueWith(async res =>
                {
                    try
                    {
                        if (Status != KompaktorStatus.Broadcasting) return;
                        _ =  UpdateStatus(res.Result is not null ? KompaktorStatus.Completed : KompaktorStatus.Failed);
                    }
                    catch (Exception e)
                    {
                        
                        _logger.LogException($"Failed to broadcast transaction (fee:{GetTransaction(_network).GetFee(Inputs.ToArray())}", e);
                        
                        
                        
                        _ =  UpdateStatus(KompaktorStatus.Failed);
                    }
                });


                break;
        }
    }


    private readonly SemaphoreSlim _statusSemaphore = new(1, 1);

    private async Task UpdateStatus(KompaktorStatus status)
    {
        await _statusSemaphore.WaitAsync();
        try
        {
            if (status == Status) return;
            if (status < Status)
                throw new InvalidOperationException("Cannot go back in status");
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