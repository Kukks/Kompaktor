using System.Security.Cryptography;
using System.Text.Json;
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
app.MapGet("/api/dashboard/summary", async (
    WalletDbContext db, IPriceService prices, string? fiat, CancellationToken ct) =>
{
    var wallets = await db.Wallets.CountAsync(ct);
    var unspent = db.Utxos.Where(u => u.SpentByTxId == null);
    var utxos = await unspent.CountAsync(ct);
    var totalSats = await unspent.SumAsync(u => u.AmountSat, ct);
    var confirmedSats = await unspent.Where(u => u.ConfirmedHeight != null).SumAsync(u => u.AmountSat, ct);
    var unconfirmedSats = await unspent.Where(u => u.ConfirmedHeight == null).SumAsync(u => u.AmountSat, ct);
    var coinjoins = await db.CoinJoinRecords.CountAsync(ct);

    // Privacy breakdown: these three buckets are not mutually exclusive —
    // a single UTXO can be both coinjoin-produced (mixed) AND sitting on an
    // exposed address if a later round burned the script. Report the raw
    // counts + balances; the UI treats exposed as a separate axis.
    var mixedQuery = unspent.Where(u => u.IsCoinJoinOutput);
    var mixedSats = await mixedQuery.SumAsync(u => u.AmountSat, ct);
    var mixedCount = await mixedQuery.CountAsync(ct);
    var exposedQuery = unspent.Where(u => u.Address.IsExposed);
    var exposedSats = await exposedQuery.SumAsync(u => u.AmountSat, ct);
    var exposedCount = await exposedQuery.CountAsync(ct);

    decimal? fiatRate = null;
    string? fiatCurrency = null;
    if (!string.IsNullOrWhiteSpace(fiat))
    {
        try
        {
            var snapshot = await prices.GetAsync(ct);
            var key = fiat.ToLowerInvariant();
            if (snapshot.Rates.TryGetValue(key, out var rate))
            {
                fiatRate = rate;
                fiatCurrency = key;
            }
        }
        catch { /* price service best-effort */ }
    }

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
        completedCoinjoins = coinjoins,
        mixedBalanceSats = mixedSats,
        mixedBalanceBtc = mixedSats / 100_000_000.0,
        mixedUtxoCount = mixedCount,
        unmixedBalanceSats = totalSats - mixedSats,
        unmixedBalanceBtc = (totalSats - mixedSats) / 100_000_000.0,
        exposedBalanceSats = exposedSats,
        exposedBalanceBtc = exposedSats / 100_000_000.0,
        exposedUtxoCount = exposedCount,
        fiatCurrency,
        fiatRate,
        totalBalanceFiat = fiatRate is null ? (decimal?)null
            : Math.Round((decimal)(totalSats / 100_000_000.0) * fiatRate.Value, 2),
        confirmedBalanceFiat = fiatRate is null ? (decimal?)null
            : Math.Round((decimal)(confirmedSats / 100_000_000.0) * fiatRate.Value, 2)
    });
}).WithTags("Dashboard");

// Live Tor health for the dashboard: resolves the wallet's persisted Tor
// settings and actively probes them so users can see at-a-glance whether
// mixing will actually route through Tor. Probe is skipped when Tor is
// disabled to keep the endpoint cheap on clearnet wallets.
app.MapGet("/api/dashboard/tor-status", async (WalletDbContext db, HttpContext ctx) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(new { enabled = false, configured = false });

    var host = string.IsNullOrWhiteSpace(wallet.TorSocksHost) ? "127.0.0.1" : wallet.TorSocksHost;
    var port = wallet.TorSocksPort ?? 9050;

    if (!wallet.TorEnabled)
    {
        return Results.Ok(new
        {
            enabled = false,
            configured = !string.IsNullOrWhiteSpace(wallet.TorSocksHost),
            host,
            port
        });
    }

    var probe = await TorReachability.ProbeAsync(host, port, ctx.RequestAborted);
    return Results.Ok(new
    {
        enabled = true,
        configured = true,
        host,
        port,
        reachable = probe.Reachable,
        latencyMs = probe.Reachable ? probe.LatencyMs : (long?)null,
        authMethod = probe.Reachable ? probe.AuthMethod : null,
        error = probe.Reachable ? null : probe.Error
    });
}).WithTags("Dashboard");

app.MapGet("/api/dashboard/utxos", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(Array.Empty<object>());

    var selector = new WalletCoinSelector(db);
    var scored = await selector.GetScoredUtxosAsync(wallet.Id, includeFrozen: true);

    // Annotate each UTXO with its privacy cluster. Clusters are union-find
    // groups of UTXOs that share a label — co-spending across clusters tells
    // an observer both are one wallet's. Only LABELLED clusters are surfaced:
    // a singleton unlabelled UTXO is not a meaningful compartment. The cluster
    // id is the minimum UtxoId in the group so it's stable across requests
    // (clients can group/color by this id without server state).
    var utxoEntities = scored.Select(s => s.Utxo).ToList();
    var utxoClusterInfo = new Dictionary<int, (int ClusterId, int Size, string[] Labels, bool HasExternalSource)>();
    if (utxoEntities.Count > 0)
    {
        var labels = await db.Labels
            .Where(l => l.EntityType == "Utxo" || l.EntityType == "Address")
            .ToListAsync();
        var coinjoins = await db.CoinJoinRecords.ToListAsync();
        var clusters = new Kompaktor.Scoring.LabelClusterAnalyzer()
            .AnalyzeClusters(utxoEntities, labels, coinjoins);

        foreach (var cluster in clusters.Where(c => c.Labels.Count > 0))
        {
            var clusterId = cluster.UtxoIds.Min();
            var info = (clusterId, cluster.UtxoIds.Count, cluster.Labels.ToArray(), cluster.HasExternalSource);
            foreach (var utxoId in cluster.UtxoIds)
                utxoClusterInfo[utxoId] = info;
        }
    }

    var result = scored
        .OrderByDescending(s => s.Utxo.AmountSat)
        .Take(100)
        .Select(s =>
        {
            object? cluster = utxoClusterInfo.TryGetValue(s.Utxo.Id, out var c)
                ? new { id = c.ClusterId, size = c.Size, labels = c.Labels, hasExternalSource = c.HasExternalSource }
                : null;
            return new
            {
                id = s.Utxo.Id,
                txId = s.Utxo.TxId,
                outputIndex = s.Utxo.OutputIndex,
                amountSat = s.Utxo.AmountSat,
                amountBtc = s.Utxo.AmountSat / 100_000_000.0,
                confirmedHeight = s.Utxo.ConfirmedHeight,
                isFrozen = s.Utxo.IsFrozen,
                // An address is "exposed" if its script was revealed to other
                // participants in a previous coinjoin round (even a failed one).
                // Surface this so coin-control callers can steer away from
                // intersection-attack risk; today only scoring's reuse penalty
                // reacts to it, and only after the address has been used twice.
                isAddressExposed = s.Utxo.Address.IsExposed,
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
                labels = s.Labels,
                cluster
            };
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
        .Select(r =>
        {
            // Anonymity-set summary: group outputs by value, count the
            // largest cluster. maxAnonSet is a useful upper bound for
            // "how well could any participant's output hide in this round".
            // Exact tolerance isn't applied here — on-chain values are
            // already rounded to the coordinator's bucketing.
            int maxAnonSet = 0;
            int distinctOutputValues = 0;
            if (r.OutputValuesSat.Length > 0)
            {
                var grouped = r.OutputValuesSat
                    .GroupBy(v => v)
                    .Select(g => g.Count())
                    .ToArray();
                maxAnonSet = grouped.Max();
                distinctOutputValues = grouped.Length;
            }

            return new
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
                maxAnonSet,
                distinctOutputValues,
                outputValuesSat = r.OutputValuesSat,
                createdAt = r.CreatedAt
            };
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

    List<(string TxId, int OutputIndex)>? specific;
    try { specific = ParseOutpoints(body.SelectedOutpoints); }
    catch (FormatException ex) { return Results.BadRequest(ex.Message); }

    try
    {
        var destination = BitcoinAddress.Create(body.Destination, network);
        var amount = Money.Satoshis(body.AmountSat);
        var feeRate = new FeeRate(Money.Satoshis(body.FeeRateSatPerVb), 1);
        var strategy = Enum.TryParse<CoinSelectionStrategy>(body.Strategy, true, out var s)
            ? s : CoinSelectionStrategy.PrivacyFirst;

        var txBuilder = new WalletTransactionBuilder(db, network);
        var plan = await txBuilder.PlanTransactionAsync(
            wallet.Id, destination.ScriptPubKey, amount, feeRate, strategy, specific);

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

app.MapGet("/api/dashboard/transactions", async (
    WalletDbContext db, int? skip, int? take, bool? confirmed) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(Array.Empty<object>());

    var pageSize = Math.Clamp(take ?? 100, 1, 500);
    var pageSkip = Math.Max(skip ?? 0, 0);

    var baseQuery = db.Utxos
        .Where(u => u.Address.Account.WalletId == wallet.Id);

    if (confirmed == true)
        baseQuery = baseQuery.Where(u => u.ConfirmedHeight != null);
    else if (confirmed == false)
        baseQuery = baseQuery.Where(u => u.ConfirmedHeight == null);

    // Paginate at the transaction level, not the UTXO level, so a tx with
    // many outputs doesn't crowd out other txs from the page.
    var txGroups = await baseQuery
        .GroupBy(u => u.TxId)
        .Select(g => new
        {
            TxId = g.Key,
            AmountSat = g.Sum(u => u.AmountSat),
            MaxHeight = g.Max(u => u.ConfirmedHeight),
            AnySpent = g.Any(u => u.SpentByTxId != null),
            UtxoCount = g.Count()
        })
        .OrderByDescending(x => x.MaxHeight)
        .Skip(pageSkip)
        .Take(pageSize)
        .ToListAsync();

    // Bulk-fetch notes for the visible page in one round-trip so the list can
    // render them inline — avoids N+1 and a per-row drill-down for the user.
    var pageTxIds = txGroups.Select(g => g.TxId).ToList();
    var notesByTxId = (await db.Labels
            .Where(l => l.EntityType == "Transaction" && pageTxIds.Contains(l.EntityId))
            .Select(l => new { l.Id, l.EntityId, l.Text })
            .ToListAsync())
        .GroupBy(l => l.EntityId)
        .ToDictionary(g => g.Key, g => g.Select(l => new { id = l.Id, text = l.Text }).ToArray());

    return Results.Ok(txGroups.Select(g => new
    {
        txId = g.TxId,
        amountSat = g.AmountSat,
        amountBtc = g.AmountSat / 100_000_000.0,
        confirmedHeight = g.MaxHeight,
        isSpent = g.AnySpent,
        utxoCount = g.UtxoCount,
        type = g.AnySpent ? "spent" : "received",
        notes = notesByTxId.TryGetValue(g.TxId, out var n) ? n : []
    }));
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

// Label an owned receive/change address. Counterparty (non-owned) labels go
// through /api/address-book. This endpoint is specifically for labelling the
// wallet's OWN addresses so downstream code (AnonymityScorer, coin selection,
// cluster analysis) can surface meaningful names like "Exchange deposit" or
// "Donations" alongside UTXOs. We look up by bitcoin-address string — stable
// external identifier — then store under the AddressEntity's int PK.
app.MapPost("/api/wallet/addresses/{address}/label", async (
    string address, WalletDbContext db, HttpContext ctx, DashboardEventBus bus) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<AddressLabelRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Text))
        return Results.BadRequest("Label text is required");

    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    byte[] scriptBytes;
    try
    {
        scriptBytes = BitcoinAddress.Create(address, network).ScriptPubKey.ToBytes();
    }
    catch
    {
        return Results.BadRequest("Invalid bitcoin address");
    }

    var addrEntity = await db.Addresses
        .Include(a => a.Account)
        .Where(a => a.Account.WalletId == wallet.Id)
        .FirstOrDefaultAsync(a => a.ScriptPubKey == scriptBytes);
    if (addrEntity is null)
        return Results.NotFound("Address is not owned by this wallet");

    var entity = new LabelEntity
    {
        EntityType = "Address",
        EntityId = addrEntity.Id.ToString(),
        Text = body.Text.Trim()
    };
    db.Labels.Add(entity);
    await db.SaveChangesAsync();
    bus.Publish("addresses");

    return Results.Ok(new { entity.Id, entity.Text, address });
}).WithTags("Wallet");

// List all labels attached to a specific owned address. Returns an empty
// array if the address is owned but unlabelled (so callers can distinguish
// "no labels" from "not found / not ours").
app.MapGet("/api/wallet/addresses/{address}/labels", async (
    string address, WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    byte[] scriptBytes;
    try
    {
        scriptBytes = BitcoinAddress.Create(address, network).ScriptPubKey.ToBytes();
    }
    catch
    {
        return Results.BadRequest("Invalid bitcoin address");
    }

    var addrEntity = await db.Addresses
        .Include(a => a.Account)
        .Where(a => a.Account.WalletId == wallet.Id)
        .FirstOrDefaultAsync(a => a.ScriptPubKey == scriptBytes);
    if (addrEntity is null)
        return Results.NotFound("Address is not owned by this wallet");

    var addrId = addrEntity.Id.ToString();
    var labels = await db.Labels
        .Where(l => l.EntityType == "Address" && l.EntityId == addrId)
        .OrderBy(l => l.Id)
        .Select(l => new { l.Id, l.Text, l.CreatedAt })
        .ToListAsync();

    return Results.Ok(new { address, labels });
}).WithTags("Wallet");

// Remove a label from an owned address. The labelId is validated to actually
// belong to that address so a stale labelId from a different entity can't
// accidentally delete the wrong row.
app.MapDelete("/api/wallet/addresses/{address}/label/{labelId}", async (
    string address, int labelId, WalletDbContext db, DashboardEventBus bus) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    byte[] scriptBytes;
    try
    {
        scriptBytes = BitcoinAddress.Create(address, network).ScriptPubKey.ToBytes();
    }
    catch
    {
        return Results.BadRequest("Invalid bitcoin address");
    }

    var addrEntity = await db.Addresses
        .Include(a => a.Account)
        .Where(a => a.Account.WalletId == wallet.Id)
        .FirstOrDefaultAsync(a => a.ScriptPubKey == scriptBytes);
    if (addrEntity is null)
        return Results.NotFound("Address is not owned by this wallet");

    var label = await db.Labels.FindAsync(labelId);
    if (label is null
        || label.EntityType != "Address"
        || label.EntityId != addrEntity.Id.ToString())
        return Results.NotFound("Label not found on this address");

    db.Labels.Remove(label);
    await db.SaveChangesAsync();
    bus.Publish("addresses");
    return Results.Ok(new { deleted = labelId });
}).WithTags("Wallet");

// List owned addresses with metadata for the address-management UI.
// Filters: type (p2tr/p2wpkh), change (true/false), used (true/false),
// exposed (true/false), hasLabel (true/false). Results are paginated via
// limit/offset; default limit 200, cap 1000 so we never flush the whole
// address table into a single response on a wallet with a huge gap limit.
// Returns computed fields (utxo counts, total received) so the UI doesn't
// have to re-aggregate them client-side.
app.MapGet("/api/wallet/addresses", async (
    WalletDbContext db,
    string? type = null,
    bool? change = null,
    bool? used = null,
    bool? exposed = null,
    bool? hasLabel = null,
    int offset = 0,
    int limit = 200) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(new { total = 0, items = Array.Empty<object>() });

    int? purposeFilter;
    try { purposeFilter = ParseAddressType(type); }
    catch (FormatException ex) { return Results.BadRequest(ex.Message); }

    if (limit is <= 0 or > 1000) limit = 200;
    if (offset < 0) offset = 0;

    var q = db.Addresses
        .Include(a => a.Account)
        .Where(a => a.Account.WalletId == wallet.Id);
    if (purposeFilter.HasValue) q = q.Where(a => a.Account.Purpose == purposeFilter.Value);
    if (change.HasValue) q = q.Where(a => a.IsChange == change.Value);
    if (used.HasValue) q = q.Where(a => a.IsUsed == used.Value);
    if (exposed.HasValue) q = q.Where(a => a.IsExposed == exposed.Value);

    var total = await q.CountAsync();
    var page = await q
        .OrderBy(a => a.Account.Purpose == 86 ? 0 : 1) // Taproot first
        .ThenBy(a => a.IsChange)                       // external then change
        .ThenBy(a => a.Id)
        .Skip(offset)
        .Take(limit)
        .ToListAsync();

    if (page.Count == 0)
        return Results.Ok(new { total, items = Array.Empty<object>() });

    // Aggregate UTXOs and labels in one DB hit each rather than per-row.
    var ids = page.Select(a => a.Id).ToList();
    var idStrings = ids.Select(i => i.ToString()).ToList();
    var utxoStats = await db.Utxos
        .Where(u => ids.Contains(u.AddressId))
        .GroupBy(u => u.AddressId)
        .Select(g => new
        {
            AddressId = g.Key,
            UtxoCount = g.Count(),
            UnspentCount = g.Count(u => u.SpentByTxId == null),
            TotalReceivedSat = g.Sum(u => u.AmountSat),
            UnspentSat = g.Where(u => u.SpentByTxId == null).Sum(u => u.AmountSat)
        })
        .ToDictionaryAsync(x => x.AddressId);
    var labelRows = await db.Labels
        .Where(l => l.EntityType == "Address" && idStrings.Contains(l.EntityId))
        .Select(l => new { l.Id, l.EntityId, l.Text })
        .ToListAsync();
    var labelsByAddrId = labelRows
        .GroupBy(l => int.Parse(l.EntityId))
        .ToDictionary(g => g.Key, g => g.Select(l => new { id = l.Id, text = l.Text }).ToArray());
    // Typed empty fallback so the anonymous-type shape matches the populated
    // branch — otherwise the conditional expression can't infer a common type.
    var emptyLabels = new[] { new { id = 0, text = "" } }.Take(0).ToArray();

    if (hasLabel.HasValue)
    {
        page = hasLabel.Value
            ? page.Where(a => labelsByAddrId.ContainsKey(a.Id)).ToList()
            : page.Where(a => !labelsByAddrId.ContainsKey(a.Id)).ToList();
    }

    var items = page.Select(a =>
    {
        string? addrStr = null;
        try { addrStr = new Script(a.ScriptPubKey).GetDestinationAddress(network)?.ToString(); }
        catch { }
        utxoStats.TryGetValue(a.Id, out var stats);
        return new
        {
            id = a.Id,
            address = addrStr,
            keyPath = a.KeyPath,
            purpose = a.Account.Purpose,
            type = a.Account.Purpose == 86 ? "P2TR" : "P2WPKH",
            isChange = a.IsChange,
            isUsed = a.IsUsed,
            isExposed = a.IsExposed,
            routingGroup = a.RoutingGroup,
            utxoCount = stats?.UtxoCount ?? 0,
            unspentCount = stats?.UnspentCount ?? 0,
            totalReceivedSat = stats?.TotalReceivedSat ?? 0,
            unspentSat = stats?.UnspentSat ?? 0,
            labels = labelsByAddrId.TryGetValue(a.Id, out var lbls) ? lbls : emptyLabels
        };
    }).ToList();

    return Results.Ok(new { total, offset, limit, items });
}).WithTags("Wallet");

// Label clusters: groups of UTXOs linked by shared labels (on the UTXOs
// themselves OR on the addresses that received them). A cluster is a
// privacy "compartment" — co-spending across clusters reveals to an
// observer that both belong to one wallet. Surface this so the UI can show
// the user their privacy compartments and warn before a co-spend that
// would merge them. Clusters containing an "Exchange:" / "KYC:" / "P2P:"
// prefix label are flagged as having an external source (deanonymizable).
app.MapGet("/api/wallet/label-clusters", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null)
        return Results.Ok(new { totalClusters = 0, clusters = Array.Empty<object>() });

    var utxos = await db.Utxos
        .Include(u => u.Address)
        .ThenInclude(a => a.Account)
        .Where(u => u.Address.Account.WalletId == wallet.Id)
        .Where(u => u.SpentByTxId == null)
        .ToListAsync();

    if (utxos.Count == 0)
        return Results.Ok(new { totalClusters = 0, clusters = Array.Empty<object>() });

    var labels = await db.Labels
        .Where(l => l.EntityType == "Utxo" || l.EntityType == "Address")
        .ToListAsync();
    var coinjoins = await db.CoinJoinRecords.ToListAsync();

    var analyzer = new Kompaktor.Scoring.LabelClusterAnalyzer();
    var clusters = analyzer.AnalyzeClusters(utxos, labels, coinjoins);

    // Only surface clusters that actually have labels — singletons with no
    // labels aren't useful privacy compartments to show the user.
    var interesting = clusters
        .Where(c => c.Labels.Count > 0)
        .OrderByDescending(c => c.UtxoIds.Count)
        .Select(c =>
        {
            var clusterUtxos = utxos.Where(u => c.UtxoIds.Contains(u.Id)).ToList();
            return new
            {
                utxoIds = c.UtxoIds.ToArray(),
                utxoCount = c.UtxoIds.Count,
                totalSat = clusterUtxos.Sum(u => u.AmountSat),
                labels = c.Labels.ToArray(),
                hasExternalSource = c.HasExternalSource
            };
        })
        .ToList();

    return Results.Ok(new { totalClusters = interesting.Count, clusters = interesting });
}).WithTags("Wallet");

// Manually flip an owned address's exposure flag. Exposure is normally set
// automatically when the address is revealed during a coinjoin round, but
// users sometimes have out-of-band knowledge — "I posted this on Twitter",
// "I gave this to a KYC exchange" — and want to force privacy-aware coin
// selection to treat the address as tainted. Symmetric undo is allowed so a
// mis-click doesn't permanently blacklist an address.
app.MapPatch("/api/wallet/addresses/{address}/exposure", async (
    string address, WalletDbContext db, HttpContext ctx, DashboardEventBus bus) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ExposureRequest>();
    if (body is null)
        return Results.BadRequest("Body required with { exposed: true|false }");

    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    byte[] scriptBytes;
    try { scriptBytes = BitcoinAddress.Create(address, network).ScriptPubKey.ToBytes(); }
    catch { return Results.BadRequest("Invalid bitcoin address"); }

    var addrEntity = await db.Addresses
        .Include(a => a.Account)
        .Where(a => a.Account.WalletId == wallet.Id)
        .FirstOrDefaultAsync(a => a.ScriptPubKey == scriptBytes);
    if (addrEntity is null)
        return Results.NotFound("Address is not owned by this wallet");

    addrEntity.IsExposed = body.Exposed;
    await db.SaveChangesAsync();
    bus.Publish("addresses");

    return Results.Ok(new { address, exposed = addrEntity.IsExposed });
}).WithTags("Wallet");

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

// One-click operationalization of the "exposed UTXOs" dashboard
// recommendation — freezes every live UTXO sitting on an exposed
// address so it can't be co-spent with non-exposed coins until the
// user explicitly unfreezes or remixes. Only touches still-unspent,
// not-already-frozen rows so it's safe to call repeatedly.
// Panic button: clears IsFrozen across every UTXO on the wallet. Counterpart
// to the freeze-exposed / freeze-external-source "bulk freeze" shortcuts —
// when a user has over-quarantined and wants every UTXO selectable again
// without clicking through individual rows. Returns the count that was
// actually unfrozen so the UI can confirm the blast radius.
app.MapPost("/api/coin-control/unfreeze-all", async (WalletDbContext db, DashboardEventBus bus) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(new { unfrozen = 0 });

    var frozen = await db.Utxos
        .Include(u => u.Address)
        .ThenInclude(a => a.Account)
        .Where(u => u.Address.Account.WalletId == wallet.Id)
        .Where(u => u.SpentByTxId == null && u.IsFrozen)
        .ToListAsync();

    foreach (var utxo in frozen)
        utxo.IsFrozen = false;

    await db.SaveChangesAsync();
    if (frozen.Count > 0) bus.Publish("utxos");
    return Results.Ok(new { unfrozen = frozen.Count });
}).WithTags("CoinControl");

app.MapPost("/api/coin-control/freeze-exposed", async (WalletDbContext db, DashboardEventBus bus) =>
{
    var candidates = await db.Utxos
        .Include(u => u.Address)
        .Where(u => u.SpentByTxId == null && !u.IsFrozen && u.Address.IsExposed)
        .ToListAsync();

    foreach (var utxo in candidates)
        utxo.IsFrozen = true;

    await db.SaveChangesAsync();
    if (candidates.Count > 0) bus.Publish("utxos");
    return Results.Ok(new { frozen = candidates.Count });
}).WithTags("CoinControl");

// Companion to freeze-exposed, but keyed on LABEL taint rather than address
// exposure: freezes every live UTXO in a cluster flagged hasExternalSource
// (any label with an "Exchange:" / "KYC:" / "P2P:" prefix). Coin selection
// will then keep those quarantined until they're mixed and the label is
// cleared. Idempotent — only touches still-unspent, not-already-frozen rows.
app.MapPost("/api/coin-control/freeze-external-source", async (WalletDbContext db, DashboardEventBus bus) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(new { frozen = 0 });

    var utxos = await db.Utxos
        .Include(u => u.Address)
        .ThenInclude(a => a.Account)
        .Where(u => u.Address.Account.WalletId == wallet.Id)
        .Where(u => u.SpentByTxId == null)
        .ToListAsync();

    if (utxos.Count == 0) return Results.Ok(new { frozen = 0 });

    var labels = await db.Labels
        .Where(l => l.EntityType == "Utxo" || l.EntityType == "Address")
        .ToListAsync();
    var coinjoins = await db.CoinJoinRecords.ToListAsync();

    var clusters = new Kompaktor.Scoring.LabelClusterAnalyzer()
        .AnalyzeClusters(utxos, labels, coinjoins);
    var taintedIds = clusters
        .Where(c => c.HasExternalSource)
        .SelectMany(c => c.UtxoIds)
        .ToHashSet();

    var candidates = utxos.Where(u => !u.IsFrozen && taintedIds.Contains(u.Id)).ToList();
    foreach (var utxo in candidates)
        utxo.IsFrozen = true;

    await db.SaveChangesAsync();
    if (candidates.Count > 0) bus.Publish("utxos");
    return Results.Ok(new { frozen = candidates.Count });
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

    // Resolve bitcoin-address string from scriptPubKey so the UI drawer can
    // render it without a second round-trip — the same conversion used by
    // the addresses-list endpoint.
    string? address;
    try { address = new Script(utxo.Address.ScriptPubKey).GetDestinationAddress(network)?.ToString(); }
    catch { address = null; }

    // Cluster membership: only load and run the analyzer if the UTXO has a
    // label itself or sits on a labelled address. Otherwise it's a singleton
    // unlabelled cluster and we'd return null anyway — skip the work.
    var addrIdStr = utxo.Address.Id.ToString();
    var utxoIdStr = utxoId.ToString();
    var hasAnyLabel = await db.Labels.AnyAsync(l =>
        (l.EntityType == "Utxo" && l.EntityId == utxoIdStr) ||
        (l.EntityType == "Address" && l.EntityId == addrIdStr));

    object? cluster = null;
    if (hasAnyLabel)
    {
        var walletUtxos = await db.Utxos
            .Include(u => u.Address)
            .ThenInclude(a => a.Account)
            .Where(u => u.Address.Account.WalletId == utxo.Address.Account.WalletId)
            .Where(u => u.SpentByTxId == null)
            .ToListAsync();
        var allLabels = await db.Labels
            .Where(l => l.EntityType == "Utxo" || l.EntityType == "Address")
            .ToListAsync();
        var coinjoins = await db.CoinJoinRecords.ToListAsync();
        var clusters = new Kompaktor.Scoring.LabelClusterAnalyzer()
            .AnalyzeClusters(walletUtxos, allLabels, coinjoins);
        var match = clusters.FirstOrDefault(c =>
            c.Labels.Count > 0 && c.UtxoIds.Contains(utxoId));
        if (match is not null)
            cluster = new
            {
                id = match.UtxoIds.Min(),
                size = match.UtxoIds.Count,
                labels = match.Labels.ToArray(),
                hasExternalSource = match.HasExternalSource
            };
    }

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
        address,
        keyPath = utxo.Address.KeyPath,
        isChange = utxo.Address.IsChange,
        isAddressExposed = utxo.Address.IsExposed,
        labels = labels.Select(l => new { id = l.Id, text = l.Text, createdAt = l.CreatedAt }),
        coinjoinHistory = participations.Select(p => new
        {
            roundId = p.CoinJoinRecord.RoundId,
            role = p.Role,
            status = p.CoinJoinRecord.Status,
            createdAt = p.CoinJoinRecord.CreatedAt
        }),
        cluster
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

// Delete the wallet and wipe ALL local state. Requires the current passphrase
// AND the literal string "DELETE" as an explicit confirmation. Mixing is stopped
// first so the background manager cannot resurrect rows mid-delete. The user
// keeps their mnemonic — restore-from-seed rebuilds everything that derives
// from it; ad-hoc state (labels, address book, webhook history) is gone.
app.MapPost("/api/wallet/delete", async (WalletDbContext db, HttpContext ctx, MixingManager mixer, DashboardEventBus bus) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<DeleteWalletRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Passphrase))
        return Results.BadRequest("Passphrase required");
    if (body.Confirmation != "DELETE")
        return Results.BadRequest("Confirmation must be the literal string \"DELETE\"");

    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    try
    {
        _ = MnemonicEncryption.Decrypt(wallet.EncryptedMnemonic, wallet.MnemonicSalt, body.Passphrase);
    }
    catch (System.Security.Cryptography.CryptographicException)
    {
        return Results.BadRequest("Passphrase is incorrect");
    }

    await mixer.StopAsync();

    // Wipe every table — single-wallet-per-install means nothing here
    // belongs to anyone else. Order: leaves first to satisfy any residual FKs.
    db.WebhookDeliveries.RemoveRange(db.WebhookDeliveries);
    db.PaymentWebhooks.RemoveRange(db.PaymentWebhooks);
    db.PendingPayments.RemoveRange(db.PendingPayments);
    db.PrivacySnapshots.RemoveRange(db.PrivacySnapshots);
    db.FailedRoundInputs.RemoveRange(db.FailedRoundInputs);
    db.AddressBook.RemoveRange(db.AddressBook);
    db.Labels.RemoveRange(db.Labels);
    db.CredentialEvents.RemoveRange(db.CredentialEvents);
    db.CoinJoinParticipations.RemoveRange(db.CoinJoinParticipations);
    db.CoinJoinRecords.RemoveRange(db.CoinJoinRecords);
    db.Utxos.RemoveRange(db.Utxos);
    db.Transactions.RemoveRange(db.Transactions);
    db.Addresses.RemoveRange(db.Addresses);
    db.Accounts.RemoveRange(db.Accounts);
    db.Wallets.RemoveRange(db.Wallets);
    await db.SaveChangesAsync();

    bus.Publish("wallet");
    return Results.Ok(new { deleted = true });
}).WithTags("Wallet");

// Rename the wallet. Cosmetic only - does not affect keys, addresses, or any on-chain state.
app.MapPost("/api/wallet/rename", async (WalletDbContext db, HttpContext ctx, DashboardEventBus bus) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<RenameWalletRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Name))
        return Results.BadRequest("Name required");

    var trimmed = body.Name.Trim();
    if (trimmed.Length > 100)
        return Results.BadRequest("Name must be 100 characters or fewer");

    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    if (wallet.Name == trimmed)
        return Results.Ok(new { renamed = false, name = wallet.Name });

    wallet.Name = trimmed;
    await db.SaveChangesAsync();
    bus.Publish("wallet");

    return Results.Ok(new { renamed = true, name = wallet.Name });
}).WithTags("Wallet");

// Sign an arbitrary message with a wallet-owned address, producing a
// BIP-322 Simple signature. Typical use: attest control of an address
// (e.g. exchange proof-of-ownership) without broadcasting a transaction.
// The address must already belong to this wallet.
app.MapPost("/api/wallet/sign-message", async (
    WalletDbContext db, HttpContext ctx, Network network) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<SignMessageRequest>();
    if (body is null) return Results.BadRequest("Invalid request");
    if (string.IsNullOrWhiteSpace(body.Address))
        return Results.BadRequest("Address required");
    if (body.Message is null)
        return Results.BadRequest("Message required");
    if (string.IsNullOrWhiteSpace(body.Passphrase))
        return Results.BadRequest("Passphrase required to unlock the signing key");

    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    KompaktorHdWallet hdWallet;
    try
    {
        hdWallet = await KompaktorHdWallet.OpenAsync(db, wallet.Id, network, body.Passphrase);
    }
    catch (System.Security.Cryptography.CryptographicException)
    {
        return Results.BadRequest("Passphrase is incorrect");
    }

    try
    {
        var signature = await hdWallet.SignMessageAsync(body.Address, body.Message);
        return Results.Ok(new { address = body.Address, message = body.Message, signature });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).WithTags("Wallet");

// Stateless BIP-322 signature verification. No wallet context is needed,
// so the caller can verify signatures for any address (wallet-owned or not).
app.MapPost("/api/message/verify", (HttpContext ctx, Network network) =>
{
    return HandleVerify(ctx, network);
}).WithTags("Wallet");

static async Task<IResult> HandleVerify(HttpContext ctx, Network network)
{
    var body = await ctx.Request.ReadFromJsonAsync<VerifyMessageRequest>();
    if (body is null) return Results.BadRequest("Invalid request");
    if (string.IsNullOrWhiteSpace(body.Address))
        return Results.BadRequest("Address required");
    if (body.Message is null)
        return Results.BadRequest("Message required");
    if (string.IsNullOrWhiteSpace(body.Signature))
        return Results.BadRequest("Signature required");

    BitcoinAddress address;
    try { address = BitcoinAddress.Create(body.Address, network); }
    catch (FormatException) { return Results.BadRequest("Invalid address for this network"); }

    NBitcoin.BIP322.BIP322Signature sig;
    try { sig = NBitcoin.BIP322.BIP322Signature.Parse(body.Signature, network); }
    catch { return Results.BadRequest("Signature is not a valid BIP-322 base64 payload"); }

    bool valid;
    try { valid = address.VerifyBIP322(body.Message, sig); }
    catch { valid = false; }

    return Results.Ok(new { valid });
}

// Wallet-level preferences for auto-mixing (profile + Tor).
// These are the defaults /api/mixing/start falls back to when the caller
// omits CoordinatorUrl / TorSocks* fields.
app.MapGet("/api/wallet/settings", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    return Results.Ok(new
    {
        mixingProfile = wallet.MixingProfile,
        availableProfiles = MixingProfileCatalog.Names,
        torEnabled = wallet.TorEnabled,
        torSocksHost = wallet.TorSocksHost,
        torSocksPort = wallet.TorSocksPort
    });
}).WithTags("Wallet");

app.MapPost("/api/wallet/settings", async (WalletDbContext db, HttpContext ctx, DashboardEventBus bus) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var body = await ctx.Request.ReadFromJsonAsync<WalletSettingsUpdateRequest>();
    if (body is null) return Results.BadRequest("Missing body");

    if (body.MixingProfile is not null)
    {
        if (!MixingProfileCatalog.IsValid(body.MixingProfile))
            return Results.BadRequest($"Unknown mixingProfile. Allowed: {string.Join(", ", MixingProfileCatalog.Names)}");
        wallet.MixingProfile = body.MixingProfile;
    }

    if (body.TorEnabled.HasValue) wallet.TorEnabled = body.TorEnabled.Value;

    if (body.TorSocksHost is not null)
    {
        // Empty string clears the override so the server default applies again.
        wallet.TorSocksHost = string.IsNullOrWhiteSpace(body.TorSocksHost) ? null : body.TorSocksHost.Trim();
    }

    if (body.TorSocksPort.HasValue)
    {
        var port = body.TorSocksPort.Value;
        if (port != 0 && (port < 1 || port > 65_535))
            return Results.BadRequest("torSocksPort must be between 1 and 65535 (or 0 to clear)");
        wallet.TorSocksPort = port == 0 ? null : port;
    }

    await db.SaveChangesAsync();
    bus.Publish("settings");

    return Results.Ok(new
    {
        mixingProfile = wallet.MixingProfile,
        torEnabled = wallet.TorEnabled,
        torSocksHost = wallet.TorSocksHost,
        torSocksPort = wallet.TorSocksPort
    });
}).WithTags("Wallet");

// Profile recommendation: inspects current wallet state (UTXO shape, prior
// mixing history, external-source taint, payment workflow) and suggests
// which mixing preset fits best. The decision rules are deterministic — no
// ML, just signal thresholds — so the caller can reason about why it picked
// what it did from the `reasons` array.
//
// Priority order (first match wins): establish privacy first > flush KYC
// taint > consolidate fragmentation > payment-heavy flow > steady state.
app.MapGet("/api/wallet/profile-recommendation", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    // includeFrozen: true — frozen UTXOs still count toward fragmentation
    // and taint signals; freezing only blocks auto-selection, not the
    // underlying state the recommendation is assessing.
    var selector = new WalletCoinSelector(db);
    var scored = await selector.GetScoredUtxosAsync(wallet.Id, includeFrozen: true);

    var totalUtxos = scored.Count;
    var unmixedUtxos = scored.Count(s => s.Score.CoinJoinCount == 0);
    const long SmallUtxoThresholdSat = 100_000;
    var smallUtxos = scored.Count(s => s.Utxo.AmountSat < SmallUtxoThresholdSat);

    // External-source taint via label clustering. Count UTXOs (selected or
    // not) that land in any cluster flagged as externally-sourced.
    var utxosForCluster = scored.Select(s => s.Utxo).ToList();
    var allLabels = await db.Labels
        .Where(l => l.EntityType == "Utxo" || l.EntityType == "Address")
        .ToListAsync();
    var allCoinjoins = await db.CoinJoinRecords.ToListAsync();
    var clusters = new LabelClusterAnalyzer()
        .AnalyzeClusters(utxosForCluster, allLabels, allCoinjoins);
    var taintedIds = clusters
        .Where(c => c.HasExternalSource)
        .SelectMany(c => c.UtxoIds)
        .ToHashSet();
    var externallyTaintedUtxos = scored.Count(s => taintedIds.Contains(s.Utxo.Id));

    var completedRounds = await db.CoinJoinRecords.CountAsync(r => r.Status == "Completed");
    // SQLite EF provider cannot translate DateTimeOffset comparisons
    // reliably, so we pull the wallet's outbound payments and count in
    // memory. This is cheap — one wallet's payment history is small.
    var outboundPayments = await db.PendingPayments
        .Where(p => p.WalletId == wallet.Id && p.Direction == "Outbound")
        .ToListAsync();
    var pendingOutboundPayments = outboundPayments.Count(p =>
        p.Status == "Pending" || p.Status == "Reserved" || p.Status == "Committed");
    var thirtyDaysAgo = DateTimeOffset.UtcNow.AddDays(-30);
    var recentOutboundPayments = outboundPayments.Count(p => p.CreatedAt >= thirtyDaysAgo);

    string recommended;
    var reasons = new List<string>();

    if (completedRounds == 0 && unmixedUtxos > 0)
    {
        recommended = "PrivacyFocused";
        reasons.Add($"Wallet has never mixed and has {unmixedUtxos} unmixed UTXO(s) — establish privacy first");
    }
    else if (externallyTaintedUtxos > 0 && unmixedUtxos > 0)
    {
        recommended = "PrivacyFocused";
        reasons.Add($"{externallyTaintedUtxos} UTXO(s) traceable to external source (Exchange/KYC/P2P) — mix out of KYC clusters");
    }
    else if (totalUtxos >= 30 || smallUtxos >= 20)
    {
        recommended = "Consolidator";
        reasons.Add($"{totalUtxos} UTXO(s) ({smallUtxos} under {SmallUtxoThresholdSat:N0} sat) — fragmentation drives fee overhead, consolidate");
    }
    else if (pendingOutboundPayments > 0 || recentOutboundPayments >= 3)
    {
        recommended = "Payments";
        reasons.Add($"{pendingOutboundPayments} pending + {recentOutboundPayments} recent outbound payment(s) — prioritise interactive payment slots");
    }
    else
    {
        recommended = "Balanced";
        reasons.Add("Wallet state is steady — Balanced gives even coverage of mixing, consolidation, and payments");
    }

    if (recommended == wallet.MixingProfile)
        reasons.Add($"Current profile '{wallet.MixingProfile}' already matches — no change needed");

    var alternatives = MixingProfileCatalog.All
        .Where(p => p.Name != recommended)
        .Select(p => new { name = p.Name, description = p.Description })
        .ToList();

    return Results.Ok(new
    {
        recommended,
        current = wallet.MixingProfile,
        reasons,
        signals = new
        {
            totalUtxos,
            unmixedUtxos,
            smallUtxos,
            smallUtxoThresholdSat = SmallUtxoThresholdSat,
            externallyTaintedUtxos,
            completedRounds,
            pendingOutboundPayments,
            recentOutboundPayments
        },
        alternatives
    });
}).WithTags("Wallet");

// Preflight diagnostic for the "Start Mixing" button. Reports whether the
// wallet is in a state where a mixing session could begin and, if not,
// enumerates the specific blockers. The UI polls this to enable/disable
// the CTA and display actionable hints ("no unmixed UTXOs", "Tor daemon
// unreachable", etc). Read-only — does NOT start or stop anything.
app.MapGet("/api/wallet/mixing-readiness", async (WalletDbContext db, MixingManager mixer) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null)
    {
        return Results.Ok(new
        {
            ready = false,
            blockers = new[] { new { code = "no_wallet", message = "No wallet found" } },
            warnings = Array.Empty<object>()
        });
    }

    var blockers = new List<object>();
    var warnings = new List<object>();

    // Already running — mixing can't be started twice, so this is a blocker
    // specifically for "start", not a generic red flag.
    if (mixer.IsRunning)
        blockers.Add(new { code = "already_running", message = "Mixing is already running" });

    var selector = new WalletCoinSelector(db);
    var scored = await selector.GetScoredUtxosAsync(wallet.Id, includeFrozen: true);
    var totalUtxos = scored.Count;
    var unfrozen = scored.Count(s => !s.Utxo.IsFrozen);
    var unmixed = scored.Count(s => !s.Utxo.IsFrozen && s.Score.CoinJoinCount == 0);

    if (totalUtxos == 0)
        blockers.Add(new { code = "no_utxos", message = "Wallet has no UTXOs to mix" });
    else if (unfrozen == 0)
        blockers.Add(new { code = "all_frozen", message = "Every UTXO is frozen — unfreeze at least one to mix" });
    else if (unmixed == 0)
        warnings.Add(new { code = "all_mixed", message = "Every unfrozen UTXO has already been mixed; another pass would add little privacy" });

    // Fresh receive / change addresses must exist or the coinjoin client
    // can't assign outputs. We surface this as a blocker because there's no
    // auto-extension path outside the wallet-signing flow.
    var freshChangeCount = await db.Addresses
        .Include(a => a.Account)
        .Where(a => a.Account.WalletId == wallet.Id)
        .CountAsync(a => !a.IsUsed && !a.IsExposed && a.IsChange);
    if (freshChangeCount == 0)
        blockers.Add(new { code = "no_fresh_change_addresses", message = "No fresh change addresses available — receive some funds or extend your xpub" });

    // Tor preflight: if the wallet has Tor enabled, check reachability NOW
    // instead of discovering it at mixing start time (where the error path
    // currently falls back to clearnet without warning).
    if (wallet.TorEnabled && !string.IsNullOrWhiteSpace(wallet.TorSocksHost))
    {
        var probe = await TorReachability.ProbeAsync(
            wallet.TorSocksHost!, wallet.TorSocksPort ?? 9050);
        if (!probe.Reachable)
        {
            blockers.Add(new
            {
                code = "tor_unreachable",
                message = $"Tor daemon unreachable at {wallet.TorSocksHost}:{wallet.TorSocksPort ?? 9050} — {probe.Error}"
            });
        }
    }

    // Backup not verified is a warning, not a blocker — the user can still
    // mix but they're risking funds on a key they've never confirmed they
    // have written down.
    if (!wallet.IsBackupVerified)
        warnings.Add(new { code = "backup_unverified", message = "Wallet backup has not been verified — confirm your mnemonic before large mixing sessions" });

    return Results.Ok(new
    {
        ready = blockers.Count == 0,
        profile = wallet.MixingProfile,
        signals = new { totalUtxos, unfrozen, unmixed, freshChangeAddresses = freshChangeCount },
        blockers,
        warnings
    });
}).WithTags("Wallet");

// Decodes a profile spec into the list of behavior traits the mixer will run
// for that preset, each with a plain-language purpose string. Lets the UI
// show "if you pick X, your wallet will run A, B, C and here's what each
// does" without the UI having to know the mapping. Unknown/empty profile
// falls back to the wallet's persisted choice (then to Balanced via Get()).
app.MapGet("/api/wallet/behavior-traits/{profileName?}", async (string? profileName, WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    // Caller-supplied name wins when valid; otherwise fall back to what the
    // wallet actually persisted. MixingProfileCatalog.Get() further defaults
    // to "Balanced" if that string is unknown — so the endpoint can never
    // 404 on "valid wallet + weird name".
    var resolvedName = !string.IsNullOrWhiteSpace(profileName) && MixingProfileCatalog.IsValid(profileName)
        ? profileName
        : wallet.MixingProfile;
    var spec = MixingProfileCatalog.Get(resolvedName);

    var traits = new List<object>
    {
        new
        {
            name = "ConsolidationBehaviorTrait",
            purpose = "Caps how many of your UTXOs the mixer queues per coinjoin round",
            config = new { inputsPerRound = spec.ConsolidationThreshold }
        },
        new
        {
            name = "SelfSendChangeBehaviorTrait",
            purpose = "Delays re-mixing of coinjoin change so it does not immediately reattach to the parent round",
            config = new { delaySeconds = spec.SelfSendDelaySeconds }
        }
    };

    if (spec.InteractivePaymentsEnabled)
    {
        traits.Add(new
        {
            name = "InteractivePaymentSenderBehaviorTrait",
            purpose = "Opts queued outgoing payments into coinjoin rounds via peer credential transfer",
            config = new { }
        });
        traits.Add(new
        {
            name = "InteractivePaymentReceiverBehaviorTrait",
            purpose = "Accepts incoming interactive payments from peers during coinjoin rounds",
            config = new { }
        });
    }

    return Results.Ok(new
    {
        profile = spec.Name,
        description = spec.Description,
        requestedProfile = profileName,
        currentProfile = wallet.MixingProfile,
        traits
    });
}).WithTags("Wallet");

// Probes whether a SOCKS5 proxy (usually a Tor daemon) is reachable on the
// given host/port. Performs the no-auth SOCKS5 greeting so we can tell a
// live proxy from "port happens to be open on some other service."
app.MapPost("/api/wallet/test-tor", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<TorTestRequest>();
    var host = string.IsNullOrWhiteSpace(body?.Host) ? "127.0.0.1" : body.Host;
    var port = body?.Port is > 0 and <= 65535 ? body.Port.Value : 9050;

    var result = await TorReachability.ProbeAsync(host, port, ctx.RequestAborted);
    if (!result.Reachable)
        return Results.Ok(new { reachable = false, error = result.Error });
    return Results.Ok(new
    {
        reachable = true,
        latencyMs = result.LatencyMs,
        authMethod = result.AuthMethod
    });
}).WithTags("Wallet");

// Probes whether a Kompaktor coordinator URL is reachable and responds
// to the /api/rounds endpoint. Lets the UI validate a custom coordinator
// *before* the user commits their passphrase and starts mixing. Clearnet
// only — users can separately validate Tor with /api/wallet/test-tor.
app.MapPost("/api/wallet/test-coordinator", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<CoordinatorTestRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Url))
        return Results.Ok(new { reachable = false, error = "missing url" });

    if (!Uri.TryCreate(body.Url.Trim(), UriKind.Absolute, out var baseUri))
        return Results.Ok(new { reachable = false, error = "invalid url" });

    if (baseUri.Scheme is not ("http" or "https"))
        return Results.Ok(new { reachable = false, error = "scheme must be http or https" });

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    var probeUri = new Uri(baseUri, "/api/rounds");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        using var resp = await http.GetAsync(probeUri, ctx.RequestAborted);
        sw.Stop();
        if (!resp.IsSuccessStatusCode)
            return Results.Ok(new { reachable = false, latencyMs = sw.ElapsedMilliseconds, error = $"HTTP {(int)resp.StatusCode}" });

        // Best-effort round count — a healthy coordinator returns a JSON
        // array even if empty. Any parse failure still counts as reachable
        // (the endpoint answered) so the user knows the URL is live.
        int? roundCount = null;
        try
        {
            var payload = await resp.Content.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted);
            if (payload.ValueKind == JsonValueKind.Array)
                roundCount = payload.GetArrayLength();
        }
        catch { /* body not JSON or shape differs — reachability still holds */ }

        return Results.Ok(new
        {
            reachable = true,
            latencyMs = sw.ElapsedMilliseconds,
            roundCount
        });
    }
    catch (TaskCanceledException)
    {
        return Results.Ok(new { reachable = false, error = "timeout" });
    }
    catch (HttpRequestException ex)
    {
        return Results.Ok(new { reachable = false, error = ex.Message });
    }
}).WithTags("Wallet");

// Get receive address
app.MapGet("/api/wallet/receive-address", async (WalletDbContext db, string? type = null) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    int? purposeFilter;
    try { purposeFilter = ParseAddressType(type); }
    catch (FormatException ex) { return Results.BadRequest(ex.Message); }

    var q = db.Addresses
        .Include(a => a.Account)
        .Where(a => a.Account.WalletId == wallet.Id)
        .Where(a => !a.IsUsed && !a.IsExposed && !a.IsChange);
    if (purposeFilter.HasValue)
        q = q.Where(a => a.Account.Purpose == purposeFilter.Value);

    var address = await q
        .OrderByDescending(a => a.Account.Purpose) // Prefer P2TR (86)
        .ThenBy(a => a.Id)
        .FirstOrDefaultAsync();

    if (address is null) return Results.BadRequest(
        purposeFilter.HasValue
            ? $"No fresh addresses available of type '{type}'"
            : "No fresh addresses available");

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
    string? message = null,
    string? type = null) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    int? purposeFilter;
    try { purposeFilter = ParseAddressType(type); }
    catch (FormatException ex) { return Results.BadRequest(ex.Message); }

    var q = db.Addresses
        .Include(a => a.Account)
        .Where(a => a.Account.WalletId == wallet.Id)
        .Where(a => !a.IsUsed && !a.IsExposed && !a.IsChange);
    if (purposeFilter.HasValue)
        q = q.Where(a => a.Account.Purpose == purposeFilter.Value);

    var address = await q
        .OrderByDescending(a => a.Account.Purpose)
        .ThenBy(a => a.Id)
        .FirstOrDefaultAsync();

    if (address is null) return Results.BadRequest(
        purposeFilter.HasValue
            ? $"No fresh addresses available of type '{type}'"
            : "No fresh addresses available");

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
    string? message = null,
    string? type = null) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    int? purposeFilter;
    try { purposeFilter = ParseAddressType(type); }
    catch (FormatException ex) { return Results.BadRequest(ex.Message); }

    var q = db.Addresses
        .Include(a => a.Account)
        .Where(a => a.Account.WalletId == wallet.Id)
        .Where(a => !a.IsUsed && !a.IsExposed && !a.IsChange);
    if (purposeFilter.HasValue)
        q = q.Where(a => a.Account.Purpose == purposeFilter.Value);

    var address = await q
        .OrderByDescending(a => a.Account.Purpose)
        .ThenBy(a => a.Id)
        .FirstOrDefaultAsync();

    if (address is null) return Results.BadRequest(
        purposeFilter.HasValue
            ? $"No fresh addresses available of type '{type}'"
            : "No fresh addresses available");

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

// Maps the user-facing "type" query param on /receive-* endpoints to the
// BIP-43 purpose number stored on the account. Returns null when no type
// was requested (caller falls back to P2TR-preferred default). Throws
// FormatException when the value is present but unrecognised.
static int? ParseAddressType(string? type)
{
    if (string.IsNullOrWhiteSpace(type)) return null;
    return type.Trim().ToUpperInvariant() switch
    {
        "P2TR" or "TAPROOT" => 86,
        "P2WPKH" or "SEGWIT" => 84,
        _ => throw new FormatException($"Unknown address type '{type}'. Use 'p2tr' or 'p2wpkh'.")
    };
}

// Parses "txid:vout" strings into outpoint tuples. Returns null when the
// input is null/empty so callers can distinguish "no selection" from an
// empty-but-specified list. Throws FormatException on malformed entries so
// the endpoint can turn it into a 400 response.
static List<(string TxId, int OutputIndex)>? ParseOutpoints(string[]? raw)
{
    if (raw is null || raw.Length == 0) return null;
    var result = new List<(string, int)>(raw.Length);
    foreach (var entry in raw)
    {
        if (string.IsNullOrWhiteSpace(entry))
            throw new FormatException("Empty outpoint entry");
        var sep = entry.LastIndexOf(':');
        if (sep <= 0 || sep == entry.Length - 1)
            throw new FormatException($"Outpoint '{entry}' must be formatted as 'txid:vout'");
        var txidPart = entry[..sep];
        var voutPart = entry[(sep + 1)..];
        if (txidPart.Length != 64 || !txidPart.All(Uri.IsHexDigit))
            throw new FormatException($"Outpoint '{entry}' has an invalid txid");
        if (!int.TryParse(voutPart, out var vout) || vout < 0)
            throw new FormatException($"Outpoint '{entry}' has an invalid vout");
        result.Add((txidPart, vout));
    }
    return result;
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

// Passphrase rotation. The passphrase encrypts the locally stored mnemonic
// via AES-256-GCM keyed by PBKDF2 — it's a storage-encryption secret, not a
// BIP39 passphrase, so changing it doesn't alter addresses or recovery. The
// user's written mnemonic still restores the wallet without any passphrase.
app.MapPost("/api/wallet/change-passphrase", async (WalletDbContext db, HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<ChangePassphraseRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.CurrentPassphrase) || string.IsNullOrWhiteSpace(body.NewPassphrase))
        return Results.BadRequest("Current and new passphrases required");

    if (body.CurrentPassphrase == body.NewPassphrase)
        return Results.BadRequest("New passphrase must differ from current");

    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    string mnemonic;
    try
    {
        mnemonic = MnemonicEncryption.Decrypt(wallet.EncryptedMnemonic, wallet.MnemonicSalt, body.CurrentPassphrase);
    }
    catch (System.Security.Cryptography.CryptographicException)
    {
        return Results.BadRequest("Current passphrase is incorrect");
    }

    var (encrypted, salt) = MnemonicEncryption.Encrypt(mnemonic, body.NewPassphrase);
    wallet.EncryptedMnemonic = encrypted;
    wallet.MnemonicSalt = salt;
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        changed = true,
        message = "Passphrase updated. Use the new passphrase for all future sensitive operations (sending payments, starting mixing, exporting keys). Your recovery phrase and addresses are unchanged."
    });
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

    // Flag exposed-address UTXOs: scripts that were revealed in a prior
    // coinjoin round (often a failed one). Co-spending these with
    // non-exposed UTXOs is the #1 foot-gun the planner warns about,
    // so surface them at the dashboard level too. Frozen exposed UTXOs
    // don't trigger the rec — freezing is the remediation we offer.
    var exposedCount = scored.Count(s => s.Utxo.Address.IsExposed && !s.Utxo.IsFrozen);
    if (exposedCount > 0)
    {
        recommendations.Add(new
        {
            priority = "high",
            title = $"{exposedCount} UTXO{(exposedCount > 1 ? "s" : "")} on Exposed Addresses",
            message = "These sit on scripts revealed in a prior coinjoin round. " +
                      "Co-spending them with non-exposed UTXOs links the two sets together. " +
                      "Either remix them or freeze them before sending."
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

    // Same SQLite DateTimeOffset trap as /webhooks/{id}/deliveries — filter
    // by wallet at the DB, then timestamp-filter + order + project in memory.
    var snapshots = (await db.PrivacySnapshots
            .Where(s => s.WalletId == wallet.Id)
            .ToListAsync())
        .Where(s => s.Timestamp >= since)
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
        .ToList();

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
        // Use a List<string> rather than string[] here — in .NET 10 the C# compiler
        // resolves `string[].Contains(string)` to the new MemoryExtensions.Contains
        // ReadOnlySpan overload, which EF Core's expression funcletizer can't compile.
        var statuses = status
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
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
            body.Destination, body.AmountSat, body.Interactive, body.Urgent, body.Label, expiry, body.ScheduledAt, body.MaxRetries);

        bus.Publish("payments");
        return Results.Ok(new
        {
            entity.Id, entity.Direction, entity.AmountSat,
            amountBtc = entity.AmountSat / 100_000_000.0,
            entity.Destination, entity.Status, entity.IsInteractive,
            kompaktorPubKey = entity.KompaktorKeyHex,
            entity.Label, entity.CreatedAt, entity.ScheduledAt, entity.MaxRetries
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
                item.Destination, item.AmountSat, item.Interactive, item.Urgent, item.Label, expiry, item.ScheduledAt, item.MaxRetries);
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

// Bulk retry: flips every Failed outbound payment back to Pending and
// resets RetryCount so the retry budget is fresh. Symmetric counterpart
// to bulk-cancel. Intended for "I fixed the thing that was failing them,
// give them all another shot" workflows (restored coordinator, fresh
// funding, etc). Fires one ManuallyRetried webhook per row so receivers
// can notice the reset and consumers reuse the single-retry handler.
app.MapPost("/api/payments/batch-retry", async (WalletDbContext db, DashboardEventBus bus) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var failed = await db.PendingPayments
        .Where(p => p.WalletId == wallet.Id && p.Direction == "Outbound" && p.Status == "Failed")
        .ToListAsync();

    foreach (var entity in failed)
    {
        entity.Status = "Pending";
        entity.RetryCount = 0;
    }
    await db.SaveChangesAsync();

    if (failed.Count > 0)
    {
        bus.Publish("payments");
        var webhookSvc = new PaymentWebhookService(db, wallet.Id);
        foreach (var entity in failed)
            _ = webhookSvc.DeliverAsync(entity, "ManuallyRetried");
    }

    return Results.Ok(new { retried = failed.Count });
}).WithTags("Payments");

// Bulk cancel: loops over every cancellable outbound payment on the wallet
// and flips it to Failed. Skips rows that are already Completed or Committed
// (those are irreversible). Fires one webhook per cancelled payment so each
// receiver sees a distinct "Failed" event — this matches the single-delete
// semantics so webhook consumers can reuse the same handler code path.
// Intended use: "stop everything" during a reconfiguration or emergency.
app.MapPost("/api/payments/bulk-cancel", async (WalletDbContext db, DashboardEventBus bus) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var manager = new WalletPaymentManager(db, wallet.Id, network);
    var cancellable = await db.PendingPayments
        .Where(p => p.WalletId == wallet.Id && p.Direction == "Outbound")
        .Where(p => p.Status != "Completed" && p.Status != "Committed" && p.Status != "Failed")
        .Select(p => p.Id)
        .ToListAsync();

    var cancelled = 0;
    var webhookSvc = new PaymentWebhookService(db, wallet.Id);
    foreach (var id in cancellable)
    {
        if (!await manager.CancelPaymentAsync(id)) continue;
        cancelled++;
        // Fire webhook post-cancellation so the receiver sees a Failed
        // event for each payment individually — consistent with the
        // single-payment DELETE path.
        var entity = await db.PendingPayments.FindAsync(id);
        if (entity is not null)
            _ = webhookSvc.DeliverAsync(entity, "Failed");
    }

    if (cancelled > 0) bus.Publish("payments");
    return Results.Ok(new { cancelled, considered = cancellable.Count });
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

app.MapPost("/api/payments/{paymentId}/retry", async (string paymentId, WalletDbContext db, DashboardEventBus bus) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var entity = await db.PendingPayments.FindAsync(paymentId);
    if (entity is null || entity.WalletId != wallet.Id) return Results.NotFound();

    if (entity.Status != "Failed")
        return Results.BadRequest($"Only Failed payments can be retried (current status: {entity.Status})");

    entity.Status = "Pending";
    entity.RetryCount = 0;
    await db.SaveChangesAsync();

    bus.Publish("payments");

    var webhookSvc = new PaymentWebhookService(db, wallet.Id);
    _ = webhookSvc.DeliverAsync(entity, "ManuallyRetried");

    return Results.Ok(new { paymentId, status = "pending", retryCount = 0 });
}).WithTags("Payments");

// Force-activate a Scheduled payment immediately, bypassing its ScheduledAt.
// The dormant/active flip normally happens as a side-effect of the payment
// list sweep; this endpoint lets the user say "run it now" explicitly — and
// gives tests a deterministic trigger instead of polling.
app.MapPost("/api/payments/{paymentId}/activate", async (
    string paymentId, WalletDbContext db, DashboardEventBus bus) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var entity = await db.PendingPayments.FindAsync(paymentId);
    if (entity is null || entity.WalletId != wallet.Id) return Results.NotFound();

    if (entity.Status != "Scheduled")
        return Results.BadRequest(
            $"Only Scheduled payments can be activated (current status: {entity.Status})");

    entity.Status = "Pending";
    entity.ScheduledAt = null;
    await db.SaveChangesAsync();

    bus.Publish("payments");

    try
    {
        var webhookSvc = new PaymentWebhookService(db, wallet.Id);
        await webhookSvc.DeliverAsync(entity, "Activated");
    }
    catch { }

    return Results.Ok(new { paymentId, status = "Pending" });
}).WithTags("Payments");

// Rename/clear a payment's label after the fact. Label is user-private
// display text — editing it never affects protocol behavior. Allowed at
// any status because the user might classify historical payments for
// their own accounting.
app.MapPatch("/api/payments/{paymentId}", async (
    string paymentId, WalletDbContext db, HttpContext ctx, DashboardEventBus bus) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var entity = await db.PendingPayments.FindAsync(paymentId);
    if (entity is null || entity.WalletId != wallet.Id) return Results.NotFound();

    // Read raw JSON so we can distinguish "field omitted" (no-op) from
    // "field present with null" (clear). C# record binding collapses the two.
    JsonElement body;
    try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(); }
    catch { return Results.BadRequest("Request body required"); }
    if (body.ValueKind != JsonValueKind.Object)
        return Results.BadRequest("Request body required");

    var touchesTiming = body.TryGetProperty("scheduledAt", out _)
                        || body.TryGetProperty("expiresAt", out _);
    if (touchesTiming && entity.Status is "Completed" or "Failed")
        return Results.BadRequest($"Cannot modify timing on {entity.Status.ToLowerInvariant()} payment");

    if (body.TryGetProperty("label", out var labelEl))
    {
        var text = labelEl.ValueKind == JsonValueKind.String ? labelEl.GetString() : null;
        entity.Label = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    if (body.TryGetProperty("scheduledAt", out var schedEl))
    {
        entity.ScheduledAt = schedEl.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number => DateTimeOffset.FromUnixTimeSeconds(schedEl.GetInt64()),
            _ => throw new BadHttpRequestException("scheduledAt must be unix seconds or null")
        };
        // Keep status in sync with the new schedule time. A schedule pushed
        // into the future dormants the payment; a null/past schedule wakes it.
        var now = DateTimeOffset.UtcNow;
        var isFutureSchedule = entity.ScheduledAt.HasValue && entity.ScheduledAt.Value > now;
        if (isFutureSchedule && entity.Status == "Pending") entity.Status = "Scheduled";
        else if (!isFutureSchedule && entity.Status == "Scheduled") entity.Status = "Pending";
    }

    if (body.TryGetProperty("expiresAt", out var expEl))
    {
        entity.ExpiresAt = expEl.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number => DateTimeOffset.FromUnixTimeSeconds(expEl.GetInt64()),
            _ => throw new BadHttpRequestException("expiresAt must be unix seconds or null")
        };
    }

    await db.SaveChangesAsync();
    bus.Publish("payments");

    return Results.Ok(new
    {
        entity.Id, entity.Label, entity.Status, entity.AmountSat, entity.Destination,
        entity.ScheduledAt, entity.ExpiresAt
    });
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
        entity.RetryCount, entity.MaxRetries, entity.Label, entity.CompletedTxId, entity.ProofJson,
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

// Pause/resume a webhook without dropping its delivery history. Useful when a
// receiver is being migrated or debugged — deleting would erase history and
// force a new secret; toggling IsActive leaves everything in place.
app.MapPatch("/api/webhooks/{webhookId}", async (int webhookId, WalletDbContext db, HttpContext ctx) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var webhook = await db.PaymentWebhooks.FindAsync(webhookId);
    if (webhook is null || webhook.WalletId != wallet.Id) return Results.NotFound();

    var body = await ctx.Request.ReadFromJsonAsync<WebhookUpdateRequest>();
    if (body is null) return Results.BadRequest("Body required");

    if (body.IsActive is bool active)
        webhook.IsActive = active;

    if (body.Url is { } url)
    {
        // Validate before saving. Accepting garbage would leave a row that
        // fails every delivery attempt, polluting the history with noise.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return Results.BadRequest("Url must be http or https");
        webhook.Url = url;
    }

    if (body.EventFilter is { } filter)
    {
        var trimmed = filter.Trim();
        // Empty string is meaningless for a filter. "*" or a comma list
        // is how MatchesFilter is implemented.
        if (trimmed.Length == 0)
            return Results.BadRequest("EventFilter cannot be empty; use \"*\" to match all events");
        webhook.EventFilter = trimmed;
    }

    await db.SaveChangesAsync();
    return Results.Ok(new
    {
        id = webhook.Id,
        url = webhook.Url,
        isActive = webhook.IsActive,
        eventFilter = webhook.EventFilter
    });
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

// Manually retry a past delivery. Sends a fresh POST to the same webhook using the
// payment's current state with the original event type — we don't persist the original
// payload, so this is "replay the event against current state" rather than "replay the
// exact bytes". Useful when a receiver was down; lets operators recover without waiting
// for the payment to change state again.
app.MapPost("/api/webhooks/{webhookId}/deliveries/{deliveryId}/redeliver",
    async (int webhookId, int deliveryId, WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var webhook = await db.PaymentWebhooks.FindAsync(webhookId);
    if (webhook is null || webhook.WalletId != wallet.Id) return Results.NotFound();

    var delivery = await db.WebhookDeliveries.FindAsync(deliveryId);
    if (delivery is null || delivery.WebhookId != webhookId) return Results.NotFound();

    var payment = await db.PendingPayments.FindAsync(delivery.PaymentId);
    if (payment is null || payment.WalletId != wallet.Id)
        return Results.StatusCode(410); // Gone — payment no longer exists

    var svc = new PaymentWebhookService(db, wallet.Id);
    var fresh = await svc.RedeliverAsync(webhook, payment, delivery.EventType);

    return Results.Ok(new
    {
        id = fresh.Id,
        paymentId = fresh.PaymentId,
        eventType = fresh.EventType,
        httpStatusCode = fresh.HttpStatusCode,
        success = fresh.Success,
        errorMessage = fresh.ErrorMessage,
        timestamp = fresh.Timestamp
    });
}).WithTags("Webhooks");

// Generate a fresh HMAC secret and atomically replace the old one. The
// returned secret is the one-time reveal — once the response is consumed
// the secret is no longer available via GET. Use this when the previous
// secret may have leaked; deleting + recreating would also drop delivery
// history, which rotation preserves.
app.MapPost("/api/webhooks/{webhookId}/rotate-secret", async (int webhookId, WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var webhook = await db.PaymentWebhooks.FindAsync(webhookId);
    if (webhook is null || webhook.WalletId != wallet.Id) return Results.NotFound();

    var newSecret = Convert.ToHexString(
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    webhook.Secret = newSecret;
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        id = webhook.Id,
        secret = newSecret,
        message = "Store the new secret — it won't be shown again. The old secret is invalidated immediately; any receiver still verifying with it will reject subsequent deliveries."
    });
}).WithTags("Webhooks");

// Sends a synthetic "Test" event to the webhook URL so the user can verify
// their receiver is wired up correctly before counting on it for real
// payment events. Records a delivery like any other attempt so the result
// is visible in the deliveries history.
app.MapPost("/api/webhooks/{webhookId}/test", async (int webhookId, WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    var webhook = await db.PaymentWebhooks.FindAsync(webhookId);
    if (webhook is null || webhook.WalletId != wallet.Id) return Results.NotFound();

    var svc = new PaymentWebhookService(db, wallet.Id);
    var delivery = await svc.SendTestAsync(webhook);

    return Results.Ok(new
    {
        deliveryId = delivery.Id,
        httpStatusCode = delivery.HttpStatusCode,
        success = delivery.Success,
        errorMessage = delivery.ErrorMessage,
        timestamp = delivery.Timestamp
    });
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

app.MapPut("/api/address-book/{entryId}", async (int entryId, WalletDbContext db, HttpContext ctx) =>
{
    var entry = await db.AddressBook.FindAsync(entryId);
    if (entry is null) return Results.NotFound();

    var body = await ctx.Request.ReadFromJsonAsync<AddressBookRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Address) || string.IsNullOrWhiteSpace(body.Label))
        return Results.BadRequest("Label and address required");

    try { BitcoinAddress.Create(body.Address.Trim(), network); }
    catch { return Results.BadRequest("Invalid Bitcoin address"); }

    // If the address is changing, make sure no *other* entry in the same
    // wallet already owns it. Keeping the same address on the same row
    // (pure rename) must still be allowed.
    var newAddress = body.Address.Trim();
    var collision = await db.AddressBook.AnyAsync(a =>
        a.WalletId == entry.WalletId && a.Id != entry.Id && a.Address == newAddress);
    if (collision) return Results.BadRequest("Address already in address book");

    entry.Label = body.Label.Trim();
    entry.Address = newAddress;
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

    List<(string TxId, int OutputIndex)>? specific;
    try { specific = ParseOutpoints(body.SelectedOutpoints); }
    catch (FormatException ex) { return Results.BadRequest(ex.Message); }

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
            wallet.Id, destination.ScriptPubKey, amount, feeRate, strategy, specific);

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

    List<(string TxId, int OutputIndex)>? specific;
    try { specific = ParseOutpoints(body.SelectedOutpoints); }
    catch (FormatException ex) { return Results.BadRequest(ex.Message); }

    try
    {
        var destination = BitcoinAddress.Create(body.Destination, network);
        var amount = Money.Satoshis(body.AmountSat);
        var feeRate = new FeeRate(Money.Satoshis(body.FeeRateSatPerVb), 1);
        var strategy = Enum.TryParse<CoinSelectionStrategy>(body.Strategy, true, out var s)
            ? s : CoinSelectionStrategy.PrivacyFirst;

        var txBuilder = new WalletTransactionBuilder(db, network);
        var plan = await txBuilder.PlanTransactionAsync(
            wallet.Id, destination.ScriptPubKey, amount, feeRate, strategy, specific);

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

// BIP329 labels export: JSONL format for wallet portability. Emits one JSON
// object per line so labels can be streamed and appended across tools that
// implement the standard (Sparrow, Electrum, etc.). Covers:
//   - addr    : address book entries
//   - tx      : transaction notes (LabelEntity EntityType=Transaction)
//   - output  : UTXO labels, ref normalised to "txid:vout" as the spec requires
// Lists every distinct label text the user has applied, with counts by
// entity type. Powers the label-autocomplete and tag-cloud views: the UI
// can show "what labels do I use, and how often" without scanning every
// UTXO/address row client-side. Includes AddressBook labels too since
// they're part of the same user-facing taxonomy even though they live in
// a separate table.
app.MapGet("/api/wallet/labels", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.Ok(new { totalDistinct = 0, labels = Array.Empty<object>() });

    // Polymorphic Labels table: only rows pertaining to owned entities. For
    // Address/Utxo we can filter by walletId via joins; Transaction rows need
    // a scoped check too. We read them all and filter in memory — the label
    // table isn't large enough to care about query-shape perfection here.
    var allLabels = await db.Labels.ToListAsync();
    var addressBook = await db.AddressBook
        .Where(a => a.WalletId == wallet.Id)
        .ToListAsync();

    // Scope: labels on entities this wallet owns. Utxo label's EntityId is
    // the UtxoEntity.Id; Address label's EntityId is AddressEntity.Id.
    var ownedUtxoIds = (await db.Utxos
            .Include(u => u.Address)
            .ThenInclude(a => a.Account)
            .Where(u => u.Address.Account.WalletId == wallet.Id)
            .Select(u => u.Id)
            .ToListAsync())
        .Select(id => id.ToString()).ToHashSet();
    var ownedAddrIds = (await db.Addresses
            .Include(a => a.Account)
            .Where(a => a.Account.WalletId == wallet.Id)
            .Select(a => a.Id)
            .ToListAsync())
        .Select(id => id.ToString()).ToHashSet();
    var walletTxIds = (await db.Utxos
            .Include(u => u.Address)
            .ThenInclude(a => a.Account)
            .Where(u => u.Address.Account.WalletId == wallet.Id)
            .Select(u => u.TxId)
            .Distinct()
            .ToListAsync())
        .ToHashSet();

    var aggregates = new Dictionary<string, (int utxo, int address, int transaction, int addressBook)>(
        StringComparer.Ordinal);

    void Bump(string text, string kind)
    {
        (int utxo, int address, int transaction, int addressBook) cur =
            aggregates.TryGetValue(text, out var v) ? v : (0, 0, 0, 0);
        aggregates[text] = kind switch
        {
            "Utxo" => (cur.utxo + 1, cur.address, cur.transaction, cur.addressBook),
            "Address" => (cur.utxo, cur.address + 1, cur.transaction, cur.addressBook),
            "Transaction" => (cur.utxo, cur.address, cur.transaction + 1, cur.addressBook),
            "AddressBook" => (cur.utxo, cur.address, cur.transaction, cur.addressBook + 1),
            _ => cur
        };
    }

    foreach (var l in allLabels)
    {
        var belongs = l.EntityType switch
        {
            "Utxo" => ownedUtxoIds.Contains(l.EntityId),
            "Address" => ownedAddrIds.Contains(l.EntityId),
            "Transaction" => walletTxIds.Contains(l.EntityId),
            _ => false
        };
        if (belongs) Bump(l.Text, l.EntityType);
    }
    foreach (var a in addressBook)
        Bump(a.Label, "AddressBook");

    var sorted = aggregates
        .Select(kv => new
        {
            text = kv.Key,
            total = kv.Value.utxo + kv.Value.address + kv.Value.transaction + kv.Value.addressBook,
            utxoCount = kv.Value.utxo,
            addressCount = kv.Value.address,
            transactionCount = kv.Value.transaction,
            addressBookCount = kv.Value.addressBook,
            // Flag externally-sourced convention prefixes so the UI can render
            // the tag with a taint badge without re-parsing the text.
            hasExternalSource =
                kv.Key.StartsWith("Exchange:", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.StartsWith("KYC:", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.StartsWith("P2P:", StringComparison.OrdinalIgnoreCase)
        })
        .OrderByDescending(x => x.total)
        .ThenBy(x => x.text, StringComparer.Ordinal)
        .ToList();

    return Results.Ok(new { totalDistinct = sorted.Count, labels = sorted });
}).WithTags("Wallet");

//   - xpub    : account-level extended public keys
// The endpoint is watch-only — no key material, no proofs — so it's safe to
// share the export with other wallets or a paper backup of label history.
app.MapGet("/api/wallet/labels/bip329", async (WalletDbContext db) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.NotFound("No wallet found");

    var lines = new List<string>();
    var jsonOpts = new JsonSerializerOptions { WriteIndented = false };

    // addr: counterparty labels from the address book.
    var addressBook = await db.AddressBook
        .Where(a => a.WalletId == wallet.Id)
        .ToListAsync();
    foreach (var entry in addressBook)
    {
        lines.Add(JsonSerializer.Serialize(new
        {
            type = "addr",
            @ref = entry.Address,
            label = entry.Label
        }, jsonOpts));
    }

    // tx: transaction notes. Multiple notes per tx collapse into one label
    // separated by " | " — BIP329 has no notion of multiple labels for a ref.
    var txLabels = await db.Labels
        .Where(l => l.EntityType == "Transaction")
        .ToListAsync();
    foreach (var group in txLabels.GroupBy(l => l.EntityId))
    {
        lines.Add(JsonSerializer.Serialize(new
        {
            type = "tx",
            @ref = group.Key,
            label = string.Join(" | ", group.Select(l => l.Text))
        }, jsonOpts));
    }

    // output: UTXO labels. Internal EntityId is the UtxoEntity primary-key int,
    // so we join back to the Utxos table to reconstruct the "txid:vout" form
    // BIP329 consumers expect.
    var utxoLabels = await db.Labels
        .Where(l => l.EntityType == "Utxo")
        .ToListAsync();
    if (utxoLabels.Count > 0)
    {
        var utxoIds = utxoLabels
            .Select(l => int.TryParse(l.EntityId, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        var utxos = await db.Utxos
            .Where(u => utxoIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => $"{u.TxId}:{u.OutputIndex}");
        foreach (var group in utxoLabels.GroupBy(l => l.EntityId))
        {
            if (!int.TryParse(group.Key, out var utxoId)) continue;
            if (!utxos.TryGetValue(utxoId, out var outpoint)) continue;
            lines.Add(JsonSerializer.Serialize(new
            {
                type = "output",
                @ref = outpoint,
                label = string.Join(" | ", group.Select(l => l.Text))
            }, jsonOpts));
        }
    }

    // addr (owned): labels the user attached to their own receive/change
    // addresses via /api/wallet/addresses/{address}/label. Internal EntityId
    // is the AddressEntity int PK, so we join back to Addresses → derive the
    // bitcoin-address string from ScriptPubKey for BIP329's addr ref format.
    // Merged into the same "addr" type as the address book — BIP329 has no
    // distinction between owned and counterparty addresses.
    var ownedAddrLabels = await db.Labels
        .Where(l => l.EntityType == "Address")
        .ToListAsync();
    if (ownedAddrLabels.Count > 0)
    {
        var addrIds = ownedAddrLabels
            .Select(l => int.TryParse(l.EntityId, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        var ownedAddrs = await db.Addresses
            .Include(a => a.Account)
            .Where(a => a.Account.WalletId == wallet.Id && addrIds.Contains(a.Id))
            .ToListAsync();
        var addrById = ownedAddrs.ToDictionary(a => a.Id, a =>
        {
            try { return new Script(a.ScriptPubKey).GetDestinationAddress(network)?.ToString(); }
            catch { return null; }
        });
        foreach (var group in ownedAddrLabels.GroupBy(l => l.EntityId))
        {
            if (!int.TryParse(group.Key, out var addrId)) continue;
            if (!addrById.TryGetValue(addrId, out var addrStr) || string.IsNullOrEmpty(addrStr)) continue;
            lines.Add(JsonSerializer.Serialize(new
            {
                type = "addr",
                @ref = addrStr,
                label = string.Join(" | ", group.Select(l => l.Text))
            }, jsonOpts));
        }
    }

    // xpub: one line per account-level xpub. The label is the account's purpose
    // so the consuming wallet can tell P2TR from P2WPKH at a glance.
    var accounts = await db.Accounts
        .Where(a => a.WalletId == wallet.Id && a.AccountXPub != null)
        .ToListAsync();
    foreach (var acct in accounts)
    {
        var purposeLabel = acct.Purpose switch
        {
            86 => "Kompaktor P2TR account",
            84 => "Kompaktor P2WPKH account",
            _ => $"Kompaktor account (purpose {acct.Purpose})"
        };
        lines.Add(JsonSerializer.Serialize(new
        {
            type = "xpub",
            @ref = acct.AccountXPub,
            label = purposeLabel
        }, jsonOpts));
    }

    // application/jsonl is the registered media type for JSON Lines.
    var body = string.Join("\n", lines);
    return Results.Text(body, "application/jsonl", System.Text.Encoding.UTF8);
}).WithTags("Wallet");

// BIP329 labels import: accepts JSONL text. Recognized types are applied to
// our internal storage (addr → AddressBook, tx → Transaction labels,
// output → UTXO labels). Unknown types and malformed lines are skipped
// silently rather than failing the whole batch — this matches the
// "be lenient in what you accept" spirit of the spec and keeps a partial
// import from the user's other wallet from being rejected over one odd line.
app.MapPost("/api/wallet/labels/bip329", async (WalletDbContext db, HttpContext ctx, DashboardEventBus bus) =>
{
    var wallet = await db.Wallets.FirstOrDefaultAsync();
    if (wallet is null) return Results.BadRequest("No wallet found");

    using var reader = new StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8);
    var payload = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(payload))
        return Results.BadRequest("Empty body — expected JSONL");

    int addrAdded = 0, txAdded = 0, outputAdded = 0, skipped = 0;

    // Pre-index UTXOs by outpoint so the per-line lookup stays cheap even for
    // large imports. Only needed if there are any output lines.
    Dictionary<string, int>? utxoByOutpoint = null;

    foreach (var raw in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        var line = raw.Trim();
        if (line.Length == 0) continue;
        JsonElement parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<JsonElement>(line);
            if (parsed.ValueKind != JsonValueKind.Object) { skipped++; continue; }
        }
        catch { skipped++; continue; }

        if (!parsed.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String
            || !parsed.TryGetProperty("ref", out var refEl) || refEl.ValueKind != JsonValueKind.String
            || !parsed.TryGetProperty("label", out var labelEl) || labelEl.ValueKind != JsonValueKind.String)
        {
            skipped++;
            continue;
        }

        var type = typeEl.GetString()!;
        var refVal = refEl.GetString()!.Trim();
        var label = labelEl.GetString()!.Trim();
        if (string.IsNullOrEmpty(refVal) || string.IsNullOrEmpty(label))
        {
            skipped++;
            continue;
        }

        switch (type)
        {
            case "addr":
            {
                byte[] scriptBytes;
                try { scriptBytes = BitcoinAddress.Create(refVal, network).ScriptPubKey.ToBytes(); }
                catch { skipped++; continue; }

                // Owned addresses get a LabelEntity(Address) so read-side
                // privacy logic sees them. Counterparty addresses go into
                // the address book. BIP329 makes no distinction so we
                // disambiguate ourselves at import time.
                var owned = await db.Addresses
                    .Include(a => a.Account)
                    .Where(a => a.Account.WalletId == wallet.Id)
                    .FirstOrDefaultAsync(a => a.ScriptPubKey == scriptBytes);
                if (owned is not null)
                {
                    var ownedIdStr = owned.Id.ToString();
                    var existsOwned = await db.Labels.AnyAsync(l =>
                        l.EntityType == "Address" && l.EntityId == ownedIdStr && l.Text == label);
                    if (existsOwned) { skipped++; continue; }
                    db.Labels.Add(new LabelEntity
                    {
                        EntityType = "Address",
                        EntityId = ownedIdStr,
                        Text = label
                    });
                    addrAdded++;
                    break;
                }

                var exists = await db.AddressBook.AnyAsync(a =>
                    a.WalletId == wallet.Id && a.Address == refVal);
                if (exists) { skipped++; continue; }
                db.AddressBook.Add(new AddressBookEntry
                {
                    WalletId = wallet.Id,
                    Address = refVal,
                    Label = label
                });
                addrAdded++;
                break;
            }
            case "tx":
            {
                var exists = await db.Labels.AnyAsync(l =>
                    l.EntityType == "Transaction" && l.EntityId == refVal && l.Text == label);
                if (exists) { skipped++; continue; }
                db.Labels.Add(new LabelEntity
                {
                    EntityType = "Transaction",
                    EntityId = refVal,
                    Text = label
                });
                txAdded++;
                break;
            }
            case "output":
            {
                // Lazy-build the outpoint index on first output-line we see.
                if (utxoByOutpoint is null)
                {
                    utxoByOutpoint = (await db.Utxos.ToListAsync())
                        .ToDictionary(u => $"{u.TxId}:{u.OutputIndex}", u => u.Id);
                }
                if (!utxoByOutpoint.TryGetValue(refVal, out var utxoId))
                {
                    // Orphan output — wallet hasn't synced the tx this labels.
                    // Skip rather than storing a dangling label.
                    skipped++;
                    continue;
                }
                var utxoIdStr = utxoId.ToString();
                var exists = await db.Labels.AnyAsync(l =>
                    l.EntityType == "Utxo" && l.EntityId == utxoIdStr && l.Text == label);
                if (exists) { skipped++; continue; }
                db.Labels.Add(new LabelEntity
                {
                    EntityType = "Utxo",
                    EntityId = utxoIdStr,
                    Text = label
                });
                outputAdded++;
                break;
            }
            default:
                // xpub / input / pubkey: not stored locally, ignore.
                skipped++;
                break;
        }
    }

    await db.SaveChangesAsync();
    bus.Publish("utxos");
    return Results.Ok(new
    {
        addrImported = addrAdded,
        txImported = txAdded,
        outputImported = outputAdded,
        skipped
    });
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

// Mixing profile catalog — exposes the server's canonical preset list so
// the UI and API consumers don't need to hardcode descriptions or knob
// values. Each spec shows what the preset actually does.
app.MapGet("/api/mixing/profiles", () =>
{
    return Results.Ok(new
    {
        @default = MixingProfileCatalog.Default,
        profiles = MixingProfileCatalog.All.Select(p => new
        {
            name = p.Name,
            description = p.Description,
            consolidationThreshold = p.ConsolidationThreshold,
            selfSendDelaySeconds = p.SelfSendDelaySeconds,
            interactivePaymentsEnabled = p.InteractivePaymentsEnabled
        })
    });
}).WithTags("Mixing");

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
        activeProfile = mixer.ActiveProfile,
        allowUnconfirmedCoinjoinReuse = mixer.AllowUnconfirmedCoinjoinReuse,
        mixingOutpoints = mixer.ActiveMixingOutpoints,
        lastRoundStatus = mixer.LastRoundStatus,
        lastRoundFailureReason = mixer.LastRoundFailureReason,
        lastRoundCompletedAt = mixer.LastRoundCompletedAt,
        lastSuccessfulRoundAt = mixer.LastSuccessfulRoundAt
    });
}).WithTags("Mixing");

app.MapPost("/api/mixing/start", async (MixingManager mixer, WalletDbContext db, HttpContext ctx) =>
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

        // Resolve Tor options. The request body wins; if the caller omitted
        // them, fall back to the wallet's persisted preference.
        Kompaktor.TorOptions? torOptions = null;
        if (!string.IsNullOrWhiteSpace(body.TorSocksHost))
        {
            torOptions = new Kompaktor.TorOptions
            {
                SocksHost = body.TorSocksHost,
                SocksPort = body.TorSocksPort ?? 9050
            };
        }
        else
        {
            var walletEntity = await db.Wallets.FirstOrDefaultAsync();
            if (walletEntity is { TorEnabled: true, TorSocksHost: { } savedHost } && !string.IsNullOrWhiteSpace(savedHost))
            {
                torOptions = new Kompaktor.TorOptions
                {
                    SocksHost = savedHost,
                    SocksPort = walletEntity.TorSocksPort ?? 9050
                };
            }
        }

        // Preflight: if Tor was requested, verify the daemon is actually
        // reachable before handing off to the mixer. The mixer would
        // otherwise silently fall back to clearnet on connection refusal,
        // leaking the wallet's IP to the coordinator.
        if (torOptions is not null)
        {
            var probe = await TorReachability.ProbeAsync(torOptions.SocksHost, torOptions.SocksPort, ctx.RequestAborted);
            if (!probe.Reachable)
                return Results.BadRequest(new
                {
                    error = "tor_unreachable",
                    message = $"Tor daemon unreachable at {torOptions.SocksHost}:{torOptions.SocksPort} — {probe.Error}"
                });
        }

        var result = await mixer.StartAsync(body.Passphrase, coordinatorUri, torOptions, body.AllowUnconfirmedCoinjoinReuse, body.Profile);
        return Results.Ok(new
        {
            status = result,
            coordinator = coordinatorUri.ToString(),
            torEnabled = torOptions is not null,
            activeProfile = mixer.ActiveProfile,
            allowUnconfirmedCoinjoinReuse = body.AllowUnconfirmedCoinjoinReuse
        });
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

record SendPlanRequest(string Destination, long AmountSat, long FeeRateSatPerVb = 2, string Strategy = "PrivacyFirst", string[]? SelectedOutpoints = null);
record BatchFreezeRequest(int[] UtxoIds, bool Freeze);
record LabelRequest(string Text);
record PassphraseRequest(string Passphrase);
record ChangePassphraseRequest(string CurrentPassphrase, string NewPassphrase);
record RenameWalletRequest(string Name);
record DeleteWalletRequest(string Passphrase, string Confirmation);
record SignMessageRequest(string Address, string Message, string Passphrase);
record VerifyMessageRequest(string Address, string Message, string Signature);
record RestoreRequest(string Mnemonic, string Passphrase, string? Name = null);
record CreateWalletRequest(string Passphrase, string? Name = null, int? WordCount = null);
record MixingStartRequest(string Passphrase, string? CoordinatorUrl = null, string? TorSocksHost = null, int? TorSocksPort = null, bool AllowUnconfirmedCoinjoinReuse = false, string? Profile = null);
record AddressBookRequest(string Label, string Address);
record TransactionNoteRequest(string Text);
record AddressLabelRequest(string Text);
record ExposureRequest(bool Exposed);
record SendRequest(string Destination, long AmountSat, long FeeRateSatPerVb = 2, string Strategy = "PrivacyFirst", string Passphrase = "", string[]? SelectedOutpoints = null);
record BroadcastPsbtRequest(string SignedPsbt);
record CreatePaymentRequest(string Destination, long AmountSat, bool Interactive = true, bool Urgent = false, string? Label = null, int? ExpiryMinutes = null, DateTimeOffset? ScheduledAt = null, int? MaxRetries = null);
record BatchSendRequest(string Destination, long AmountSat, bool Interactive = true, bool Urgent = false, string? Label = null, int? ExpiryMinutes = null, DateTimeOffset? ScheduledAt = null, int? MaxRetries = null);
record CreateReceiveRequest(long AmountSat, string? Label = null, int? ExpiryMinutes = null);
record WebhookCreateRequest(string Url, string? EventFilter = null);
record WebhookUpdateRequest(bool? IsActive = null, string? Url = null, string? EventFilter = null);
record VerifyBackupRequest(string Passphrase, VerifyBackupAnswer[] Answers);
record VerifyBackupAnswer(int Position, string Word);
record FeeBumpRequest(string TxId, long NewFeeRateSatPerVb);
record SweepRequest(string Destination, long FeeRateSatPerVb = 2);
record WalletSettingsUpdateRequest(
    string? MixingProfile = null,
    bool? TorEnabled = null,
    string? TorSocksHost = null,
    int? TorSocksPort = null);
record TorTestRequest(string? Host = null, int? Port = null);
record CoordinatorTestRequest(string? Url = null);

/// <summary>
/// Result of a SOCKS5 greeting probe against a Tor daemon (or anything
/// claiming to speak SOCKS5). Used both by the Settings "Test connection"
/// button and as a preflight check when starting a mixing session — we
/// refuse to start under Tor if the probe fails, preventing silent
/// clearnet fallback that would leak the wallet's IP.
/// </summary>
internal record TorProbeResult(bool Reachable, long LatencyMs, string? AuthMethod, string? Error);

internal static class TorReachability
{
    public static async Task<TorProbeResult> ProbeAsync(string host, int port, CancellationToken ct = default)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(host, port, connectCts.Token);
        }
        catch (Exception ex)
        {
            return new TorProbeResult(false, 0, null, ex.Message);
        }

        try
        {
            var stream = tcp.GetStream();
            using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ioCts.CancelAfter(TimeSpan.FromSeconds(3));

            // Offer both NoAuth (0x00) and UserPass (0x02); whichever the
            // server picks confirms SOCKS5 and tells us which stream
            // isolation path TorCircuitFactory will actually use.
            await stream.WriteAsync(new byte[] { 0x05, 0x02, 0x00, 0x02 }, ioCts.Token);

            // Use ReadExactlyAsync: NetworkStream.ReadAsync is free to
            // return <2 bytes on partial delivery. On virtualized loopback
            // under CI load we've seen the response arrive in two TCP
            // segments, tripping the "Endpoint did not speak SOCKS5"
            // short-read fallback even against a real SOCKS5 peer.
            var resp = new byte[2];
            try
            {
                await stream.ReadExactlyAsync(resp.AsMemory(), ioCts.Token);
            }
            catch (EndOfStreamException)
            {
                sw.Stop();
                return new TorProbeResult(false, sw.ElapsedMilliseconds, null, "Endpoint closed connection before sending SOCKS5 reply");
            }
            sw.Stop();

            if (resp[0] != 0x05)
                return new TorProbeResult(false, sw.ElapsedMilliseconds, null, "Endpoint did not speak SOCKS5");
            if (resp[1] == 0xFF)
                return new TorProbeResult(false, sw.ElapsedMilliseconds, null, "SOCKS5 server rejected offered auth methods");

            var auth = resp[1] switch { 0x00 => "none", 0x02 => "userpass", _ => $"0x{resp[1]:X2}" };
            return new TorProbeResult(true, sw.ElapsedMilliseconds, auth, null);
        }
        catch (Exception ex)
        {
            return new TorProbeResult(false, sw.ElapsedMilliseconds, null, ex.Message);
        }
    }
}

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
