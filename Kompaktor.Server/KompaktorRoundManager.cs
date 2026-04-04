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
            _options.ConnectionConfirmationTimeout,
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
            },
            _options.InputRegistrationSoftTimeout
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

        // Blame round creation on signing failure
        op.BlameRoundRequested += async (parentRoundId, whitelist) =>
        {
            try
            {
                var blameRoundId = await CreateBlameRound(parentRoundId, whitelist);
                logger.LogInformation("Blame round {BlameRoundId} created from {ParentRoundId} with {Count} whitelisted inputs",
                    blameRoundId, parentRoundId, whitelist.Count);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to create blame round from {ParentRoundId}", parentRoundId);
            }
        };

        await op.Start(created, issuers);
        logger.LogInformation("Round {RoundId} created", roundId);
        return roundId;
    }

    /// <summary>Creates a blame round from a failed parent round with a whitelist of honest participants.</summary>
    public async Task<string> CreateBlameRound(string parentRoundId, HashSet<OutPoint> whitelist)
    {
        var roundId = Guid.NewGuid().ToString();
        var logger = _loggerFactory.CreateLogger($"BlameRound-{roundId[..8]}");
        var op = new KompaktorRoundOperator(_network, _rpcClient, _random, logger, _prison);

        var issuerKey = new CredentialIssuerSecretKey(_random);
        var k = _options.CredentialCount;
        var useBp = _options.UseBulletproofs;
        var issuer = CredentialType.Amount.CreateIssuer(issuerKey, _random, k, useBp);
        var issuers = new Dictionary<CredentialType, ICredentialIssuer>
        {
            { CredentialType.Amount, issuer }
        };

        // Blame rounds have shorter input registration (3 min) and same output/signing timeouts
        var blameInputTimeout = TimeSpan.FromMinutes(3);
        var minInputCount = Math.Max(1, (int)(whitelist.Count * 0.4));

        var created = new KompaktorRoundEventCreated(
            roundId,
            _options.FeeRate,
            blameInputTimeout,
            _options.ConnectionConfirmationTimeout,
            _options.OutputTimeout,
            _options.SigningTimeout,
            new IntRange(minInputCount, whitelist.Count),
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
            },
            _options.InputRegistrationSoftTimeout)
        {
            BlameOf = parentRoundId,
            BlameWhitelist = whitelist
        };

        _rounds[roundId] = op;

        // Clean up completed/failed blame rounds
        op.NewEvent += async (sender, evt) =>
        {
            if (evt is KompaktorRoundEventStatusUpdate status &&
                status.Status is KompaktorStatus.Completed or KompaktorStatus.Failed)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(5));
                    if (_rounds.TryRemove(roundId, out var r))
                        r.Dispose();
                });
            }
        };

        // Blame rounds can also trigger further blame rounds
        op.BlameRoundRequested += async (blameParentId, blameWhitelist) =>
        {
            try
            {
                await CreateBlameRound(blameParentId, blameWhitelist);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to create nested blame round from {ParentRoundId}", blameParentId);
            }
        };

        await op.Start(created, issuers);
        logger.LogInformation("Blame round {RoundId} created from parent {ParentRoundId} with {WhitelistCount} whitelisted inputs (min: {MinInputs})",
            roundId, parentRoundId, whitelist.Count, minInputCount);
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
