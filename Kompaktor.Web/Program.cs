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

// Wallet database — resolve path lazily so test-time config overrides apply
builder.Services.AddDbContext<WalletDbContext>((sp, opt) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var walletPath = cfg["Wallet:Path"] ?? "./wallet.db";
    opt.UseSqlite($"DataSource={walletPath}");
});

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
builder.Services.AddSingleton<IPriceService, CoingeckoPriceService>();
builder.Services.AddOpenApi();

var app = builder.Build();

// Ensure database exists
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// Connect blockchain (resolve from DI so tests can swap the backend)
await app.Services.GetRequiredService<IBlockchainBackend>().ConnectAsync();

app.UseRateLimiter();

// Static files (dashboard HTML/JS/CSS)
app.UseStaticFiles();

// Map coordinator API
app.MapKompaktorEndpoints();

// Map wallet dashboard API
app.MapGet("/api/dashboard/summary", async (WalletDbContext db) =>
{
    var wallets = await db.Wallets.CountAsync();
    var unspent = db.Utxos.Where(u => u.SpentByTxId == null);
    var utxos = await unspent.CountAsync();
    var totalSats = await unspent.SumAsync(u => u.AmountSat);
    var confirmedSats = await unspent.Where(u => u.ConfirmedHeight != null).SumAsync(u => u.AmountSat);
    var unconfirmedSats = await unspent.Where(u => u.ConfirmedHeight == null).SumAsync(u => u.AmountSat);
    var coinjoins = await db.CoinJoinRecords.CountAsync();

    return Results.Ok(new
    {
        walletCount = wallets,
        unspentUtxoCount = utxos,
        totalBalanceSats = totalSats,
        totalBalanceBtc = totalSats / 100_000_000.0,
        confirmedBalanceSats = confirmedSats,
        confirmedBalanceBtc = confirmedSats / 100_000_000.0,
        unconfirmedBalanceSats = unconfirmedSats,
        unconfirmedBalanceBtc = unconfirmedSats / 100_000_000.0,
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
    // SQLite's EF Core provider can't ORDER BY DateTimeOffset — pull then sort in memory.
    var records = (await db.CoinJoinRecords.ToListAsync())
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
        .ToList();

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

// Sweep: send all available funds to a destination (minus fees)
app.MapPost("/api/dashboard/sweep", async (WalletDbContext db, HttpContext ctx) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var body = await ctx.Request.ReadFromJsonAsync<SweepRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Destination))
        return Results.BadRequest("Destination required");

    try
    {
        var destination = BitcoinAddress.Create(body.Destination, network);
        var feeRate = new FeeRate(Money.Satoshis(body.FeeRateSatPerVb > 0 ? body.FeeRateSatPerVb : 2), 1);

        // Get all spendable UTXOs
        var utxos = await db.Utxos
            .Include(u => u.Address)
            .ThenInclude(a => a.Account)
            .Where(u => u.Address.Account.WalletId == wallet.Id)
            .Where(u => u.SpentByTxId == null && !u.IsFrozen)
            .Where(u => u.ConfirmedHeight != null || u.IsCoinJoinOutput)
            .ToListAsync();

        if (utxos.Count == 0)
            return Results.BadRequest("No spendable UTXOs available");

        var totalSat = utxos.Sum(u => u.AmountSat);

        // Build transaction spending all UTXOs to destination
        var coins = utxos.Select(u => new Coin(
            new OutPoint(uint256.Parse(u.TxId), u.OutputIndex),
            new TxOut(Money.Satoshis(u.AmountSat), new Script(u.ScriptPubKey))
        )).ToArray();

        var builder = network.CreateTransactionBuilder();
        builder.AddCoins(coins);
        builder.SendAllRemaining(destination.ScriptPubKey);
        builder.SendEstimatedFees(feeRate);
        builder.OptInRBF = true;

        var tx = builder.BuildTransaction(sign: false);
        var fee = builder.EstimateFees(tx, feeRate);
        var sendAmount = totalSat - fee.Satoshi;

        if (sendAmount <= 546)
            return Results.BadRequest($"After fees ({fee.Satoshi} sat), remaining amount ({sendAmount} sat) is below dust limit");

        // Create PSBT for signing
        var psbt = PSBT.FromTransaction(tx, network);
        for (int i = 0; i < tx.Inputs.Count; i++)
        {
            var coin = coins.FirstOrDefault(c => c.Outpoint == tx.Inputs[i].PrevOut);
            if (coin is not null)
                psbt.Inputs[i].WitnessUtxo = coin.TxOut;
        }

        return Results.Ok(new
        {
            txHex = tx.ToHex(),
            psbt = psbt.ToBase64(),
            inputCount = coins.Length,
            totalInputSat = totalSat,
            feeSat = fee.Satoshi,
            sendAmountSat = sendAmount,
            sendAmountBtc = sendAmount / 100_000_000.0,
            destination = destination.ToString(),
            warning = "This sends ALL wallet funds. Review carefully before signing."
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Sweep failed: {ex.Message}");
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

// Transaction detail: all UTXOs we own that this tx touched, plus labels,
// confirmation state, and any linked coinjoin-round record.
app.MapGet("/api/dashboard/transactions/{txId}", async (string txId, WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.NotFound();

    // Outputs of txId that paid *to* this wallet.
    var received = await db.Utxos
        .Include(u => u.Address).ThenInclude(a => a.Account)
        .Where(u => u.Address.Account.WalletId == wallet.Id && u.TxId == txId)
        .ToListAsync();

    // Inputs *spent by* txId: UTXOs we owned whose SpentByTxId = txId.
    var spent = await db.Utxos
        .Include(u => u.Address).ThenInclude(a => a.Account)
        .Where(u => u.Address.Account.WalletId == wallet.Id && u.SpentByTxId == txId)
        .ToListAsync();

    if (received.Count == 0 && spent.Count == 0)
        return Results.NotFound();

    // Pull UTXO labels in one shot so each output/input can show its notes.
    var utxoIds = received.Select(u => u.Id).Concat(spent.Select(u => u.Id))
        .Select(id => id.ToString()).ToHashSet();
    var labelsByUtxoId = (await db.Labels
            .Where(l => l.EntityType == "Utxo" && utxoIds.Contains(l.EntityId))
            .ToListAsync())
        .GroupBy(l => l.EntityId)
        .ToDictionary(g => g.Key, g => g.Select(l => l.Text).ToArray());

    var receivedSats = received.Sum(u => u.AmountSat);
    var spentSats = spent.Sum(u => u.AmountSat);
    // Wallet-POV delta. Positive = net inflow. Fee sits between wallet-visible
    // inputs and non-wallet outputs, so we can't compute it from this view alone.
    var netSats = receivedSats - spentSats;

    var coinjoin = await db.CoinJoinRecords
        .Where(c => c.TransactionId == txId)
        .Select(c => new { c.Id, c.RoundId, c.Status, c.OurInputCount, c.TotalInputCount, c.OurOutputCount, c.TotalOutputCount, c.ParticipantCount })
        .FirstOrDefaultAsync();

    var txNotes = await db.Labels
        .Where(l => l.EntityType == "Transaction" && l.EntityId == txId)
        .Select(l => new { l.Id, l.Text })
        .ToListAsync();

    return Results.Ok(new
    {
        txId,
        netSats,
        receivedSats,
        spentSats,
        confirmedHeight = received.FirstOrDefault()?.ConfirmedHeight ?? spent.FirstOrDefault()?.ConfirmedHeight ?? 0,
        coinjoin,
        notes = txNotes,
        receivedOutputs = received.Select(u => new
        {
            u.Id,
            vout = u.OutputIndex,
            u.AmountSat,
            u.ConfirmedHeight,
            u.IsFrozen,
            labels = labelsByUtxoId.GetValueOrDefault(u.Id.ToString(), Array.Empty<string>()),
            address = new Script(u.Address.ScriptPubKey).GetDestinationAddress(network)?.ToString()
        }),
        spentInputs = spent.Select(u => new
        {
            u.Id,
            sourceTxId = u.TxId,
            vout = u.OutputIndex,
            u.AmountSat,
            labels = labelsByUtxoId.GetValueOrDefault(u.Id.ToString(), Array.Empty<string>()),
            address = new Script(u.Address.ScriptPubKey).GetDestinationAddress(network)?.ToString()
        })
    });
}).WithTags("Dashboard");

// CSV export of the wallet's full transaction history. Intended for
// tax/accounting — one row per tx we participated in (either as output
// recipient, input spender, or both).
app.MapGet("/api/dashboard/transactions/export", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null)
        return Results.Text("TxId,Direction,ReceivedSats,SpentSats,NetSats,ReceivedBtc,SpentBtc,NetBtc,ConfirmedHeight,IsCoinJoin,Notes\n",
            "text/csv", System.Text.Encoding.UTF8);

    var utxos = await db.Utxos
        .Include(u => u.Address).ThenInclude(a => a.Account)
        .Where(u => u.Address.Account.WalletId == wallet.Id)
        .ToListAsync();

    var allTxIds = new HashSet<string>();
    foreach (var u in utxos)
    {
        allTxIds.Add(u.TxId);
        if (u.SpentByTxId is not null) allTxIds.Add(u.SpentByTxId);
    }

    var coinjoinTxIds = (await db.CoinJoinRecords
            .Where(c => c.TransactionId != null && allTxIds.Contains(c.TransactionId))
            .Select(c => c.TransactionId!)
            .ToListAsync())
        .ToHashSet();

    var allTxIdsAsStrings = allTxIds.ToList();
    var notesByTxId = (await db.Labels
            .Where(l => l.EntityType == "Transaction" && allTxIdsAsStrings.Contains(l.EntityId))
            .ToListAsync())
        .GroupBy(l => l.EntityId)
        .ToDictionary(g => g.Key, g => string.Join("; ", g.Select(l => l.Text)));

    var csv = new System.Text.StringBuilder();
    csv.AppendLine("TxId,Direction,ReceivedSats,SpentSats,NetSats,ReceivedBtc,SpentBtc,NetBtc,ConfirmedHeight,IsCoinJoin,Notes");

    // Sort txids by the best "time" signal we have — max confirmed height of any
    // touched UTXO. Unconfirmed (null height) sort last as 0.
    var rows = allTxIds
        .Select(txId =>
        {
            var received = utxos.Where(u => u.TxId == txId).Sum(u => u.AmountSat);
            var spent = utxos.Where(u => u.SpentByTxId == txId).Sum(u => u.AmountSat);
            var net = received - spent;
            var direction = (received, spent) switch
            {
                ( > 0, > 0) => "self",
                ( > 0, 0) => "received",
                (0, > 0) => "sent",
                _ => "unknown"
            };
            var height = utxos
                .Where(u => u.TxId == txId || u.SpentByTxId == txId)
                .Max(u => u.ConfirmedHeight) ?? 0;
            var isCoinJoin = coinjoinTxIds.Contains(txId);
            var notes = notesByTxId.GetValueOrDefault(txId, "").Replace("\"", "\"\"");
            return (txId, direction, received, spent, net, height, isCoinJoin, notes);
        })
        .OrderByDescending(r => r.height)
        .ThenBy(r => r.txId);

    foreach (var r in rows)
    {
        csv.AppendLine($"{r.txId},{r.direction},{r.received},{r.spent},{r.net}," +
                       $"{r.received / 100_000_000.0:F8},{r.spent / 100_000_000.0:F8},{r.net / 100_000_000.0:F8}," +
                       $"{r.height},{(r.isCoinJoin ? "true" : "false")},\"{r.notes}\"");
    }

    return Results.Text(csv.ToString(), "text/csv", System.Text.Encoding.UTF8);
}).WithTags("Dashboard");

// Attach a free-text note to a transaction. Notes are user-private — they never
// leave the local DB and are not broadcast to the network.
app.MapPost("/api/dashboard/transactions/{txId}/note", async (
    string txId, WalletDbContext db, HttpContext ctx, DashboardEventBus bus) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<TransactionNoteRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Text))
        return Results.BadRequest("Note text is required");

    // Ensure wallet actually knows about this tx — prevents scribbling notes
    // on arbitrary txids that have nothing to do with this wallet.
    var touchesWallet = await db.Utxos
        .AnyAsync(u => u.TxId == txId || u.SpentByTxId == txId);
    if (!touchesWallet) return Results.NotFound("Transaction not found in wallet");

    var entity = new LabelEntity
    {
        EntityType = "Transaction",
        EntityId = txId,
        Text = body.Text.Trim()
    };
    db.Labels.Add(entity);
    await db.SaveChangesAsync();
    bus.Publish("transactions");

    return Results.Ok(new { entity.Id, entity.Text });
}).WithTags("Dashboard");

app.MapDelete("/api/dashboard/transactions/{txId}/note/{noteId}", async (
    string txId, int noteId, WalletDbContext db, DashboardEventBus bus) =>
{
    var label = await db.Labels.FindAsync(noteId);
    if (label is null || label.EntityType != "Transaction" || label.EntityId != txId)
        return Results.NotFound();

    db.Labels.Remove(label);
    await db.SaveChangesAsync();
    bus.Publish("transactions");
    return Results.Ok(new { deleted = noteId });
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

    // Materialize to List<int> — EF Core's expression interpreter on .NET 10
    // chokes on Contains() against int[] (it resolves to a span-based overload).
    var ids = body.UtxoIds.ToList();
    var utxos = await db.Utxos
        .Where(u => ids.Contains(u.Id))
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
    var freshReceive = await db.Addresses
        .Include(a => a.Account)
        .CountAsync(a => a.Account.WalletId == wallet.Id && !a.IsUsed && !a.IsExposed && !a.IsChange);
    var freshChange = await db.Addresses
        .Include(a => a.Account)
        .CountAsync(a => a.Account.WalletId == wallet.Id && !a.IsUsed && !a.IsExposed && a.IsChange);
    var hasXPub = await db.Accounts.AnyAsync(a => a.WalletId == wallet.Id && a.AccountXPub != null);

    return Results.Ok(new
    {
        exists = true,
        id = wallet.Id,
        name = wallet.Name,
        network = wallet.Network,
        createdAt = wallet.CreatedAt,
        accountCount,
        addressCount,
        freshReceiveAddresses = freshReceive,
        freshChangeAddresses = freshChange,
        gapExtensionEnabled = hasXPub,
        isBackupVerified = wallet.IsBackupVerified
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

// QR code for receive address (BIP-21 URI).
// Optional: ?amountSat= or ?amount= (BTC) + ?label= + ?message= fill in the
// BIP-21 payment request so the payer's wallet pre-fills the send form.
app.MapGet("/api/wallet/receive-qr", async (
    WalletDbContext db,
    long? amountSat = null,
    decimal? amount = null,
    string? label = null,
    string? message = null) =>
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
    var bip21 = BuildBip21Uri(btcAddress!.ToString(), amountSat, amount, label, message);

    using var qrGenerator = new QRCodeGenerator();
    var qrData = qrGenerator.CreateQrCode(bip21, QRCodeGenerator.ECCLevel.M);
    var svgQr = new SvgQRCode(qrData);
    var svg = svgQr.GetGraphic(4, "#e6edf3", "#0d1117", false);

    return Results.Content(svg, "image/svg+xml");
}).WithTags("Wallet");

// Returns just the BIP-21 URI for a receive address — handy for copy-to-clipboard
// and for embedding in Lightning-style payment flows that want the raw string.
app.MapGet("/api/wallet/receive-uri", async (
    WalletDbContext db,
    long? amountSat = null,
    decimal? amount = null,
    string? label = null,
    string? message = null) =>
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
    var uri = BuildBip21Uri(btcAddress!.ToString(), amountSat, amount, label, message);

    return Results.Ok(new { address = btcAddress.ToString(), uri });
}).WithTags("Wallet");

static string BuildBip21Uri(string address, long? amountSat, decimal? amount, string? label, string? message)
{
    var parts = new List<string>();
    // amountSat wins if both are given — avoids ambiguity from mixing units.
    if (amountSat.HasValue && amountSat.Value > 0)
        parts.Add($"amount={amountSat.Value / 100_000_000m:0.########}");
    else if (amount.HasValue && amount.Value > 0)
        parts.Add($"amount={amount.Value:0.########}");
    if (!string.IsNullOrWhiteSpace(label))
        parts.Add($"label={Uri.EscapeDataString(label)}");
    if (!string.IsNullOrWhiteSpace(message))
        parts.Add($"message={Uri.EscapeDataString(message)}");

    var query = parts.Count > 0 ? "?" + string.Join("&", parts) : "";
    return $"bitcoin:{address}{query}";
}

// Export account-level extended public keys for watch-only / hardware-wallet pairing.
// No passphrase required — xpubs are not secret, but they expose all wallet addresses.
app.MapGet("/api/wallet/export-xpub", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var accounts = await db.Accounts
        .Where(a => a.WalletId == wallet.Id && a.AccountXPub != null)
        .OrderByDescending(a => a.Purpose)
        .ThenBy(a => a.AccountIndex)
        .Select(a => new
        {
            purpose = a.Purpose,
            scriptType = a.Purpose == 86 ? "P2TR" : a.Purpose == 84 ? "P2WPKH" : "Unknown",
            accountIndex = a.AccountIndex,
            derivationPath = $"m/{a.Purpose}'/{(wallet.Network == "RegTest" || wallet.Network == "TestNet" ? 1 : 0)}'/{a.AccountIndex}'",
            xpub = a.AccountXPub
        })
        .ToListAsync();

    return Results.Ok(new { walletName = wallet.Name, network = wallet.Network, accounts });
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

// Backup verification: generate challenge (3 random word positions)
app.MapPost("/api/wallet/backup-challenge", async (WalletDbContext db, HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<PassphraseRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Passphrase))
        return Results.BadRequest("Passphrase required");

    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");
    if (wallet.IsBackupVerified) return Results.Ok(new { verified = true, message = "Backup already verified" });

    try
    {
        var mnemonic = MnemonicEncryption.Decrypt(wallet.EncryptedMnemonic, wallet.MnemonicSalt, body.Passphrase);
        var words = mnemonic.Split(' ');
        // Pick 3 random positions (1-indexed for user display)
        var rng = new Random();
        var positions = Enumerable.Range(0, words.Length).OrderBy(_ => rng.Next()).Take(3).OrderBy(x => x).ToArray();
        return Results.Ok(new
        {
            verified = false,
            positions = positions.Select(p => p + 1).ToArray(), // 1-indexed
            wordCount = words.Length
        });
    }
    catch (System.Security.Cryptography.CryptographicException)
    {
        return Results.BadRequest("Wrong passphrase");
    }
}).WithTags("Wallet");

// Backup verification: verify user's answers
app.MapPost("/api/wallet/verify-backup", async (WalletDbContext db, HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<VerifyBackupRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Passphrase) || body.Answers is null || body.Answers.Length == 0)
        return Results.BadRequest("Passphrase and answers required");

    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");
    if (wallet.IsBackupVerified) return Results.Ok(new { verified = true, message = "Backup already verified" });

    try
    {
        var mnemonic = MnemonicEncryption.Decrypt(wallet.EncryptedMnemonic, wallet.MnemonicSalt, body.Passphrase);
        var words = mnemonic.Split(' ');

        foreach (var answer in body.Answers)
        {
            var idx = answer.Position - 1; // Convert from 1-indexed
            if (idx < 0 || idx >= words.Length)
                return Results.BadRequest($"Invalid position: {answer.Position}");
            if (!string.Equals(words[idx], answer.Word?.Trim(), StringComparison.OrdinalIgnoreCase))
                return Results.Ok(new { verified = false, message = $"Word at position {answer.Position} is incorrect. Please try again." });
        }

        wallet.IsBackupVerified = true;
        await db.SaveChangesAsync();
        return Results.Ok(new { verified = true, message = "Backup verified successfully! Your recovery phrase is confirmed." });
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

    // Drive scheduled-payment activation from the list path so UI-visible state
    // always reflects the current time, even when the CoinJoin manager is idle.
    try { await new WalletPaymentManager(db, wallet.Id, network).ActivateScheduledPaymentsAsync(); } catch { }

    // SQLite's EF Core provider can't ORDER BY DateTimeOffset — pull then sort in memory.
    var payments = (await db.PendingPayments
            .Where(p => p.WalletId == wallet.Id)
            .ToListAsync())
        .OrderByDescending(p => p.CreatedAt)
        .Take(50)
        .Select(p => new
        {
            p.Id, p.Direction, p.AmountSat,
            amountBtc = p.AmountSat / 100_000_000.0,
            p.Destination, p.Status, p.IsInteractive, p.IsUrgent,
            p.Label, p.CompletedTxId, p.ProofJson, p.RetryCount, p.MaxRetries,
            p.CreatedAt, p.CompletedAt, p.ExpiresAt, p.ScheduledAt
        })
        .ToList();

    return Results.Ok(payments);
}).WithTags("Payments");

// Payments search with filters and pagination.
// Separate from /api/payments to keep that endpoint's bare-array shape stable.
app.MapGet("/api/payments/search", async (
    WalletDbContext db,
    string? direction = null,
    string? status = null,
    string? search = null,
    int limit = 50,
    int skip = 0) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null)
        return Results.Ok(new { total = 0, skip = 0, limit = 0, items = Array.Empty<object>() });

    limit = Math.Clamp(limit, 1, 500);
    skip = Math.Max(skip, 0);

    var query = db.PendingPayments.Where(p => p.WalletId == wallet.Id);

    if (!string.IsNullOrWhiteSpace(direction))
        query = query.Where(p => p.Direction == direction);

    if (!string.IsNullOrWhiteSpace(status))
    {
        var statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        query = query.Where(p => statuses.Contains(p.Status));
    }

    if (!string.IsNullOrWhiteSpace(search))
    {
        var term = search.Trim();
        query = query.Where(p =>
            EF.Functions.Like(p.Destination, $"%{term}%") ||
            (p.Label != null && EF.Functions.Like(p.Label, $"%{term}%")));
    }

    var total = await query.CountAsync();

    // SQLite can't ORDER BY DateTimeOffset with OFFSET, so page in memory after filtering.
    var matches = await query.ToListAsync();
    var payments = matches
        .OrderByDescending(p => p.CreatedAt)
        .Skip(skip)
        .Take(limit)
        .Select(p => new
        {
            p.Id, p.Direction, p.AmountSat,
            amountBtc = p.AmountSat / 100_000_000.0,
            p.Destination, p.Status, p.IsInteractive, p.IsUrgent,
            p.Label, p.CompletedTxId, p.ProofJson, p.RetryCount, p.MaxRetries,
            p.CreatedAt, p.CompletedAt, p.ExpiresAt, p.ScheduledAt
        })
        .ToList();

    return Results.Ok(new { total, skip, limit, items = payments });
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
            body.Destination, body.AmountSat, body.Interactive, body.Urgent, body.Label, expiry, body.ScheduledAt);

        bus.Publish("payments");
        return Results.Ok(new
        {
            entity.Id, entity.Direction, entity.AmountSat,
            amountBtc = entity.AmountSat / 100_000_000.0,
            entity.Destination, entity.Status, entity.IsInteractive,
            kompaktorPubKey = entity.KompaktorKeyHex,
            entity.Label, entity.CreatedAt, entity.ScheduledAt
        });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).WithTags("Payments");

app.MapPost("/api/payments/batch-send", async (WalletDbContext db, HttpContext ctx, DashboardEventBus bus) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var items = await ctx.Request.ReadFromJsonAsync<BatchSendRequest[]>();
    if (items is null || items.Length == 0)
        return Results.BadRequest("At least one payment required");
    if (items.Length > 50)
        return Results.BadRequest("Maximum 50 payments per batch");

    // Validate all addresses up front before creating any
    foreach (var item in items)
    {
        if (string.IsNullOrWhiteSpace(item.Destination) || item.AmountSat <= 0)
            return Results.BadRequest($"Invalid entry: destination and amount required for all items");
        try { BitcoinAddress.Create(item.Destination, network); }
        catch { return Results.BadRequest($"Invalid Bitcoin address: {item.Destination}"); }
    }

    var manager = new WalletPaymentManager(db, wallet.Id, network);
    var created = new List<object>();
    try
    {
        foreach (var item in items)
        {
            var expiry = item.ExpiryMinutes.HasValue ? TimeSpan.FromMinutes(item.ExpiryMinutes.Value) : (TimeSpan?)null;
            var entity = await manager.CreateOutboundPaymentAsync(
                item.Destination, item.AmountSat, item.Interactive, item.Urgent, item.Label, expiry, item.ScheduledAt);
            created.Add(new
            {
                entity.Id, entity.Direction, entity.AmountSat,
                amountBtc = entity.AmountSat / 100_000_000.0,
                entity.Destination, entity.Status, entity.Label
            });
        }
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }

    bus.Publish("payments");
    return Results.Ok(new { count = created.Count, payments = created });
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
    if (cancelled)
    {
        bus.Publish("payments");
        var entity = await db.PendingPayments.FindAsync(paymentId);
        if (entity is not null)
        {
            var webhookSvc = new PaymentWebhookService(db, wallet.Id);
            _ = webhookSvc.DeliverAsync(entity, "Failed");
        }
    }
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
        entity.CreatedAt, entity.CompletedAt, entity.ExpiresAt, entity.ScheduledAt
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

    var payments = (await db.PendingPayments
            .Where(p => p.WalletId == wallet.Id)
            .ToListAsync())
        .OrderByDescending(p => p.CreatedAt)
        .ToList();

    var csv = new System.Text.StringBuilder();
    csv.AppendLine("Id,Direction,AmountSat,AmountBtc,Destination,Status,Interactive,Urgent,Retries,Label,TxId,CreatedAt,CompletedAt,ExpiresAt");
    foreach (var p in payments)
    {
        var label = (p.Label ?? "").Replace("\"", "\"\"");
        csv.AppendLine($"{p.Id},{p.Direction},{p.AmountSat},{p.AmountSat / 100_000_000.0:F8},{p.Destination},{p.Status},{p.IsInteractive},{p.IsUrgent},{p.RetryCount},\"{label}\",{p.CompletedTxId ?? ""},{p.CreatedAt:o},{p.CompletedAt?.ToString("o") ?? ""},{p.ExpiresAt?.ToString("o") ?? ""}");
    }

    return Results.Text(csv.ToString(), "text/csv", System.Text.Encoding.UTF8);
}).WithTags("Payments");

// Payment webhooks
app.MapGet("/api/webhooks", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(Array.Empty<object>());

    var webhooks = (await db.PaymentWebhooks
            .Where(w => w.WalletId == wallet.Id)
            .ToListAsync())
        .OrderByDescending(w => w.CreatedAt)
        .Select(w => new
        {
            id = w.Id,
            url = w.Url,
            isActive = w.IsActive,
            eventFilter = w.EventFilter,
            createdAt = w.CreatedAt
        })
        .ToList();

    return Results.Ok(webhooks);
}).WithTags("Webhooks");

app.MapPost("/api/webhooks", async (WalletDbContext db, HttpContext ctx) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var body = await ctx.Request.ReadFromJsonAsync<WebhookCreateRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Url))
        return Results.BadRequest("URL is required");

    if (!Uri.TryCreate(body.Url, UriKind.Absolute, out var uri) ||
        (uri.Scheme != "http" && uri.Scheme != "https"))
        return Results.BadRequest("Invalid URL — must be http or https");

    var secret = Convert.ToHexString(
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    var entity = new PaymentWebhookEntity
    {
        WalletId = wallet.Id,
        Url = body.Url,
        Secret = secret,
        EventFilter = body.EventFilter ?? "*"
    };

    db.PaymentWebhooks.Add(entity);
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        id = entity.Id,
        url = entity.Url,
        secret,
        eventFilter = entity.EventFilter,
        message = "Store the secret — it won't be shown again. Use it to verify HMAC-SHA256 signatures in X-Kompaktor-Signature header."
    });
}).WithTags("Webhooks");

app.MapDelete("/api/webhooks/{webhookId}", async (int webhookId, WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var webhook = await db.PaymentWebhooks.FindAsync(webhookId);
    if (webhook is null || webhook.WalletId != wallet.Id) return Results.NotFound();

    db.PaymentWebhooks.Remove(webhook);
    await db.SaveChangesAsync();
    return Results.Ok(new { deleted = true });
}).WithTags("Webhooks");

app.MapGet("/api/webhooks/{webhookId}/deliveries", async (int webhookId, WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(Array.Empty<object>());

    var webhook = await db.PaymentWebhooks.FindAsync(webhookId);
    if (webhook is null || webhook.WalletId != wallet.Id) return Results.NotFound();

    // Same SQLite DateTimeOffset-in-ORDER-BY trap as the payments endpoints —
    // materialize first, order in memory, then cap to 50.
    var deliveries = (await db.WebhookDeliveries
            .Where(d => d.WebhookId == webhookId)
            .ToListAsync())
        .OrderByDescending(d => d.Timestamp)
        .Take(50)
        .Select(d => new
        {
            id = d.Id,
            paymentId = d.PaymentId,
            eventType = d.EventType,
            httpStatusCode = d.HttpStatusCode,
            success = d.Success,
            errorMessage = d.ErrorMessage,
            timestamp = d.Timestamp
        })
        .ToList();

    return Results.Ok(deliveries);
}).WithTags("Webhooks");

// Address book CRUD
app.MapGet("/api/address-book", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(Array.Empty<object>());

    var entries = (await db.AddressBook
        .Where(a => a.WalletId == wallet.Id)
        .ToListAsync())
        .OrderByDescending(a => a.CreatedAt)
        .Select(a => new { a.Id, a.Label, a.Address, a.CreatedAt })
        .ToList();

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
        var txIdStr = txId.ToString();

        // Record the raw transaction so /api/dashboard/fee-bump and detail
        // endpoints can find it by txid later.
        if (!await db.Transactions.AnyAsync(t => t.Id == txIdStr))
        {
            db.Transactions.Add(new TransactionEntity
            {
                Id = txIdStr,
                RawHex = tx.ToHex(),
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        // Mark UTXOs as spent
        foreach (var coin in plan.InputCoins)
        {
            var utxo = await db.Utxos.FirstOrDefaultAsync(u =>
                u.TxId == coin.Outpoint.Hash.ToString() && u.OutputIndex == (int)coin.Outpoint.N);
            if (utxo is not null) utxo.SpentByTxId = txIdStr;
        }
        await db.SaveChangesAsync();

        bus.Publish("utxos");
        bus.Publish("wallet");

        return Results.Ok(new
        {
            txId = txIdStr,
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

// Quick client-side address validation against the coordinator's network.
// Used by the send form to give immediate feedback as the user pastes.
// Accepts a raw address OR a BIP21 `bitcoin:` URI — when a URI is supplied,
// the parsed amount/label/message are returned alongside validation.
app.MapGet("/api/dashboard/validate-address", (string address) =>
{
    if (string.IsNullOrWhiteSpace(address))
        return Results.Ok(new { valid = false, reason = "empty" });

    var input = address.Trim();
    string? parsedAmountBtc = null;
    long? parsedAmountSat = null;
    string? parsedLabel = null;
    string? parsedMessage = null;
    string addressOnly = input;

    if (input.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase))
    {
        var afterScheme = input.Substring("bitcoin:".Length);
        var qIdx = afterScheme.IndexOf('?');
        addressOnly = qIdx >= 0 ? afterScheme.Substring(0, qIdx) : afterScheme;
        if (qIdx >= 0)
        {
            var query = afterScheme.Substring(qIdx + 1);
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var key = Uri.UnescapeDataString(pair.Substring(0, eq)).ToLowerInvariant();
                var val = Uri.UnescapeDataString(pair.Substring(eq + 1));
                switch (key)
                {
                    case "amount":
                        parsedAmountBtc = val;
                        if (decimal.TryParse(val, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var btc) && btc > 0)
                        {
                            parsedAmountSat = (long)Math.Round(btc * 100_000_000m);
                        }
                        break;
                    case "label":
                        parsedLabel = val;
                        break;
                    case "message":
                        parsedMessage = val;
                        break;
                }
            }
        }
        addressOnly = addressOnly.Trim();
        if (string.IsNullOrEmpty(addressOnly))
            return Results.Ok(new { valid = false, reason = "empty" });
    }

    try
    {
        var addr = BitcoinAddress.Create(addressOnly, network);
        var script = addr.ScriptPubKey;
        string type = script switch
        {
            var s when s.IsScriptType(ScriptType.Taproot) => "P2TR",
            var s when s.IsScriptType(ScriptType.Witness) => "P2WPKH",
            var s when s.IsScriptType(ScriptType.P2SH) => "P2SH",
            _ => "Other"
        };
        return Results.Ok(new
        {
            valid = true,
            type,
            network = network.Name,
            address = addressOnly,
            amountBtc = parsedAmountBtc,
            amountSat = parsedAmountSat,
            label = parsedLabel,
            message = parsedMessage
        });
    }
    catch (FormatException)
    {
        // Might be valid for a different network — try parsing leniently
        foreach (var net in new[] { Network.Main, Network.TestNet, Network.RegTest })
        {
            if (net == network) continue;
            try
            {
                _ = BitcoinAddress.Create(addressOnly, net);
                return Results.Ok(new
                {
                    valid = false,
                    reason = "wrong_network",
                    detectedNetwork = net.Name,
                    expectedNetwork = network.Name
                });
            }
            catch { /* keep trying */ }
        }
        return Results.Ok(new { valid = false, reason = "invalid" });
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

// Curated fee presets for send-form UX: fast / medium / slow / economy.
// Returns named tiers with sat/vB rates so clients don't have to map
// confirmation targets to human-readable labels themselves.
app.MapGet("/api/dashboard/fee-presets", async (IBlockchainBackend chain) =>
{
    async Task<long> EstimateAsync(int target, long fallback)
    {
        try
        {
            var feeRate = await chain.EstimateFeeAsync(target);
            return Math.Max(1, (long)Math.Ceiling(feeRate.SatoshiPerByte));
        }
        catch
        {
            return fallback;
        }
    }

    var fast = await EstimateAsync(1, 10);
    var medium = await EstimateAsync(3, 5);
    var slow = await EstimateAsync(6, 2);
    var economy = await EstimateAsync(25, 1);

    // Guarantee monotonic ordering — a backend returning noisy/inconsistent
    // estimates shouldn't show the user "medium is slower than slow".
    medium = Math.Min(medium, fast);
    slow = Math.Min(slow, medium);
    economy = Math.Min(economy, slow);

    return Results.Ok(new
    {
        presets = new[]
        {
            new { key = "fast", label = "Fast", confirmationTarget = 1, satPerVb = fast, description = "Next block (~10 min)" },
            new { key = "medium", label = "Medium", confirmationTarget = 3, satPerVb = medium, description = "~30 minutes" },
            new { key = "slow", label = "Slow", confirmationTarget = 6, satPerVb = slow, description = "~1 hour" },
            new { key = "economy", label = "Economy", confirmationTarget = 25, satPerVb = economy, description = "Hours to days" }
        }
    });
}).WithTags("Dashboard");

// Current BTC price in fiat currencies (cached 60s via Coingecko)
app.MapGet("/api/dashboard/price", async (IPriceService prices, CancellationToken ct) =>
{
    var snapshot = await prices.GetAsync(ct);
    if (snapshot.Rates.Count == 0)
        return Results.Ok(new { available = false });

    return Results.Ok(new
    {
        available = true,
        fetchedAt = snapshot.FetchedAt,
        rates = snapshot.Rates
    });
}).WithTags("Dashboard");

// RBF fee bump: create a replacement transaction with higher fee
app.MapPost("/api/dashboard/fee-bump", async (WalletDbContext db, HttpContext ctx) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var body = await ctx.Request.ReadFromJsonAsync<FeeBumpRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.TxId) || body.NewFeeRateSatPerVb <= 0)
        return Results.BadRequest("Transaction ID and new fee rate required");

    var txEntity = await db.Transactions.FindAsync(body.TxId);
    if (txEntity is null)
        return Results.BadRequest("Transaction not found in wallet history");
    if (txEntity.BlockHeight.HasValue)
        return Results.BadRequest("Transaction already confirmed — cannot RBF");

    try
    {
        var originalTx = Transaction.Parse(txEntity.RawHex, network);

        // Verify at least one input signals RBF (nSequence < 0xFFFFFFFE)
        if (!originalTx.Inputs.Any(i => i.Sequence < 0xFFFFFFFE))
            return Results.BadRequest("Original transaction does not signal RBF (BIP 125) — cannot replace");

        // Find wallet-owned inputs by matching UTXOs
        var walletAddresses = await db.Addresses
            .Include(a => a.Account)
            .Where(a => a.Account.WalletId == wallet.Id)
            .Select(a => a.ScriptPubKey)
            .ToListAsync();
        var walletScripts = walletAddresses.Select(s => new Script(s)).ToHashSet();

        // Reconstruct coins from original inputs by looking up UTXOs
        var coins = new List<Coin>();
        foreach (var input in originalTx.Inputs)
        {
            var utxo = await db.Utxos.FirstOrDefaultAsync(u =>
                u.TxId == input.PrevOut.Hash.ToString() &&
                u.OutputIndex == (int)input.PrevOut.N);
            if (utxo is not null)
            {
                coins.Add(new Coin(input.PrevOut,
                    new TxOut(Money.Satoshis(utxo.AmountSat), new Script(utxo.ScriptPubKey))));
            }
        }

        if (coins.Count == 0)
            return Results.BadRequest("Could not find wallet UTXOs for any inputs");

        // Find the original destination (non-change output)
        var changeScript = originalTx.Outputs
            .Where(o => walletScripts.Contains(o.ScriptPubKey))
            .Select(o => o.ScriptPubKey)
            .FirstOrDefault();
        var destOutput = originalTx.Outputs
            .FirstOrDefault(o => changeScript is null || o.ScriptPubKey != changeScript)
            ?? originalTx.Outputs.First();

        // Build replacement with higher fee
        var newFeeRate = new FeeRate(Money.Satoshis(body.NewFeeRateSatPerVb), 1);
        var builder = network.CreateTransactionBuilder();
        builder.AddCoins(coins);
        builder.Send(destOutput.ScriptPubKey, destOutput.Value);
        if (changeScript is not null)
            builder.SetChange(changeScript);
        builder.SendEstimatedFees(newFeeRate);
        builder.OptInRBF = true;

        var bumpedTx = builder.BuildTransaction(sign: false);
        var estimatedFee = builder.EstimateFees(bumpedTx, newFeeRate);

        // Create PSBT for hardware wallet signing
        var psbt = PSBT.FromTransaction(bumpedTx, network);
        for (int i = 0; i < bumpedTx.Inputs.Count; i++)
        {
            var coin = coins.FirstOrDefault(c => c.Outpoint == bumpedTx.Inputs[i].PrevOut);
            if (coin is not null)
                psbt.Inputs[i].WitnessUtxo = coin.TxOut;
        }

        return Results.Ok(new
        {
            originalTxId = body.TxId,
            bumpedPsbt = psbt.ToBase64(),
            estimatedFeeSat = estimatedFee.Satoshi,
            newFeeRateSatPerVb = body.NewFeeRateSatPerVb,
            inputCount = bumpedTx.Inputs.Count,
            outputCount = bumpedTx.Outputs.Count,
            message = "Sign this PSBT and broadcast via /api/dashboard/broadcast-psbt to replace the original transaction"
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Fee bump failed: {ex.Message}");
    }
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
record TransactionNoteRequest(string Text);
record SendRequest(string Destination, long AmountSat, long FeeRateSatPerVb = 2, string Strategy = "PrivacyFirst", string Passphrase = "");
record BroadcastPsbtRequest(string SignedPsbt);
record CreatePaymentRequest(string Destination, long AmountSat, bool Interactive = true, bool Urgent = false, string? Label = null, int? ExpiryMinutes = null, DateTimeOffset? ScheduledAt = null);
record BatchSendRequest(string Destination, long AmountSat, bool Interactive = true, bool Urgent = false, string? Label = null, int? ExpiryMinutes = null, DateTimeOffset? ScheduledAt = null);
record CreateReceiveRequest(long AmountSat, string? Label = null, int? ExpiryMinutes = null);
record WebhookCreateRequest(string Url, string? EventFilter = null);
record VerifyBackupRequest(string Passphrase, VerifyBackupAnswer[] Answers);
record VerifyBackupAnswer(int Position, string Word);
record FeeBumpRequest(string TxId, long NewFeeRateSatPerVb);
record SweepRequest(string Destination, long FeeRateSatPerVb = 2);

namespace Kompaktor.Web
{
    /// <summary>
    /// Marker type used by <c>WebApplicationFactory&lt;WebEntryPoint&gt;</c> in E2E tests.
    /// Since the Program class generated from top-level statements lives in the
    /// global namespace and collides with Kompaktor.Server's Program, we expose
    /// this dedicated anchor type for test hosts to locate the Kompaktor.Web assembly.
    /// </summary>
    public sealed class WebEntryPoint { }
}
