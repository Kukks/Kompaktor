using System.Security.Cryptography;
using Kompaktor.Models;
using Kompaktor.Prison;
using Kompaktor.Server;
using Kompaktor.Server.Orchestration;
using Kompaktor.JsonConverters;
using Kompaktor.Utils;
using NBitcoin;
using NBitcoin.RPC;
using NBitcoin.Secp256k1;
using WabiSabi.Crypto.Randomness;

var builder = WebApplication.CreateBuilder(args);

// Configure JSON serialization with Kompaktor converters
builder.Services.ConfigureHttpJsonOptions(options =>
{
    KompaktorJsonHelper.ConfigureJsonOptions(options.SerializerOptions);
});

// Configure coordinator options
var coordinatorOptions = builder.Configuration
    .GetSection("Kompaktor")
    .Get<KompaktorCoordinatorOptions>() ?? new KompaktorCoordinatorOptions();

// Configure Bitcoin RPC
var rpcUri = builder.Configuration["Bitcoin:RpcUri"] ?? "http://localhost:53782";
var rpcUser = builder.Configuration["Bitcoin:RpcUser"] ?? "ceiwHEbqWI83";
var rpcPassword = builder.Configuration["Bitcoin:RpcPassword"] ?? "DwubwWsoo3";
var network = Network.RegTest;

var rpcClient = new RPCClient($"{rpcUser}:{rpcPassword}", rpcUri, network);

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
builder.Services.AddSingleton<KompaktorRoundManager>(sp =>
    new KompaktorRoundManager(
        network,
        rpcClient,
        new InsecureRandom(),
        sp.GetRequiredService<ILoggerFactory>(),
        coordinatorOptions,
        sp.GetRequiredService<KompaktorPrison>(),
        coordinatorSigningKey));

// Round orchestration
builder.Services.AddSingleton<IRoundSchedulingPolicy>(new DemandAdaptiveSchedulingPolicy());
builder.Services.AddSingleton<KompaktorRoundOrchestrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KompaktorRoundOrchestrator>());

var app = builder.Build();

// Map Kompaktor API endpoints
app.MapKompaktorEndpoints();

app.Run();
