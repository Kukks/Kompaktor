using System.Security.Cryptography;
using Kompaktor.Blockchain;
using Kompaktor.Models;
using Kompaktor.Prison;
using Kompaktor.Server;
using Kompaktor.Wallet;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.RPC;
using NBitcoin.Secp256k1;
using WabiSabi.Crypto.Randomness;

// --- Parse CLI args ---
var network = Network.RegTest;
string? electrumHost = null;
int electrumPort = 50001;
bool electrumSsl = false;
string? rpcUri = null;
string? rpcUser = null;
string? rpcPassword = null;
string walletPath = "./wallet.db";

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--network":
            network = args[++i] switch
            {
                "mainnet" or "main" => Network.Main,
                "testnet" => Network.TestNet,
                _ => Network.RegTest
            };
            break;
        case "--electrum-host": electrumHost = args[++i]; break;
        case "--electrum-port": electrumPort = int.Parse(args[++i]); break;
        case "--electrum-ssl": electrumSsl = true; break;
        case "--rpc-uri": rpcUri = args[++i]; break;
        case "--rpc-user": rpcUser = args[++i]; break;
        case "--rpc-password": rpcPassword = args[++i]; break;
        case "--wallet-path": walletPath = args[++i]; break;
    }
}

// --- Setup logging ---
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger("Sample");

// --- Create blockchain backend ---
IBlockchainBackend blockchain;
if (electrumHost is not null)
{
    logger.LogInformation("Using Electrum backend: {Host}:{Port} (SSL={Ssl})", electrumHost, electrumPort, electrumSsl);
    blockchain = new ElectrumBackend(new ElectrumOptions
    {
        Host = electrumHost,
        Port = electrumPort,
        UseSsl = electrumSsl
    });
}
else
{
    var uri = rpcUri ?? "http://localhost:53782";
    var user = rpcUser ?? "ceiwHEbqWI83";
    var pass = rpcPassword ?? "DwubwWsoo3";
    logger.LogInformation("Using Bitcoin Core RPC backend: {Uri}", uri);
    var rpc = new RPCClient($"{user}:{pass}", uri, network);
    blockchain = new BitcoinCoreBackend(rpc);
}

await blockchain.ConnectAsync();
var height = await blockchain.GetBlockHeightAsync();
logger.LogInformation("Blockchain backend connected. Height: {Height}", height);

// --- Setup wallet DB ---
var dbOptions = new DbContextOptionsBuilder<WalletDbContext>()
    .UseSqlite($"DataSource={walletPath}")
    .Options;
await using var db = new WalletDbContext(dbOptions);
await db.Database.EnsureCreatedAsync();

// --- Create or open wallet ---
Console.Write("Wallet passphrase: ");
var passphrase = Console.ReadLine() ?? "default";

KompaktorHdWallet wallet;
var existingWallet = await db.Wallets.FirstOrDefaultAsync();
if (existingWallet is not null)
{
    logger.LogInformation("Opening existing wallet: {Name}", existingWallet.Name);
    wallet = await KompaktorHdWallet.OpenAsync(db, existingWallet.Id, network, passphrase);
}
else
{
    logger.LogInformation("Creating new wallet...");
    wallet = await KompaktorHdWallet.CreateAsync(db, network, "Kompaktor Sample Wallet", passphrase);
    logger.LogInformation("Wallet created: {Id}", wallet.WalletId);
}

wallet.SetBlockchainBackend(blockchain);

// --- Sync wallet UTXOs from blockchain ---
logger.LogInformation("Syncing wallet UTXOs...");
var syncService = new WalletSyncService(db, blockchain, network);
syncService.UtxosReceived += utxos =>
    logger.LogInformation("Discovered {Count} new UTXOs", utxos.Length);
syncService.UtxosSpent += utxos =>
    logger.LogInformation("Detected {Count} spent UTXOs", utxos.Length);

await syncService.FullSyncAsync(wallet.WalletId);
var (confirmed, unconfirmed) = await syncService.GetBalanceAsync(wallet.WalletId);
logger.LogInformation("Balance: {Confirmed} sat confirmed, {Unconfirmed} sat unconfirmed",
    confirmed, unconfirmed);

// Start real-time monitoring for new transactions
await syncService.StartMonitoringAsync(wallet.WalletId);

// --- Setup CoinJoin recorder ---
var recorder = new CoinJoinRecorder(db, wallet.WalletId);

// --- Start coordinator ---
var coordinatorOptions = new KompaktorCoordinatorOptions();
var prison = new KompaktorPrison();
var signingKey = ECPrivKey.Create(RandomNumberGenerator.GetBytes(32));

var roundManager = new KompaktorRoundManager(
    network, blockchain, SecureRandom.Instance, loggerFactory,
    coordinatorOptions, prison, signingKey);

var roundId = await roundManager.CreateRound();
logger.LogInformation("Round created: {RoundId}", roundId);

// --- Get coins ---
var coins = await wallet.GetCoins();
logger.LogInformation("Wallet has {Count} confirmed coins, total {Amount} BTC",
    coins.Length, coins.Sum(c => c.Amount.ToUnit(MoneyUnit.BTC)));

if (coins.Length == 0)
{
    logger.LogWarning("No confirmed coins in wallet. Fund the wallet first, then re-run.");

    // Show a receive address using the new fresh address API
    var receiveScript = await wallet.GetFreshAddressAsync();
    var receiveAddr = receiveScript.GetDestinationAddress(network);
    logger.LogInformation("Send funds to: {Address}", receiveAddr);
}
else
{
    logger.LogInformation("Ready for coinjoin with {Count} coins", coins.Length);

    // Example: when integrating with KompaktorService, wire the recorder to RoundCompleted:
    // service.RoundCompleted += async result =>
    // {
    //     if (result.Success && result.Transaction is not null)
    //     {
    //         await recorder.RecordRoundAsync(
    //             result.RoundId, result.Transaction,
    //             result.OurInputOutpoints!, result.OurOutputScripts!,
    //             result.TotalParticipantInputs);
    //         logger.LogInformation("Recorded coinjoin round {RoundId}", result.RoundId);
    //     }
    //     else if (!result.Success && result.OurInputOutpoints is not null)
    //     {
    //         await recorder.RecordFailedRoundAsync(result.RoundId, result.OurInputOutpoints);
    //     }
    // };
}

// --- Cleanup ---
await syncService.DisposeAsync();
wallet.Close();
await blockchain.DisposeAsync();
logger.LogInformation("Done.");
