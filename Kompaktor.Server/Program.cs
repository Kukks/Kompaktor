using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Kompaktor.Models;
using Kompaktor.Prison;
using Kompaktor.Server;
using Kompaktor.Server.Orchestration;
using Kompaktor.JsonConverters;
using Kompaktor.Utils;
using NBitcoin;
using Kompaktor.Blockchain;
using NBitcoin.RPC;
using NBitcoin.Secp256k1;
using WabiSabi.Crypto.Randomness;

var builder = WebApplication.CreateBuilder(args);

// Configure JSON serialization with Kompaktor converters
builder.Services.ConfigureHttpJsonOptions(options =>
{
    KompaktorJsonHelper.ConfigureJsonOptions(options.SerializerOptions);
});

// Rate limiting — generous per-IP limits to accommodate Tor exit node sharing.
// Primary abuse prevention is the Prison system (UTXO-based); this is defense-in-depth.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("protocol", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 200,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.AddPolicy("discovery", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

// Configure coordinator options
var coordinatorOptions = builder.Configuration
    .GetSection("Kompaktor")
    .Get<KompaktorCoordinatorOptions>() ?? new KompaktorCoordinatorOptions();

// Configure Bitcoin RPC
var rpcUri = builder.Configuration["Bitcoin:RpcUri"] ?? "http://localhost:53782";
var rpcUser = builder.Configuration["Bitcoin:RpcUser"] ?? "ceiwHEbqWI83";
var rpcPassword = builder.Configuration["Bitcoin:RpcPassword"] ?? "DwubwWsoo3";
var networkStr = builder.Configuration["Bitcoin:Network"] ?? "regtest";
var network = networkStr.ToLowerInvariant() switch
{
    "main" or "mainnet" => Network.Main,
    "testnet" or "test" => Network.TestNet,
    "regtest" => Network.RegTest,
    _ => throw new InvalidOperationException($"Unknown Bitcoin network '{networkStr}'. Use 'main', 'testnet', or 'regtest'.")
};

var rpcClient = new RPCClient($"{rpcUser}:{rpcPassword}", rpcUri, network);
var blockchain = new BitcoinCoreBackend(rpcClient);

// Load coordinator signing key from config, or generate ephemeral
ECPrivKey? coordinatorSigningKey = null;
if (!string.IsNullOrEmpty(coordinatorOptions.CoordinatorSigningKeyHex))
{
    coordinatorSigningKey = coordinatorOptions.CoordinatorSigningKeyHex.ToPrivKey();
}
else
{
    coordinatorSigningKey = ECPrivKey.Create(RandomNumberGenerator.GetBytes(32));
    using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
    loggerFactory.CreateLogger("Startup").LogWarning(
        "No CoordinatorSigningKeyHex configured — generated an ephemeral key. " +
        "Transcript signatures will not survive restarts; set CoordinatorSigningKeyHex for production.");
}

// Register services
builder.Services.AddSingleton(coordinatorOptions);
builder.Services.AddSingleton(new KompaktorPrison());
var random = network == Network.RegTest ? new InsecureRandom() : (WasabiRandom)SecureRandom.Instance;
builder.Services.AddSingleton<KompaktorRoundManager>(sp =>
    new KompaktorRoundManager(
        network,
        blockchain,
        random,
        sp.GetRequiredService<ILoggerFactory>(),
        coordinatorOptions,
        sp.GetRequiredService<KompaktorPrison>(),
        coordinatorSigningKey));

// Round orchestration
builder.Services.AddSingleton<IRoundSchedulingPolicy>(new DemandAdaptiveSchedulingPolicy());
builder.Services.AddSingleton<KompaktorRoundOrchestrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KompaktorRoundOrchestrator>());

var app = builder.Build();

app.UseRateLimiter();

// Map Kompaktor API endpoints
app.MapKompaktorEndpoints();

app.Run();
