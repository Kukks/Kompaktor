using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Kompaktor.Utils;
using Kompaktor.Blockchain;
using Kompaktor.JsonConverters;
using Kompaktor.Models;
using Kompaktor.Prison;
using Kompaktor.Scoring;
using Kompaktor.Server;
using Kompaktor.Server.Orchestration;
using Kompaktor.Wallet;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using NBitcoin.RPC;
using NBitcoin.Secp256k1;
using WabiSabi.Crypto.Randomness;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    KompaktorJsonHelper.ConfigureJsonOptions(options.SerializerOptions);
});

// Coordinator options
var coordinatorOptions = builder.Configuration
    .GetSection("Kompaktor")
    .Get<KompaktorCoordinatorOptions>() ?? new KompaktorCoordinatorOptions();

// Network
var networkStr = builder.Configuration["Bitcoin:Network"] ?? "regtest";
var network = networkStr switch
{
    "mainnet" or "main" => Network.Main,
    "testnet" => Network.TestNet,
    _ => Network.RegTest
};

// Blockchain backend
IBlockchainBackend blockchain;
var electrumServers = builder.Configuration.GetSection("Electrum:Servers").Get<List<ElectrumServerConfig>>();
var electrumHost = builder.Configuration["Electrum:Host"];
if (electrumServers is { Count: > 1 })
{
    // Multi-server mode: split-server routing for privacy
    var strategyStr = builder.Configuration["Electrum:RoutingStrategy"] ?? "RoundRobin";
    Enum.TryParse<RoutingStrategy>(strategyStr, true, out var strategy);
    blockchain = new MultiServerBackend(new MultiServerOptions
    {
        Servers = electrumServers,
        Strategy = strategy
    });
}
else if (electrumServers is { Count: 1 })
{
    var server = electrumServers[0];
    blockchain = new ElectrumBackend(new ElectrumOptions
    {
        Host = server.Host,
        Port = server.Port,
        UseSsl = server.UseSsl
    });
}
else if (electrumHost is not null)
{
    var electrumPort = int.Parse(builder.Configuration["Electrum:Port"] ?? "50001");
    var electrumSsl = bool.Parse(builder.Configuration["Electrum:UseSsl"] ?? "false");
    blockchain = new ElectrumBackend(new ElectrumOptions
    {
        Host = electrumHost,
        Port = electrumPort,
        UseSsl = electrumSsl
    });
}
else
{
    var rpcUri = builder.Configuration["Bitcoin:RpcUri"] ?? "http://localhost:53782";
    var rpcUser = builder.Configuration["Bitcoin:RpcUser"] ?? "ceiwHEbqWI83";
    var rpcPassword = builder.Configuration["Bitcoin:RpcPassword"] ?? "DwubwWsoo3";
    var rpcClient = new RPCClient($"{rpcUser}:{rpcPassword}", rpcUri, network);
    blockchain = new BitcoinCoreBackend(rpcClient);
}

// Wallet database
var walletPath = builder.Configuration["Wallet:Path"] ?? "./wallet.db";
builder.Services.AddDbContext<WalletDbContext>(opt => opt.UseSqlite($"DataSource={walletPath}"));

// Coordinator signing key
var signingKey = !string.IsNullOrEmpty(coordinatorOptions.CoordinatorSigningKeyHex)
    ? coordinatorOptions.CoordinatorSigningKeyHex.ToPrivKey()
    : ECPrivKey.Create(RandomNumberGenerator.GetBytes(32));

// Rate limiting
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

// Services
var random = network == Network.RegTest ? new InsecureRandom() : (WasabiRandom)SecureRandom.Instance;
builder.Services.AddSingleton(coordinatorOptions);
builder.Services.AddSingleton(new KompaktorPrison());
builder.Services.AddSingleton(blockchain);
builder.Services.AddSingleton(new ScoringOptions());
builder.Services.AddSingleton<KompaktorRoundManager>(sp =>
    new KompaktorRoundManager(
        network,
        blockchain,
        random,
        sp.GetRequiredService<ILoggerFactory>(),
        coordinatorOptions,
        sp.GetRequiredService<KompaktorPrison>(),
        signingKey));
builder.Services.AddSingleton<IRoundSchedulingPolicy>(new DemandAdaptiveSchedulingPolicy());
builder.Services.AddSingleton<KompaktorRoundOrchestrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KompaktorRoundOrchestrator>());

builder.Services.AddOpenApi();

var app = builder.Build();

// Ensure database exists
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// Connect blockchain
await blockchain.ConnectAsync();

app.UseRateLimiter();

// Static files (dashboard HTML/JS/CSS)
app.UseStaticFiles();

// Map coordinator API
app.MapKompaktorEndpoints();

// Map wallet dashboard API
app.MapGet("/api/dashboard/summary", async (WalletDbContext db) =>
{
    var wallets = await db.Wallets.CountAsync();
    var utxos = await db.Utxos.Where(u => u.SpentByTxId == null).CountAsync();
    var totalSats = await db.Utxos.Where(u => u.SpentByTxId == null).SumAsync(u => u.AmountSat);
    var coinjoins = await db.CoinJoinRecords.CountAsync();

    return Results.Ok(new
    {
        walletCount = wallets,
        unspentUtxoCount = utxos,
        totalBalanceSats = totalSats,
        totalBalanceBtc = totalSats / 100_000_000.0,
        completedCoinjoins = coinjoins
    });
}).WithTags("Dashboard");

app.MapGet("/api/dashboard/utxos", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(Array.Empty<object>());

    var selector = new WalletCoinSelector(db);
    var scored = await selector.GetScoredUtxosAsync(wallet.Id);

    var result = scored
        .OrderByDescending(s => s.Utxo.AmountSat)
        .Take(100)
        .Select(s => new
        {
            txId = s.Utxo.TxId,
            outputIndex = s.Utxo.OutputIndex,
            amountSat = s.Utxo.AmountSat,
            amountBtc = s.Utxo.AmountSat / 100_000_000.0,
            confirmedHeight = s.Utxo.ConfirmedHeight,
            rawAnonSet = s.Score.RawAnonSet,
            effectiveScore = Math.Round(s.Score.EffectiveScore, 2),
            coinJoinCount = s.Score.CoinJoinCount,
            confidence = s.Score.Confidence.ToString(),
            labels = s.Labels
        })
        .ToList();

    return Results.Ok(result);
}).WithTags("Dashboard");

app.MapGet("/api/dashboard/privacy-summary", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null)
        return Results.Ok(new { totalUtxos = 0, totalAmountSat = 0L, averageEffectiveScore = 0.0,
            minEffectiveScore = 0.0, maxEffectiveScore = 0.0, mixedUtxoCount = 0, needsMixingCount = 0 });

    var selector = new WalletCoinSelector(db);
    var summary = await selector.GetPrivacySummaryAsync(wallet.Id);

    return Results.Ok(new
    {
        totalUtxos = summary.TotalUtxos,
        totalAmountSat = summary.TotalAmountSat,
        totalAmountBtc = summary.TotalAmountSat / 100_000_000.0,
        averageEffectiveScore = Math.Round(summary.AverageEffectiveScore, 2),
        minEffectiveScore = Math.Round(summary.MinEffectiveScore, 2),
        maxEffectiveScore = Math.Round(summary.MaxEffectiveScore, 2),
        mixedUtxoCount = summary.MixedUtxoCount,
        needsMixingCount = summary.NeedsMixingCount
    });
}).WithTags("Dashboard");

app.MapGet("/api/dashboard/coinjoins", async (WalletDbContext db) =>
{
    var records = await db.CoinJoinRecords
        .OrderByDescending(r => r.CreatedAt)
        .Take(50)
        .Select(r => new
        {
            id = r.Id,
            roundId = r.RoundId,
            transactionId = r.TransactionId,
            status = r.Status,
            ourInputCount = r.OurInputCount,
            totalInputCount = r.TotalInputCount,
            ourOutputCount = r.OurOutputCount,
            totalOutputCount = r.TotalOutputCount,
            participantCount = r.ParticipantCount,
            createdAt = r.CreatedAt
        })
        .ToListAsync();

    return Results.Ok(records);
}).WithTags("Dashboard");

app.MapGet("/api/dashboard/credential-flows/{roundId}", async (int roundId, WalletDbContext db) =>
{
    var events = await db.CredentialEvents
        .Where(e => e.CoinJoinRecordId == roundId)
        .ToListAsync();

    if (events.Count == 0)
        return Results.Ok(Array.Empty<object>());

    var tracker = new CredentialFlowTracker();
    var flows = tracker.AnalyzeFlows(events);

    return Results.Ok(flows.Select(f => new
    {
        inputAmountSat = f.InputAmountSat,
        inputAmountBtc = f.InputAmountSat / 100_000_000.0,
        outputAmountSat = f.OutputAmountSat,
        outputAmountBtc = f.OutputAmountSat / 100_000_000.0,
        changeAmountSat = f.ChangeAmountSat,
        feeSat = f.FeeSat,
        reissuanceSteps = f.ReissuanceSteps,
        graphDepth = f.GraphDepth,
        outputLabel = f.OutputLabel
    }));
}).WithTags("Dashboard");

app.MapPost("/api/dashboard/plan-send", async (WalletDbContext db, HttpContext ctx) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var body = await ctx.Request.ReadFromJsonAsync<SendPlanRequest>();
    if (body is null) return Results.BadRequest("Invalid request");

    try
    {
        var destination = BitcoinAddress.Create(body.Destination, network);
        var amount = Money.Satoshis(body.AmountSat);
        var feeRate = new FeeRate(Money.Satoshis(body.FeeRateSatPerVb), 1);
        var strategy = Enum.TryParse<CoinSelectionStrategy>(body.Strategy, true, out var s)
            ? s : CoinSelectionStrategy.PrivacyFirst;

        var txBuilder = new WalletTransactionBuilder(db, network);
        var plan = await txBuilder.PlanTransactionAsync(
            wallet.Id, destination.ScriptPubKey, amount, feeRate, strategy);

        return Results.Ok(new
        {
            txHex = plan.Transaction.ToHex(),
            inputCount = plan.InputCoins.Length,
            outputCount = plan.Transaction.Outputs.Count,
            estimatedFeeSat = plan.EstimatedFee.Satoshi,
            estimatedFeeBtc = plan.EstimatedFee.ToUnit(MoneyUnit.BTC),
            selectedUtxos = plan.SelectedUtxos.Select(u => new
            {
                txId = u.Utxo.TxId,
                outputIndex = u.Utxo.OutputIndex,
                amountSat = u.Utxo.AmountSat,
                effectiveScore = Math.Round(u.Score.EffectiveScore, 2)
            }),
            warnings = plan.Warnings
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).WithTags("Dashboard");

app.MapGet("/api/dashboard/transactions", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(Array.Empty<object>());

    // Get all UTXOs (spent and unspent) to build transaction history
    var utxos = await db.Utxos
        .Include(u => u.Address)
        .ThenInclude(a => a.Account)
        .Where(u => u.Address.Account.WalletId == wallet.Id)
        .OrderByDescending(u => u.ConfirmedHeight)
        .Take(100)
        .ToListAsync();

    // Group by transaction
    var txGroups = utxos.GroupBy(u => u.TxId).Select(g =>
    {
        var received = g.Sum(u => u.AmountSat);
        var isSpent = g.Any(u => u.SpentByTxId != null);
        var firstUtxo = g.First();

        return new
        {
            txId = g.Key,
            amountSat = received,
            amountBtc = received / 100_000_000.0,
            confirmedHeight = firstUtxo.ConfirmedHeight,
            isSpent,
            utxoCount = g.Count(),
            type = firstUtxo.SpentByTxId != null ? "spent" : "received"
        };
    }).ToList();

    return Results.Ok(txGroups);
}).WithTags("Dashboard");

// Health check
app.MapGet("/health", (KompaktorRoundManager manager) =>
{
    var rounds = manager.GetActiveRounds();
    return Results.Ok(new
    {
        status = "healthy",
        network = network.Name,
        activeRounds = rounds.Length,
        timestamp = DateTimeOffset.UtcNow
    });
}).WithTags("Health").ExcludeFromDescription();

app.MapOpenApi();

// Serve index.html as fallback
app.MapFallbackToFile("index.html");

app.Run();

record SendPlanRequest(string Destination, long AmountSat, long FeeRateSatPerVb = 2, string Strategy = "PrivacyFirst");
