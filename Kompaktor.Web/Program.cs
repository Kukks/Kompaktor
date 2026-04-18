using System.Security.Cryptography;
using System.Threading.RateLimiting;
using QRCoder;
using Kompaktor.Behaviors;
using Kompaktor.Client;
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
using Kompaktor.Web;
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

builder.Services.AddSingleton<DashboardEventBus>();
builder.Services.AddSingleton(network);
builder.Services.AddSingleton<MixingManager>();
builder.Services.AddSingleton<WalletSyncBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WalletSyncBackgroundService>());
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
    var scored = await selector.GetScoredUtxosAsync(wallet.Id, includeFrozen: true);

    var result = scored
        .OrderByDescending(s => s.Utxo.AmountSat)
        .Take(100)
        .Select(s => new
        {
            id = s.Utxo.Id,
            txId = s.Utxo.TxId,
            outputIndex = s.Utxo.OutputIndex,
            amountSat = s.Utxo.AmountSat,
            amountBtc = s.Utxo.AmountSat / 100_000_000.0,
            confirmedHeight = s.Utxo.ConfirmedHeight,
            isFrozen = s.Utxo.IsFrozen,
            rawAnonSet = s.Score.RawAnonSet,
            effectiveScore = Math.Round(s.Score.EffectiveScore, 2),
            coinJoinCount = s.Score.CoinJoinCount,
            confidence = s.Score.Confidence.ToString(),
            penalties = new
            {
                amount = Math.Round(s.Score.AmountPenalty, 3),
                cluster = Math.Round(s.Score.ClusterPenalty, 3),
                addressReuse = Math.Round(s.Score.ReusePenalty, 3)
            },
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

// Coin control: freeze/unfreeze UTXOs
app.MapPost("/api/coin-control/freeze/{utxoId}", async (int utxoId, WalletDbContext db, DashboardEventBus bus) =>
{
    var utxo = await db.Utxos.FindAsync(utxoId);
    if (utxo is null) return Results.NotFound();

    utxo.IsFrozen = true;
    await db.SaveChangesAsync();
    bus.Publish("utxos");
    return Results.Ok(new { utxoId, frozen = true });
}).WithTags("CoinControl");

app.MapPost("/api/coin-control/unfreeze/{utxoId}", async (int utxoId, WalletDbContext db, DashboardEventBus bus) =>
{
    var utxo = await db.Utxos.FindAsync(utxoId);
    if (utxo is null) return Results.NotFound();

    utxo.IsFrozen = false;
    await db.SaveChangesAsync();
    bus.Publish("utxos");
    return Results.Ok(new { utxoId, frozen = false });
}).WithTags("CoinControl");

// Coin control: batch freeze/unfreeze
app.MapPost("/api/coin-control/batch-freeze", async (WalletDbContext db, HttpContext ctx, DashboardEventBus bus) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<BatchFreezeRequest>();
    if (body is null) return Results.BadRequest("Invalid request");

    var utxos = await db.Utxos
        .Where(u => body.UtxoIds.Contains(u.Id))
        .ToListAsync();

    foreach (var utxo in utxos)
        utxo.IsFrozen = body.Freeze;

    await db.SaveChangesAsync();
    bus.Publish("utxos");
    return Results.Ok(new { updated = utxos.Count, frozen = body.Freeze });
}).WithTags("CoinControl");

// Labels: add label to a UTXO
app.MapPost("/api/coin-control/label/{utxoId}", async (int utxoId, WalletDbContext db, HttpContext ctx, DashboardEventBus bus) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<LabelRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Text))
        return Results.BadRequest("Label text required");

    var utxo = await db.Utxos.FindAsync(utxoId);
    if (utxo is null) return Results.NotFound();

    // Don't add duplicate labels
    var exists = await db.Labels.AnyAsync(l =>
        l.EntityType == "Utxo" && l.EntityId == utxoId.ToString() && l.Text == body.Text);
    if (exists) return Results.Ok(new { utxoId, label = body.Text, status = "already_exists" });

    db.Labels.Add(new LabelEntity
    {
        EntityType = "Utxo",
        EntityId = utxoId.ToString(),
        Text = body.Text
    });
    await db.SaveChangesAsync();
    bus.Publish("utxos");
    return Results.Ok(new { utxoId, label = body.Text, status = "added" });
}).WithTags("CoinControl");

// Labels: remove label from a UTXO
app.MapDelete("/api/coin-control/label/{utxoId}/{labelId}", async (int utxoId, int labelId, WalletDbContext db, DashboardEventBus bus) =>
{
    var label = await db.Labels.FindAsync(labelId);
    if (label is null || label.EntityType != "Utxo" || label.EntityId != utxoId.ToString())
        return Results.NotFound();

    db.Labels.Remove(label);
    await db.SaveChangesAsync();
    bus.Publish("utxos");
    return Results.Ok(new { utxoId, labelId, status = "removed" });
}).WithTags("CoinControl");

// Coin control: get UTXO detail with all labels and coinjoin history
app.MapGet("/api/coin-control/utxo/{utxoId}", async (int utxoId, WalletDbContext db) =>
{
    var utxo = await db.Utxos
        .Include(u => u.Address)
        .ThenInclude(a => a.Account)
        .FirstOrDefaultAsync(u => u.Id == utxoId);

    if (utxo is null) return Results.NotFound();

    var labels = await db.Labels
        .Where(l => l.EntityType == "Utxo" && l.EntityId == utxoId.ToString())
        .ToListAsync();

    var participations = await db.Set<CoinJoinParticipationEntity>()
        .Include(p => p.CoinJoinRecord)
        .Where(p => p.UtxoId == utxoId)
        .ToListAsync();

    return Results.Ok(new
    {
        id = utxo.Id,
        txId = utxo.TxId,
        outputIndex = utxo.OutputIndex,
        amountSat = utxo.AmountSat,
        amountBtc = utxo.AmountSat / 100_000_000.0,
        confirmedHeight = utxo.ConfirmedHeight,
        isFrozen = utxo.IsFrozen,
        isSpent = utxo.SpentByTxId != null,
        spentByTxId = utxo.SpentByTxId,
        keyPath = utxo.Address.KeyPath,
        isChange = utxo.Address.IsChange,
        labels = labels.Select(l => new { id = l.Id, text = l.Text, createdAt = l.CreatedAt }),
        coinjoinHistory = participations.Select(p => new
        {
            roundId = p.CoinJoinRecord.RoundId,
            role = p.Role,
            status = p.CoinJoinRecord.Status,
            createdAt = p.CoinJoinRecord.CreatedAt
        })
    });
}).WithTags("CoinControl");

// Wallet creation
app.MapPost("/api/wallet/create", async (WalletDbContext db, HttpContext ctx, DashboardEventBus bus) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<CreateWalletRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Passphrase))
        return Results.BadRequest("Passphrase required");

    if (await db.Wallets.AnyAsync())
        return Results.BadRequest("A wallet already exists");

    var hdWallet = await KompaktorHdWallet.CreateAsync(
        db, network, body.Name ?? "Default", body.Passphrase, body.WordCount ?? 12);

    // Return the mnemonic ONCE for user to write down
    var mnemonic = await hdWallet.ExportMnemonicAsync(body.Passphrase);

    bus.Publish("wallet");
    return Results.Ok(new
    {
        walletId = hdWallet.WalletId,
        mnemonic,
        wordCount = mnemonic.Split(' ').Length,
        message = "IMPORTANT: Write down your mnemonic words and store them safely. They will not be shown again."
    });
}).WithTags("Wallet");

// Wallet info
app.MapGet("/api/wallet/info", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(new { exists = false });

    var accountCount = await db.Accounts.CountAsync(a => a.WalletId == wallet.Id);
    var addressCount = await db.Addresses
        .Include(a => a.Account)
        .CountAsync(a => a.Account.WalletId == wallet.Id);

    return Results.Ok(new
    {
        exists = true,
        id = wallet.Id,
        name = wallet.Name,
        network = wallet.Network,
        createdAt = wallet.CreatedAt,
        accountCount,
        addressCount
    });
}).WithTags("Wallet");

// Get receive address
app.MapGet("/api/wallet/receive-address", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    // Find fresh P2TR address (purpose 86 preferred)
    var address = await db.Addresses
        .Include(a => a.Account)
        .Where(a => a.Account.WalletId == wallet.Id)
        .Where(a => !a.IsUsed && !a.IsExposed && !a.IsChange)
        .OrderByDescending(a => a.Account.Purpose) // Prefer P2TR (86)
        .ThenBy(a => a.Id)
        .FirstOrDefaultAsync();

    if (address is null) return Results.BadRequest("No fresh addresses available");

    var script = new Script(address.ScriptPubKey);
    var btcAddress = script.GetDestinationAddress(network);

    return Results.Ok(new
    {
        address = btcAddress?.ToString(),
        scriptHex = script.ToHex(),
        keyPath = address.KeyPath,
        purpose = address.Account.Purpose,
        type = address.Account.Purpose == 86 ? "P2TR" : "P2WPKH"
    });
}).WithTags("Wallet");

// QR code for receive address (BIP-21 URI)
app.MapGet("/api/wallet/receive-qr", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var address = await db.Addresses
        .Include(a => a.Account)
        .Where(a => a.Account.WalletId == wallet.Id)
        .Where(a => !a.IsUsed && !a.IsExposed && !a.IsChange)
        .OrderByDescending(a => a.Account.Purpose)
        .ThenBy(a => a.Id)
        .FirstOrDefaultAsync();

    if (address is null) return Results.BadRequest("No fresh addresses available");

    var script = new Script(address.ScriptPubKey);
    var btcAddress = script.GetDestinationAddress(network);
    var bip21 = $"bitcoin:{btcAddress}";

    using var qrGenerator = new QRCodeGenerator();
    var qrData = qrGenerator.CreateQrCode(bip21, QRCodeGenerator.ECCLevel.M);
    var svgQr = new SvgQRCode(qrData);
    var svg = svgQr.GetGraphic(4, "#e6edf3", "#0d1117", false);

    return Results.Content(svg, "image/svg+xml");
}).WithTags("Wallet");

// Wallet backup: export mnemonic (requires passphrase)
app.MapPost("/api/wallet/export-mnemonic", async (WalletDbContext db, HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<PassphraseRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Passphrase))
        return Results.BadRequest("Passphrase required");

    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    try
    {
        var mnemonic = MnemonicEncryption.Decrypt(wallet.EncryptedMnemonic, wallet.MnemonicSalt, body.Passphrase);
        return Results.Ok(new { mnemonic, wordCount = mnemonic.Split(' ').Length });
    }
    catch (System.Security.Cryptography.CryptographicException)
    {
        return Results.BadRequest("Wrong passphrase");
    }
}).WithTags("Wallet");

// Wallet restore from mnemonic
app.MapPost("/api/wallet/restore", async (WalletDbContext db, HttpContext ctx, DashboardEventBus bus) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<RestoreRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Mnemonic) || string.IsNullOrWhiteSpace(body.Passphrase))
        return Results.BadRequest("Mnemonic and passphrase required");

    // Check if wallet already exists
    if (await db.Wallets.AnyAsync())
        return Results.BadRequest("A wallet already exists. Delete the existing wallet first.");

    try
    {
        var hdWallet = await KompaktorHdWallet.RestoreAsync(
            db, network, body.Name ?? "Restored", body.Mnemonic.Trim(), body.Passphrase);
        bus.Publish("wallet");
        return Results.Ok(new
        {
            walletId = hdWallet.WalletId,
            message = "Wallet restored. Run a full sync to discover existing UTXOs."
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Invalid mnemonic: {ex.Message}");
    }
}).WithTags("Wallet");

// Coordinator stats for dashboard
app.MapGet("/api/coordinator/stats", (KompaktorRoundManager manager, KompaktorRoundOrchestrator orchestrator) =>
{
    var activeRounds = manager.GetActiveRoundOperators();
    var demand = orchestrator.DemandTracker;

    var roundDetails = activeRounds.Select(r => new
    {
        roundId = r.RoundEventCreated.RoundId,
        status = r.Status.ToString(),
        inputCount = r.Inputs.Count,
        outputCount = r.Outputs.Count,
        signatureCount = r.SignatureCount,
        maxInputs = r.RoundEventCreated.InputCount.Max,
        fillPercent = r.RoundEventCreated.InputCount.Max > 0
            ? Math.Round(100.0 * r.Inputs.Count / r.RoundEventCreated.InputCount.Max, 1)
            : 0,
        isBlameRound = r.RoundEventCreated.IsBlameRound
    }).ToList();

    return Results.Ok(new
    {
        network = network.Name,
        activeRounds = roundDetails.Count,
        roundsInRegistration = roundDetails.Count(r => r.status == "InputRegistration"),
        roundsInProgress = roundDetails.Count(r => r.status != "InputRegistration"),
        recentCompletedRounds = demand.RecentCompletedCount,
        recentFailedRounds = demand.RecentFailedCount,
        averageFillRate = Math.Round(demand.AverageFillRate * 100, 1),
        rounds = roundDetails,
        timestamp = DateTimeOffset.UtcNow
    });
}).WithTags("Coordinator");

// Privacy score distribution for visualization
app.MapGet("/api/dashboard/privacy-distribution", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null)
        return Results.Ok(new { tiers = Array.Empty<object>(), totalUtxos = 0 });

    var selector = new WalletCoinSelector(db);
    var scored = await selector.GetScoredUtxosAsync(wallet.Id);

    // Group into privacy tiers
    var tiers = new[]
    {
        new { name = "Unmixed", min = 0.0, max = 1.0, color = "#f85149" },
        new { name = "Low", min = 1.0, max = 3.0, color = "#db6d28" },
        new { name = "Medium", min = 3.0, max = 10.0, color = "#d29922" },
        new { name = "Good", min = 10.0, max = 50.0, color = "#3fb950" },
        new { name = "Excellent", min = 50.0, max = double.MaxValue, color = "#58a6ff" }
    };

    var distribution = tiers.Select(t =>
    {
        var inTier = scored.Where(s =>
            s.Score.EffectiveScore >= t.min && s.Score.EffectiveScore < t.max).ToList();
        return new
        {
            tier = t.name,
            color = t.color,
            count = inTier.Count,
            amountSat = inTier.Sum(s => s.Utxo.AmountSat),
            amountBtc = inTier.Sum(s => s.Utxo.AmountSat) / 100_000_000.0
        };
    }).ToList();

    return Results.Ok(new
    {
        tiers = distribution,
        totalUtxos = scored.Count
    });
}).WithTags("Dashboard");

// Privacy recommendations
app.MapGet("/api/dashboard/privacy-recommendations", async (WalletDbContext db, MixingManager mixer) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(new { recommendations = Array.Empty<object>() });

    var selector = new WalletCoinSelector(db);
    var scored = await selector.GetScoredUtxosAsync(wallet.Id, includeFrozen: true);

    var recommendations = new List<object>();

    if (scored.Count == 0)
    {
        recommendations.Add(new { priority = "info", title = "No UTXOs", message = "Fund your wallet to get started with privacy mixing." });
        return Results.Ok(new { recommendations });
    }

    // Check unmixed UTXOs
    var unmixed = scored.Count(s => s.Score.CoinJoinCount == 0);
    if (unmixed > 0)
    {
        var pct = (int)(100.0 * unmixed / scored.Count);
        recommendations.Add(new
        {
            priority = unmixed > scored.Count / 2 ? "high" : "medium",
            title = $"{unmixed} Unmixed UTXOs ({pct}%)",
            message = "These UTXOs have never participated in a CoinJoin. Enable auto-mixing to improve their anonymity set."
        });
    }

    // Check low-score UTXOs
    var lowScore = scored.Count(s => s.Score.EffectiveScore < 3 && s.Score.CoinJoinCount > 0);
    if (lowScore > 0)
    {
        recommendations.Add(new
        {
            priority = "medium",
            title = $"{lowScore} Low-Privacy UTXOs",
            message = "These UTXOs have been mixed but still have a low anonymity score. Additional mixing rounds would improve privacy."
        });
    }

    // Check for large value concentration
    var totalSat = scored.Sum(s => s.Utxo.AmountSat);
    var largestUtxo = scored.Max(s => s.Utxo.AmountSat);
    if (totalSat > 0 && (double)largestUtxo / totalSat > 0.5)
    {
        recommendations.Add(new
        {
            priority = "medium",
            title = "High Value Concentration",
            message = $"One UTXO holds {(double)largestUtxo / totalSat * 100:F0}% of your total balance. Consider splitting through CoinJoin rounds to reduce amount-based fingerprinting."
        });
    }

    // Check cluster-linked coins (external entity labels)
    var clusterLinked = scored.Count(s => s.Score.ClusterPenalty < 1.0);
    if (clusterLinked > 0)
    {
        recommendations.Add(new
        {
            priority = "medium",
            title = $"{clusterLinked} Cluster-Linked UTXO{(clusterLinked > 1 ? "s" : "")}",
            message = "These coins are labeled with external entities (exchanges, KYC services). Their anonymity set is reduced because the entity knows your identity. Mix thoroughly before spending."
        });
    }

    // Check address reuse
    var reusedAddressCoins = scored.Where(s => s.Score.ReusePenalty < 1.0).ToList();
    if (reusedAddressCoins.Count > 0)
    {
        var reusedAddresses = reusedAddressCoins.Select(s => s.Utxo.AddressId).Distinct().Count();
        recommendations.Add(new
        {
            priority = "high",
            title = $"Address Reuse Detected ({reusedAddresses} address{(reusedAddresses > 1 ? "es" : "")})",
            message = $"{reusedAddressCoins.Count} UTXO{(reusedAddressCoins.Count > 1 ? "s" : "")} sit on reused addresses. " +
                      "Address reuse destroys privacy gains from CoinJoin by linking transactions. " +
                      "Mix these coins and avoid sending to already-used addresses."
        });
    }

    // Check frozen UTXOs
    var frozenCount = scored.Count(s => s.Utxo.IsFrozen);
    if (frozenCount > 0)
    {
        recommendations.Add(new
        {
            priority = "info",
            title = $"{frozenCount} Frozen UTXOs",
            message = "Frozen UTXOs are excluded from coin selection. Review your frozen coins periodically."
        });
    }

    // Check mixing status
    if (!mixer.IsRunning && unmixed > 0)
    {
        recommendations.Add(new
        {
            priority = "high",
            title = "Auto-Mix Not Running",
            message = "Enable auto-mixing to continuously improve your wallet's privacy. Unmixed UTXOs are vulnerable to chain analysis."
        });
    }

    // Good privacy
    var excellentCount = scored.Count(s => s.Score.EffectiveScore >= 50);
    if (excellentCount > 0)
    {
        recommendations.Add(new
        {
            priority = "success",
            title = $"{excellentCount} Excellent Privacy UTXOs",
            message = "These UTXOs have strong anonymity sets. They're ready for privacy-preserving spending."
        });
    }

    return Results.Ok(new { recommendations });
}).WithTags("Dashboard");

// Privacy trend history — returns time-series data for dashboard chart
app.MapGet("/api/dashboard/privacy-history", async (WalletDbContext db, HttpContext ctx) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(Array.Empty<object>());

    // Default last 30 days, configurable via query params
    var daysStr = ctx.Request.Query["days"].FirstOrDefault();
    var days = int.TryParse(daysStr, out var d) && d is > 0 and <= 365 ? d : 30;
    var since = DateTimeOffset.UtcNow.AddDays(-days);

    var snapshots = await db.PrivacySnapshots
        .Where(s => s.WalletId == wallet.Id && s.Timestamp >= since)
        .OrderBy(s => s.Timestamp)
        .Select(s => new
        {
            timestamp = s.Timestamp,
            totalUtxos = s.TotalUtxos,
            totalAmountSat = s.TotalAmountSat,
            averageAnonScore = Math.Round(s.AverageAnonScore, 2),
            minAnonScore = Math.Round(s.MinAnonScore, 2),
            maxAnonScore = Math.Round(s.MaxAnonScore, 2),
            mixedUtxoCount = s.MixedUtxoCount,
            unmixedUtxoCount = s.UnmixedUtxoCount,
            coinJoinRoundNumber = s.CoinJoinRoundNumber
        })
        .ToListAsync();

    return Results.Ok(snapshots);
}).WithTags("Dashboard");

// Interactive Payments: create, list, cancel
app.MapGet("/api/payments", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(Array.Empty<object>());

    var payments = await db.PendingPayments
        .Where(p => p.WalletId == wallet.Id)
        .OrderByDescending(p => p.CreatedAt)
        .Take(50)
        .Select(p => new
        {
            p.Id, p.Direction, p.AmountSat,
            amountBtc = p.AmountSat / 100_000_000.0,
            p.Destination, p.Status, p.IsInteractive, p.IsUrgent,
            p.Label, p.CompletedTxId, p.ProofJson, p.RetryCount,
            p.CreatedAt, p.CompletedAt, p.ExpiresAt
        })
        .ToListAsync();

    return Results.Ok(payments);
}).WithTags("Payments");

app.MapPost("/api/payments/send", async (WalletDbContext db, HttpContext ctx, DashboardEventBus bus) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var body = await ctx.Request.ReadFromJsonAsync<CreatePaymentRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Destination) || body.AmountSat <= 0)
        return Results.BadRequest("Destination and amount required");

    try
    {
        BitcoinAddress.Create(body.Destination, network);
    }
    catch
    {
        return Results.BadRequest("Invalid Bitcoin address");
    }

    try
    {
        var manager = new WalletPaymentManager(db, wallet.Id, network);
        var expiry = body.ExpiryMinutes.HasValue ? TimeSpan.FromMinutes(body.ExpiryMinutes.Value) : (TimeSpan?)null;
        var entity = await manager.CreateOutboundPaymentAsync(
            body.Destination, body.AmountSat, body.Interactive, body.Urgent, body.Label, expiry);

        bus.Publish("payments");
        return Results.Ok(new
        {
            entity.Id, entity.Direction, entity.AmountSat,
            amountBtc = entity.AmountSat / 100_000_000.0,
            entity.Destination, entity.Status, entity.IsInteractive,
            kompaktorPubKey = entity.KompaktorKeyHex,
            entity.Label, entity.CreatedAt
        });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).WithTags("Payments");

app.MapPost("/api/payments/receive", async (WalletDbContext db, HttpContext ctx, DashboardEventBus bus) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var body = await ctx.Request.ReadFromJsonAsync<CreateReceiveRequest>();
    if (body is null || body.AmountSat <= 0)
        return Results.BadRequest("Amount required");

    try
    {
        var manager = new WalletPaymentManager(db, wallet.Id, network);
        var recvExpiry = body.ExpiryMinutes.HasValue ? TimeSpan.FromMinutes(body.ExpiryMinutes.Value) : (TimeSpan?)null;
        var entity = await manager.CreateInboundPaymentAsync(body.AmountSat, body.Label, recvExpiry);

        // Build BIP21 URI with Kompaktor extension parameter
        var bip21 = $"bitcoin:{entity.Destination}?amount={entity.AmountSat / 100_000_000.0:F8}";
        if (entity.KompaktorKeyHex is not null)
            bip21 += $"&kompaktor={entity.KompaktorKeyHex.ToLower()}";

        bus.Publish("payments");
        return Results.Ok(new
        {
            entity.Id, entity.Direction, entity.AmountSat,
            amountBtc = entity.AmountSat / 100_000_000.0,
            entity.Destination, entity.Status, entity.IsInteractive,
            bip21Uri = bip21,
            kompaktorKey = entity.KompaktorKeyHex,
            entity.Label, entity.CreatedAt
        });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).WithTags("Payments");

app.MapDelete("/api/payments/{paymentId}", async (string paymentId, WalletDbContext db, DashboardEventBus bus) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var manager = new WalletPaymentManager(db, wallet.Id, network);
    var cancelled = await manager.CancelPaymentAsync(paymentId);
    if (cancelled) bus.Publish("payments");
    return cancelled ? Results.Ok(new { paymentId, status = "cancelled" }) : Results.NotFound();
}).WithTags("Payments");

app.MapGet("/api/payments/{paymentId}/qr", async (string paymentId, WalletDbContext db) =>
{
    var entity = await db.PendingPayments.FindAsync(paymentId);
    if (entity is null) return Results.NotFound();

    var bip21 = $"bitcoin:{entity.Destination}?amount={entity.AmountSat / 100_000_000.0:F8}";
    if (entity.IsInteractive && entity.KompaktorKeyHex is not null)
        bip21 += $"&kompaktor={entity.KompaktorKeyHex.ToLower()}";

    using var qrGenerator = new QRCodeGenerator();
    var qrData = qrGenerator.CreateQrCode(bip21, QRCodeGenerator.ECCLevel.M);
    var svgQr = new SvgQRCode(qrData);
    var svg = svgQr.GetGraphic(4, "#e6edf3", "#0d1117", false);

    return Results.Content(svg, "image/svg+xml");
}).WithTags("Payments");

app.MapGet("/api/payments/{paymentId}/status", async (string paymentId, WalletDbContext db) =>
{
    var entity = await db.PendingPayments.FindAsync(paymentId);
    if (entity is null) return Results.NotFound();

    return Results.Ok(new
    {
        entity.Id, entity.Direction, entity.AmountSat,
        amountBtc = entity.AmountSat / 100_000_000.0,
        entity.Destination, entity.Status, entity.IsInteractive, entity.IsUrgent,
        entity.RetryCount, entity.Label, entity.CompletedTxId, entity.ProofJson,
        entity.CreatedAt, entity.CompletedAt, entity.ExpiresAt
    });
}).WithTags("Payments");

app.MapGet("/api/payments/stats", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(new { total = 0 });

    var payments = await db.PendingPayments
        .Where(p => p.WalletId == wallet.Id)
        .ToListAsync();

    var completed = payments.Where(p => p.Status == "Completed").ToList();
    var pending = payments.Where(p => p.Status is "Pending" or "Reserved" or "Committed").ToList();
    var failed = payments.Where(p => p.Status == "Failed").ToList();

    return Results.Ok(new
    {
        total = payments.Count,
        completedCount = completed.Count,
        pendingCount = pending.Count,
        failedCount = failed.Count,
        totalSentSat = completed.Where(p => p.Direction == "Outbound").Sum(p => p.AmountSat),
        totalReceivedSat = completed.Where(p => p.Direction == "Inbound").Sum(p => p.AmountSat),
        averageRetries = completed.Count > 0 ? completed.Average(p => p.RetryCount) : 0,
        successRate = payments.Count(p => p.Status is "Completed" or "Failed") > 0
            ? (double)completed.Count / payments.Count(p => p.Status is "Completed" or "Failed") * 100
            : 0
    });
}).WithTags("Payments");

app.MapGet("/api/payments/export", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok("No wallet");

    var payments = await db.PendingPayments
        .Where(p => p.WalletId == wallet.Id)
        .OrderByDescending(p => p.CreatedAt)
        .ToListAsync();

    var csv = new System.Text.StringBuilder();
    csv.AppendLine("Id,Direction,AmountSat,AmountBtc,Destination,Status,Interactive,Urgent,Retries,Label,TxId,CreatedAt,CompletedAt,ExpiresAt");
    foreach (var p in payments)
    {
        var label = (p.Label ?? "").Replace("\"", "\"\"");
        csv.AppendLine($"{p.Id},{p.Direction},{p.AmountSat},{p.AmountSat / 100_000_000.0:F8},{p.Destination},{p.Status},{p.IsInteractive},{p.IsUrgent},{p.RetryCount},\"{label}\",{p.CompletedTxId ?? ""},{p.CreatedAt:o},{p.CompletedAt?.ToString("o") ?? ""},{p.ExpiresAt?.ToString("o") ?? ""}");
    }

    return Results.Text(csv.ToString(), "text/csv", System.Text.Encoding.UTF8);
}).WithTags("Payments");

// Address book CRUD
app.MapGet("/api/address-book", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(Array.Empty<object>());

    var entries = await db.AddressBook
        .Where(a => a.WalletId == wallet.Id)
        .OrderByDescending(a => a.CreatedAt)
        .Select(a => new { a.Id, a.Label, a.Address, a.CreatedAt })
        .ToListAsync();

    return Results.Ok(entries);
}).WithTags("AddressBook");

app.MapPost("/api/address-book", async (WalletDbContext db, HttpContext ctx) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var body = await ctx.Request.ReadFromJsonAsync<AddressBookRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Address) || string.IsNullOrWhiteSpace(body.Label))
        return Results.BadRequest("Label and address required");

    // Validate address
    try { BitcoinAddress.Create(body.Address.Trim(), network); }
    catch { return Results.BadRequest("Invalid Bitcoin address"); }

    // Prevent duplicates
    var exists = await db.AddressBook.AnyAsync(a =>
        a.WalletId == wallet.Id && a.Address == body.Address.Trim());
    if (exists) return Results.BadRequest("Address already in address book");

    var entry = new AddressBookEntry
    {
        WalletId = wallet.Id,
        Label = body.Label.Trim(),
        Address = body.Address.Trim()
    };
    db.AddressBook.Add(entry);
    await db.SaveChangesAsync();

    return Results.Ok(new { entry.Id, entry.Label, entry.Address });
}).WithTags("AddressBook");

app.MapDelete("/api/address-book/{entryId}", async (int entryId, WalletDbContext db) =>
{
    var entry = await db.AddressBook.FindAsync(entryId);
    if (entry is null) return Results.NotFound();

    db.AddressBook.Remove(entry);
    await db.SaveChangesAsync();
    return Results.Ok(new { deleted = entryId });
}).WithTags("AddressBook");

// Send (sign + broadcast) a planned transaction
app.MapPost("/api/dashboard/send", async (WalletDbContext db, IBlockchainBackend chain, HttpContext ctx, DashboardEventBus bus) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var body = await ctx.Request.ReadFromJsonAsync<SendRequest>();
    if (body is null) return Results.BadRequest("Invalid request");
    if (string.IsNullOrWhiteSpace(body.Passphrase))
        return Results.BadRequest("Passphrase required to sign the transaction");

    try
    {
        var destination = BitcoinAddress.Create(body.Destination, network);
        var amount = Money.Satoshis(body.AmountSat);
        var feeRate = new FeeRate(Money.Satoshis(body.FeeRateSatPerVb), 1);
        var strategy = Enum.TryParse<CoinSelectionStrategy>(body.Strategy, true, out var s)
            ? s : CoinSelectionStrategy.PrivacyFirst;

        // Plan the transaction
        var txBuilder = new WalletTransactionBuilder(db, network);
        var plan = await txBuilder.PlanTransactionAsync(
            wallet.Id, destination.ScriptPubKey, amount, feeRate, strategy);

        // Open wallet and sign
        var hdWallet = await KompaktorHdWallet.OpenAsync(db, wallet.Id, network, body.Passphrase);

        var tx = plan.Transaction;
        for (var i = 0; i < tx.Inputs.Count; i++)
        {
            var input = tx.Inputs[i];
            var coin = plan.InputCoins.First(c => c.Outpoint == input.PrevOut);
            var witness = await hdWallet.GenerateWitness(coin, tx, plan.InputCoins);
            input.WitScript = witness;
        }

        // Broadcast
        var txId = await chain.BroadcastAsync(tx);

        // Mark UTXOs as spent and record the transaction
        foreach (var coin in plan.InputCoins)
        {
            var utxo = await db.Utxos.FirstOrDefaultAsync(u =>
                u.TxId == coin.Outpoint.Hash.ToString() && u.OutputIndex == (int)coin.Outpoint.N);
            if (utxo is not null) utxo.SpentByTxId = txId.ToString();
        }
        await db.SaveChangesAsync();

        bus.Publish("utxos");
        bus.Publish("wallet");

        return Results.Ok(new
        {
            txId = txId.ToString(),
            feeSat = plan.EstimatedFee.Satoshi,
            inputCount = plan.InputCoins.Length,
            outputCount = tx.Outputs.Count
        });
    }
    catch (System.Security.Cryptography.CryptographicException)
    {
        return Results.BadRequest("Wrong passphrase");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).WithTags("Dashboard");

// Export transaction plan as PSBT for hardware wallet signing
app.MapPost("/api/dashboard/export-psbt", async (WalletDbContext db, HttpContext ctx) =>
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

        // Create PSBT from the unsigned transaction
        var psbt = PSBT.FromTransaction(plan.Transaction, network);

        // Add witness UTXO data for each input so hardware wallets can verify
        for (var i = 0; i < plan.InputCoins.Length; i++)
        {
            psbt.Inputs[i].WitnessUtxo = plan.InputCoins[i].TxOut;
        }

        return Results.Ok(new
        {
            psbt = psbt.ToBase64(),
            inputCount = plan.InputCoins.Length,
            outputCount = plan.Transaction.Outputs.Count,
            estimatedFeeSat = plan.EstimatedFee.Satoshi,
            warnings = plan.Warnings
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).WithTags("Dashboard");

// Broadcast a signed PSBT
app.MapPost("/api/dashboard/broadcast-psbt", async (WalletDbContext db, IBlockchainBackend chain, HttpContext ctx, DashboardEventBus bus) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<BroadcastPsbtRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.SignedPsbt))
        return Results.BadRequest("Signed PSBT required");

    try
    {
        var psbt = PSBT.Parse(body.SignedPsbt, network);

        if (!psbt.TryFinalize(out _))
            return Results.BadRequest("PSBT is not fully signed — all inputs must be signed before broadcast");

        var tx = psbt.ExtractTransaction();
        var txId = await chain.BroadcastAsync(tx);

        // Mark spent UTXOs
        foreach (var input in tx.Inputs)
        {
            var utxo = await db.Utxos.FirstOrDefaultAsync(u =>
                u.TxId == input.PrevOut.Hash.ToString() && u.OutputIndex == (int)input.PrevOut.N);
            if (utxo is not null) utxo.SpentByTxId = txId.ToString();
        }
        await db.SaveChangesAsync();

        bus.Publish("utxos");
        bus.Publish("wallet");

        return Results.Ok(new
        {
            txId = txId.ToString(),
            inputCount = tx.Inputs.Count,
            outputCount = tx.Outputs.Count
        });
    }
    catch (FormatException)
    {
        return Results.BadRequest("Invalid PSBT format");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).WithTags("Dashboard");

// Fee estimation for send form
app.MapGet("/api/dashboard/fee-estimates", async (IBlockchainBackend chain) =>
{
    var targets = new[] { 1, 3, 6, 25 };
    var estimates = new List<object>();

    foreach (var target in targets)
    {
        try
        {
            var feeRate = await chain.EstimateFeeAsync(target);
            var satPerVb = Math.Max(1, (long)Math.Ceiling(feeRate.SatoshiPerByte));
            estimates.Add(new { confirmationTarget = target, satPerVb });
        }
        catch
        {
            // Backend may not support all targets; skip failures
        }
    }

    // If no estimates available, return sensible defaults
    if (estimates.Count == 0)
    {
        estimates.Add(new { confirmationTarget = 1, satPerVb = 10L });
        estimates.Add(new { confirmationTarget = 6, satPerVb = 2L });
        estimates.Add(new { confirmationTarget = 25, satPerVb = 1L });
    }

    return Results.Ok(estimates);
}).WithTags("Dashboard");

// Blockchain info
app.MapGet("/api/blockchain/info", async (IBlockchainBackend chain) =>
{
    int? height = null;
    try { height = await chain.GetBlockHeightAsync(); }
    catch { /* backend may be disconnected */ }

    return Results.Ok(new
    {
        connected = chain.IsConnected,
        blockHeight = height,
        network = network.Name
    });
}).WithTags("Blockchain");

// Wallet data export (labels, address book, coinjoin history)
app.MapGet("/api/wallet/export", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var labels = await db.Labels.ToListAsync();
    var addressBook = await db.AddressBook.Where(a => a.WalletId == wallet.Id).ToListAsync();
    var coinjoins = await db.CoinJoinRecords.Include(c => c.Participations).ToListAsync();

    var export = new
    {
        version = 1,
        exportedAt = DateTimeOffset.UtcNow,
        wallet = new { wallet.Name, wallet.Network, wallet.CreatedAt },
        labels = labels.Select(l => new { l.EntityType, l.EntityId, l.Text }),
        addressBook = addressBook.Select(a => new { a.Label, a.Address }),
        coinjoinHistory = coinjoins.Select(c => new
        {
            c.RoundId, c.Status, c.OurInputCount, c.TotalInputCount,
            c.OurOutputCount, c.TotalOutputCount, c.ParticipantCount, c.CreatedAt
        })
    };

    return Results.Json(export, contentType: "application/json");
}).WithTags("Wallet");

// Wallet data import (labels, address book)
app.MapPost("/api/wallet/import", async (WalletDbContext db, HttpContext ctx, DashboardEventBus bus) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var body = await ctx.Request.ReadFromJsonAsync<System.Text.Json.JsonElement>();
    var imported = new { labels = 0, addressBook = 0 };
    int labelsAdded = 0, abAdded = 0;

    // Import labels
    if (body.TryGetProperty("labels", out var labelsArr) && labelsArr.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        foreach (var l in labelsArr.EnumerateArray())
        {
            var entityType = l.GetProperty("entityType").GetString() ?? "";
            var entityId = l.GetProperty("entityId").GetString() ?? "";
            var text = l.GetProperty("text").GetString() ?? "";
            if (string.IsNullOrEmpty(text)) continue;

            var exists = await db.Labels.AnyAsync(x =>
                x.EntityType == entityType && x.EntityId == entityId && x.Text == text);
            if (!exists)
            {
                db.Labels.Add(new LabelEntity { EntityType = entityType, EntityId = entityId, Text = text });
                labelsAdded++;
            }
        }
    }

    // Import address book
    if (body.TryGetProperty("addressBook", out var abArr) && abArr.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        foreach (var a in abArr.EnumerateArray())
        {
            var label = a.GetProperty("label").GetString() ?? "";
            var address = a.GetProperty("address").GetString() ?? "";
            if (string.IsNullOrEmpty(address)) continue;

            var exists = await db.AddressBook.AnyAsync(x =>
                x.WalletId == wallet.Id && x.Address == address);
            if (!exists)
            {
                db.AddressBook.Add(new AddressBookEntry { WalletId = wallet.Id, Label = label, Address = address });
                abAdded++;
            }
        }
    }

    await db.SaveChangesAsync();
    bus.Publish("utxos");
    return Results.Ok(new { labelsImported = labelsAdded, addressBookImported = abAdded });
}).WithTags("Wallet");

// Mixing statistics summary
app.MapGet("/api/mixing/statistics", async (WalletDbContext db) =>
{
    var records = await db.CoinJoinRecords.ToListAsync();
    var completed = records.Where(r => r.Status == "Completed").ToList();
    var failed = records.Where(r => r.Status != "Completed").ToList();

    var totalInputsSat = completed.Sum(r =>
    {
        // Approximate from output values if available
        return r.OutputValuesSat.Length > 0 ? r.OutputValuesSat.Sum() : 0L;
    });

    return Results.Ok(new
    {
        totalRounds = records.Count,
        completedRounds = completed.Count,
        failedRounds = failed.Count,
        successRate = records.Count > 0 ? Math.Round(100.0 * completed.Count / records.Count, 1) : 0.0,
        totalOurInputs = completed.Sum(r => r.OurInputCount),
        totalOurOutputs = completed.Sum(r => r.OurOutputCount),
        totalParticipants = completed.Sum(r => r.ParticipantCount),
        averageParticipantsPerRound = completed.Count > 0 ? Math.Round((double)completed.Sum(r => r.ParticipantCount) / completed.Count, 1) : 0.0,
        firstRound = records.Min(r => (DateTimeOffset?)r.CreatedAt),
        lastRound = records.Max(r => (DateTimeOffset?)r.CreatedAt)
    });
}).WithTags("Mixing");

// Wallet sync status
app.MapGet("/api/wallet/sync-status", (WalletSyncBackgroundService sync) =>
{
    return Results.Ok(new
    {
        syncing = sync.IsSyncing,
        monitoring = sync.IsMonitoring,
        lastSyncTime = sync.LastSyncTime,
        lastSyncUtxoCount = sync.LastSyncUtxoCount
    });
}).WithTags("Wallet");

app.MapPost("/api/wallet/resync", (WalletSyncBackgroundService sync) =>
{
    if (sync.IsSyncing)
        return Results.Ok(new { status = "already syncing" });

    sync.TriggerResync();
    return Results.Ok(new { status = "resync triggered" });
}).WithTags("Wallet");

// Auto-mixing: start/stop/status
app.MapGet("/api/mixing/status", (MixingManager mixer) =>
{
    return Results.Ok(new
    {
        running = mixer.IsRunning,
        completedRounds = mixer.CompletedRounds,
        failedRounds = mixer.FailedRounds,
        coordinatorUrl = mixer.CoordinatorUri?.ToString(),
        activeRoundPhase = mixer.ActiveRoundPhase,
        activeRoundInputs = mixer.ActiveRoundInputCount,
        torEnabled = mixer.TorEnabled,
        allowUnconfirmedCoinjoinReuse = mixer.AllowUnconfirmedCoinjoinReuse,
        mixingOutpoints = mixer.ActiveMixingOutpoints,
        lastRoundStatus = mixer.LastRoundStatus,
        lastRoundFailureReason = mixer.LastRoundFailureReason
    });
}).WithTags("Mixing");

app.MapPost("/api/mixing/start", async (MixingManager mixer, HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<MixingStartRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Passphrase))
        return Results.BadRequest("Passphrase required");

    try
    {
        // Use custom coordinator URL if provided, otherwise connect to ourselves
        var coordinatorUri = !string.IsNullOrWhiteSpace(body.CoordinatorUrl)
            ? new Uri(body.CoordinatorUrl)
            : new Uri($"{ctx.Request.Scheme}://localhost:{ctx.Connection.LocalPort}");

        Kompaktor.TorOptions? torOptions = null;
        if (!string.IsNullOrWhiteSpace(body.TorSocksHost))
        {
            torOptions = new Kompaktor.TorOptions
            {
                SocksHost = body.TorSocksHost,
                SocksPort = body.TorSocksPort ?? 9050
            };
        }

        var result = await mixer.StartAsync(body.Passphrase, coordinatorUri, torOptions, body.AllowUnconfirmedCoinjoinReuse);
        return Results.Ok(new { status = result, coordinator = coordinatorUri.ToString(), torEnabled = torOptions is not null, allowUnconfirmedCoinjoinReuse = body.AllowUnconfirmedCoinjoinReuse });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).WithTags("Mixing");

app.MapPost("/api/mixing/stop", async (MixingManager mixer) =>
{
    var result = await mixer.StopAsync();
    return Results.Ok(new { status = result });
}).WithTags("Mixing");

// SSE: real-time dashboard event stream
app.MapGet("/api/dashboard/events", async (HttpContext ctx, DashboardEventBus bus, CancellationToken ct) =>
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";

    var reader = bus.Subscribe();
    try
    {
        // Send initial heartbeat so the client knows the connection is live
        await ctx.Response.WriteAsync("event: connected\ndata: {}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);

        await foreach (var eventType in reader.ReadAllAsync(ct))
        {
            await ctx.Response.WriteAsync($"event: {eventType}\ndata: {{}}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { /* client disconnected */ }
    finally
    {
        bus.Unsubscribe(reader);
    }
}).ExcludeFromDescription();

// Background task: publish round state changes every 2s (aligned with orchestrator tick)
_ = Task.Run(async () =>
{
    var bus = app.Services.GetRequiredService<DashboardEventBus>();
    var manager = app.Services.GetRequiredService<KompaktorRoundManager>();
    var lastRoundState = "";

    while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(2000, app.Lifetime.ApplicationStopping);
        }
        catch (OperationCanceledException) { break; }

        if (bus.SubscriberCount == 0) continue;

        // Build a lightweight fingerprint of current round state
        var rounds = manager.GetActiveRoundOperators();
        var snapshot = string.Join("|", rounds.Select(r =>
            $"{r.RoundEventCreated.RoundId}:{r.Status}:{r.Inputs.Count}:{r.Outputs.Count}:{r.SignatureCount}"));

        if (snapshot != lastRoundState)
        {
            lastRoundState = snapshot;
            bus.Publish("rounds");
        }
    }
});

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
record BatchFreezeRequest(int[] UtxoIds, bool Freeze);
record LabelRequest(string Text);
record PassphraseRequest(string Passphrase);
record RestoreRequest(string Mnemonic, string Passphrase, string? Name = null);
record CreateWalletRequest(string Passphrase, string? Name = null, int? WordCount = null);
record MixingStartRequest(string Passphrase, string? CoordinatorUrl = null, string? TorSocksHost = null, int? TorSocksPort = null, bool AllowUnconfirmedCoinjoinReuse = false);
record AddressBookRequest(string Label, string Address);
record SendRequest(string Destination, long AmountSat, long FeeRateSatPerVb = 2, string Strategy = "PrivacyFirst", string Passphrase = "");
record BroadcastPsbtRequest(string SignedPsbt);
record CreatePaymentRequest(string Destination, long AmountSat, bool Interactive = true, bool Urgent = false, string? Label = null, int? ExpiryMinutes = null);
record CreateReceiveRequest(long AmountSat, string? Label = null, int? ExpiryMinutes = null);
