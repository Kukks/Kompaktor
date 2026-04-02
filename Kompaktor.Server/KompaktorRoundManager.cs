using System.Collections.Concurrent;
using Kompaktor.Credentials;
using Kompaktor.Models;
using Kompaktor.Prison;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.RPC;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Randomness;

namespace Kompaktor.Server;

/// <summary>
/// Manages the lifecycle of multiple concurrent Kompaktor rounds.
/// </summary>
public class KompaktorRoundManager : IDisposable
{
    private readonly ConcurrentDictionary<string, KompaktorRoundOperator> _rounds = new();
    private readonly Network _network;
    private readonly RPCClient _rpcClient;
    private readonly WasabiRandom _random;
    private readonly ILoggerFactory _loggerFactory;
    private readonly KompaktorCoordinatorOptions _options;
    private readonly KompaktorPrison _prison;

    public KompaktorRoundManager(
        Network network,
        RPCClient rpcClient,
        WasabiRandom random,
        ILoggerFactory loggerFactory,
        KompaktorCoordinatorOptions? options = null,
        KompaktorPrison? prison = null)
    {
        _network = network;
        _rpcClient = rpcClient;
        _random = random;
        _loggerFactory = loggerFactory;
        _options = options ?? new KompaktorCoordinatorOptions();
        _prison = prison ?? new KompaktorPrison();
    }

    /// <summary>Creates a new round with the configured options and starts it.</summary>
    public async Task<string> CreateRound()
    {
        var roundId = Guid.NewGuid().ToString();
        var logger = _loggerFactory.CreateLogger($"Round-{roundId[..8]}");
        var op = new KompaktorRoundOperator(_network, _rpcClient, _random, logger, _prison);

        var issuerKey = new CredentialIssuerSecretKey(_random);
        var k = _options.CredentialCount;
        var useBp = _options.UseBulletproofs;
        var issuer = CredentialType.Amount.CreateIssuer(issuerKey, _random, k, useBp);
        var issuers = new Dictionary<CredentialType, ICredentialIssuer>
        {
            { CredentialType.Amount, issuer }
        };

        var created = new KompaktorRoundEventCreated(
            roundId,
            _options.FeeRate,
            _options.InputTimeout,
            _options.OutputTimeout,
            _options.SigningTimeout,
            new IntRange(_options.MinInputCount, _options.MaxInputCount),
            new MoneyRange(_options.MinInputAmount, _options.MaxInputAmount),
            new IntRange(_options.MinOutputCount, _options.MaxOutputCount),
            new MoneyRange(_options.MinOutputAmount, _options.MaxOutputAmount),
            new Dictionary<CredentialType, CredentialConfiguration>
            {
                {
                    CredentialType.Amount,
                    new CredentialConfiguration(_options.MaxCredentialValue, new IntRange(k, k), new IntRange(k, k),
                        issuerKey.ComputeCredentialIssuerParameters(), useBp)
                }
            }
        );

        _rounds[roundId] = op;

        // Clean up completed/failed rounds
        op.NewEvent += async (sender, evt) =>
        {
            if (evt is KompaktorRoundEventStatusUpdate status &&
                status.Status is KompaktorStatus.Completed or KompaktorStatus.Failed)
            {
                // Keep the round for a bit for status queries, then remove
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(5));
                    if (_rounds.TryRemove(roundId, out var r))
                        r.Dispose();
                });
            }
        };

        await op.Start(created, issuers);
        logger.LogInformation("Round {RoundId} created", roundId);
        return roundId;
    }

    /// <summary>Gets the operator for a specific round.</summary>
    public KompaktorRoundOperator? GetOperator(string roundId)
    {
        _rounds.TryGetValue(roundId, out var op);
        return op;
    }

    /// <summary>Gets information about all active rounds.</summary>
    public object[] GetActiveRounds()
    {
        return _rounds.Select(kvp => new
        {
            roundId = kvp.Key,
            status = kvp.Value.Status.ToString(),
            inputCount = kvp.Value.Inputs.Count,
            outputCount = kvp.Value.Outputs.Count
        }).ToArray<object>();
    }

    public KompaktorPrison Prison => _prison;

    public void Dispose()
    {
        foreach (var kvp in _rounds)
            kvp.Value.Dispose();
        _rounds.Clear();
    }
}
