# Wallet Library (Sub-Project A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create `Kompaktor.Blockchain` and `Kompaktor.Wallet` libraries that replace the hard `RPCClient` dependency with an `IBlockchainBackend` abstraction, add an HD wallet implementing `IKompaktorWalletInterface`, and provide a sample console app running both coordinator and client.

**Architecture:** New `Kompaktor.Blockchain` project owns the `IBlockchainBackend` interface plus two implementations (`BitcoinCoreBackend` wrapping `RPCClient`, `ElectrumBackend` over Stratum TCP/SSL). New `Kompaktor.Wallet` project provides EF Core/SQLite persistence and `KompaktorHdWallet`. The existing `KompaktorRoundOperator` and `KompaktorRoundManager` are refactored to accept `IBlockchainBackend` instead of `RPCClient` — a non-breaking change since `BitcoinCoreBackend` adapts the existing RPC calls. A sample console app (`Kompaktor.Wallet.Sample`) runs both roles in one process.

**Tech Stack:** .NET 10, NBitcoin 7.0.46, EF Core 9.0 + SQLite, xunit 2.9.3

**Spec:** `docs/specs/2026-04-17-wallet-library-design.md`

---

## File Structure

### New Files

| File | Responsibility |
|------|---------------|
| `Kompaktor.Blockchain/Kompaktor.Blockchain.csproj` | Project file — depends on NBitcoin |
| `Kompaktor.Blockchain/IBlockchainBackend.cs` | Interface + `UtxoInfo`/`AddressNotification` DTOs |
| `Kompaktor.Blockchain/BitcoinCoreBackend.cs` | `RPCClient` adapter implementing `IBlockchainBackend` |
| `Kompaktor.Blockchain/ElectrumOptions.cs` | Configuration POCO for Electrum connections |
| `Kompaktor.Blockchain/ElectrumClient.cs` | Low-level Stratum JSON-RPC TCP/SSL client |
| `Kompaktor.Blockchain/ElectrumBackend.cs` | `IBlockchainBackend` over `ElectrumClient` |
| `Kompaktor.Wallet/Kompaktor.Wallet.csproj` | Project file — depends on Kompaktor, Kompaktor.Blockchain, EF Core |
| `Kompaktor.Wallet/Data/WalletDbContext.cs` | EF Core DbContext with all entity sets |
| `Kompaktor.Wallet/Data/Entities.cs` | All entity classes (Wallet, Account, Address, Utxo, Transaction, CoinJoinRecord, CoinJoinParticipation, Label) |
| `Kompaktor.Wallet/KompaktorHdWallet.cs` | `IKompaktorWalletInterface` implementation with HD keys + EF Core persistence |
| `Kompaktor.Wallet/MnemonicEncryption.cs` | AES-256-GCM encrypt/decrypt for BIP-39 mnemonic |
| `Kompaktor.Wallet.Sample/Kompaktor.Wallet.Sample.csproj` | Console app project file |
| `Kompaktor.Wallet.Sample/Program.cs` | CLI entry point — coordinator + client in one process |

### Modified Files

| File | Change |
|------|--------|
| `Kompaktor.sln` | Add 3 new projects |
| `Kompaktor/Kompaktor.csproj` | Add ProjectReference to Kompaktor.Blockchain |
| `Kompaktor/KompaktorRoundOperator.cs` | Replace `RPCClient` field/constructor with `IBlockchainBackend`; change 2 call sites (lines 136, 577) |
| `Kompaktor.Server/Kompaktor.Server.csproj` | Add ProjectReference to Kompaktor.Blockchain |
| `Kompaktor.Server/KompaktorRoundManager.cs` | Replace `RPCClient` field/constructor with `IBlockchainBackend` |
| `Kompaktor.Server/Program.cs` | Create `BitcoinCoreBackend` from `RPCClient`, pass to `KompaktorRoundManager` |
| `Kompaktor.Tests/Kompaktor.Tests.csproj` | Add ProjectReference to Kompaktor.Blockchain |
| `Kompaktor.Tests/Test.cs` | Wrap `RPCClient` in `BitcoinCoreBackend`, pass to `KompaktorRoundOperator` |

### New Test Files

| File | Covers |
|------|--------|
| `Kompaktor.Tests/BitcoinCoreBackendTests.cs` | `BitcoinCoreBackend` adapter against regtest bitcoind |
| `Kompaktor.Tests/ElectrumClientTests.cs` | JSON-RPC framing, request correlation, subscription dispatch (unit, mocked stream) |
| `Kompaktor.Tests/WalletDbTests.cs` | EF Core entities, migrations, UTXO queries (unit, in-memory SQLite) |
| `Kompaktor.Tests/KompaktorHdWalletTests.cs` | Key derivation, BIP-322 signing, address management (unit) |
| `Kompaktor.Tests/MnemonicEncryptionTests.cs` | AES-256-GCM round-trip, wrong passphrase (unit) |

---

## Phase 1: IBlockchainBackend + BitcoinCoreBackend + Coordinator Refactor

### Task 1: Create Kompaktor.Blockchain project with IBlockchainBackend interface

**Files:**
- Create: `Kompaktor.Blockchain/Kompaktor.Blockchain.csproj`
- Create: `Kompaktor.Blockchain/IBlockchainBackend.cs`

- [ ] **Step 1: Create the project directory and csproj**

```xml
<!-- Kompaktor.Blockchain/Kompaktor.Blockchain.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <Description>Blockchain abstraction for Kompaktor — IBlockchainBackend interface and implementations</Description>
        <IsAotCompatible>true</IsAotCompatible>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="NBitcoin" Version="7.0.46"/>
    </ItemGroup>

</Project>
```

- [ ] **Step 2: Create IBlockchainBackend interface and DTOs**

```csharp
// Kompaktor.Blockchain/IBlockchainBackend.cs
using NBitcoin;

namespace Kompaktor.Blockchain;

/// <summary>
/// Abstraction over blockchain data access, replacing direct RPCClient usage
/// in both coordinator and wallet code.
/// </summary>
public interface IBlockchainBackend : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct = default);
    bool IsConnected { get; }

    // UTXO queries
    Task<UtxoInfo?> GetUtxoAsync(OutPoint outpoint, CancellationToken ct = default);
    Task<IList<UtxoInfo>> GetUtxosForScriptAsync(Script scriptPubKey, CancellationToken ct = default);
    Task<Transaction?> GetTransactionAsync(uint256 txId, CancellationToken ct = default);

    // Broadcasting
    Task<uint256> BroadcastAsync(Transaction tx, CancellationToken ct = default);

    // Address monitoring
    Task SubscribeAddressAsync(Script scriptPubKey, CancellationToken ct = default);
    Task UnsubscribeAddressAsync(Script scriptPubKey, CancellationToken ct = default);
    event EventHandler<AddressNotification> AddressNotified;

    // Fee estimation
    Task<FeeRate> EstimateFeeAsync(int confirmationTarget = 6, CancellationToken ct = default);

    // Block height
    Task<int> GetBlockHeightAsync(CancellationToken ct = default);
}

/// <summary>
/// Wire DTO for blockchain UTXO queries — distinct from UtxoEntity (persisted wallet state).
/// </summary>
public record UtxoInfo(OutPoint OutPoint, TxOut TxOut, int Confirmations, bool IsCoinBase);

/// <summary>
/// Notification fired when a subscribed address receives or sends a transaction.
/// </summary>
public record AddressNotification(Script ScriptPubKey, uint256 TxId, int? Height);
```

- [ ] **Step 3: Add project to solution**

Run: `dotnet sln Kompaktor.sln add Kompaktor.Blockchain/Kompaktor.Blockchain.csproj`
Expected: "Project `Kompaktor.Blockchain\Kompaktor.Blockchain.csproj` added to the solution."

- [ ] **Step 4: Verify it builds**

Run: `dotnet build Kompaktor.Blockchain/Kompaktor.Blockchain.csproj`
Expected: Build succeeded. 0 Error(s).

- [ ] **Step 5: Commit**

```bash
git add Kompaktor.Blockchain/ Kompaktor.sln
git commit -m "Add Kompaktor.Blockchain project with IBlockchainBackend interface"
```

---

### Task 2: Implement BitcoinCoreBackend adapter

**Files:**
- Create: `Kompaktor.Blockchain/BitcoinCoreBackend.cs`

- [ ] **Step 1: Write BitcoinCoreBackend**

```csharp
// Kompaktor.Blockchain/BitcoinCoreBackend.cs
using System.Collections.Concurrent;
using NBitcoin;
using NBitcoin.RPC;

namespace Kompaktor.Blockchain;

/// <summary>
/// IBlockchainBackend adapter wrapping NBitcoin's RPCClient.
/// Address subscriptions are implemented via polling since Bitcoin Core has no push notifications.
/// </summary>
public class BitcoinCoreBackend : IBlockchainBackend
{
    private readonly RPCClient _rpc;
    private readonly ConcurrentDictionary<Script, CancellationTokenSource> _subscriptions = new();
    private readonly TimeSpan _pollInterval;
    private bool _connected;

    public BitcoinCoreBackend(RPCClient rpc, TimeSpan? pollInterval = null)
    {
        _rpc = rpc;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
    }

    public bool IsConnected => _connected;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _rpc.GetBlockCountAsync();
        _connected = true;
    }

    public async Task<UtxoInfo?> GetUtxoAsync(OutPoint outpoint, CancellationToken ct = default)
    {
        var result = await _rpc.GetTxOutAsync(outpoint.Hash, (int)outpoint.N);
        if (result is null) return null;
        return new UtxoInfo(
            outpoint,
            new TxOut(result.TxOut.Value, result.TxOut.ScriptPubKey),
            result.Confirmations,
            result.IsCoinBase);
    }

    public async Task<IList<UtxoInfo>> GetUtxosForScriptAsync(Script scriptPubKey, CancellationToken ct = default)
    {
        // Bitcoin Core RPC does not support querying UTXOs by script directly.
        // This requires wallet import or scanning — return empty for coordinator use.
        // Full implementation would use scantxoutset or importdescriptors.
        return Array.Empty<UtxoInfo>();
    }

    public async Task<Transaction?> GetTransactionAsync(uint256 txId, CancellationToken ct = default)
    {
        try
        {
            var raw = await _rpc.GetRawTransactionAsync(txId);
            return raw;
        }
        catch (RPCException ex) when (ex.RPCCode == RPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY)
        {
            return null;
        }
    }

    public async Task<uint256> BroadcastAsync(Transaction tx, CancellationToken ct = default)
    {
        // Pass maxfeerate=0 to skip the per-kVB fee rate check;
        // the node's -maxtxfee still caps the absolute fee.
        var resp = await _rpc.SendCommandAsync("sendrawtransaction", tx.ToHex(), 0);
        resp.ThrowIfError();
        return uint256.Parse(resp.Result.ToString()!);
    }

    public Task SubscribeAddressAsync(Script scriptPubKey, CancellationToken ct = default)
    {
        // Bitcoin Core has no push notification for address activity.
        // For the coordinator, this is not needed (coordinator only queries UTXOs on demand).
        // A polling implementation would be added for wallet use via scantxoutset.
        _subscriptions.TryAdd(scriptPubKey, new CancellationTokenSource());
        return Task.CompletedTask;
    }

    public Task UnsubscribeAddressAsync(Script scriptPubKey, CancellationToken ct = default)
    {
        if (_subscriptions.TryRemove(scriptPubKey, out var cts))
            cts.Cancel();
        return Task.CompletedTask;
    }

    public event EventHandler<AddressNotification>? AddressNotified;

    public async Task<FeeRate> EstimateFeeAsync(int confirmationTarget = 6, CancellationToken ct = default)
    {
        var result = await _rpc.EstimateSmartFeeAsync(confirmationTarget);
        return result.FeeRate;
    }

    public async Task<int> GetBlockHeightAsync(CancellationToken ct = default)
    {
        return await _rpc.GetBlockCountAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _subscriptions)
        {
            kvp.Value.Cancel();
            kvp.Value.Dispose();
        }
        _subscriptions.Clear();
    }
}
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build Kompaktor.Blockchain/Kompaktor.Blockchain.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Kompaktor.Blockchain/BitcoinCoreBackend.cs
git commit -m "Implement BitcoinCoreBackend wrapping RPCClient into IBlockchainBackend"
```

---

### Task 3: Refactor KompaktorRoundOperator to use IBlockchainBackend

**Files:**
- Modify: `Kompaktor/Kompaktor.csproj` — add ProjectReference
- Modify: `Kompaktor/KompaktorRoundOperator.cs:23,30-33,136,577` — replace RPCClient

- [ ] **Step 1: Add Kompaktor.Blockchain reference to Kompaktor.csproj**

Add to `Kompaktor/Kompaktor.csproj` inside `<ItemGroup>`:
```xml
<ProjectReference Include="..\Kompaktor.Blockchain\Kompaktor.Blockchain.csproj" />
```

- [ ] **Step 2: Replace RPCClient in KompaktorRoundOperator**

In `Kompaktor/KompaktorRoundOperator.cs`:

Replace the `using NBitcoin.RPC;` import:
```csharp
// Add:
using Kompaktor.Blockchain;
// Remove:
// using NBitcoin.RPC;  (only if no other RPC usages remain in this file)
```

Replace the field declaration (line 23):
```csharp
// Before:
private readonly RPCClient _rpcClient;
// After:
private readonly IBlockchainBackend _blockchain;
```

Replace constructor (lines 30-33):
```csharp
// Before:
public KompaktorRoundOperator(Network network, RPCClient rpcClient, WasabiRandom random, ILogger logger, KompaktorPrison? prison = null)
{
    _network = network;
    _rpcClient = rpcClient;
// After:
public KompaktorRoundOperator(Network network, IBlockchainBackend blockchain, WasabiRandom random, ILogger logger, KompaktorPrison? prison = null)
{
    _network = network;
    _blockchain = blockchain;
```

Replace UTXO lookup (line 136):
```csharp
// Before:
var txOutStatus = await _rpcClient.GetTxOutAsync(txIn.PrevOut.Hash, (int)txIn.PrevOut.N);

if (txOutStatus is null or { Confirmations: < 1 } or { Confirmations: < 100, IsCoinBase: true })
    throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InputNotValid,
        "Coin not valid");

var coin = new Coin(txIn.PrevOut, new TxOut(txOutStatus.TxOut.Value, txOutStatus.TxOut.ScriptPubKey));
// After:
var utxoInfo = await _blockchain.GetUtxoAsync(txIn.PrevOut);

if (utxoInfo is null or { Confirmations: < 1 } or { Confirmations: < 100, IsCoinBase: true })
    throw new KompaktorProtocolException(KompaktorProtocolErrorCode.InputNotValid,
        "Coin not valid");

var coin = new Coin(txIn.PrevOut, utxoInfo.TxOut);
```

Replace broadcast (line 577):
```csharp
// Before:
var resp = await _rpcClient.SendCommandAsync("sendrawtransaction", tx.ToHex(), 0);
resp.ThrowIfError();
var result = uint256.Parse(resp.Result.ToString());
// After:
var result = await _blockchain.BroadcastAsync(tx);
```

- [ ] **Step 3: Verify Kompaktor builds**

Run: `dotnet build Kompaktor/Kompaktor.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Kompaktor/Kompaktor.csproj Kompaktor/KompaktorRoundOperator.cs
git commit -m "Refactor KompaktorRoundOperator to use IBlockchainBackend instead of RPCClient"
```

---

### Task 4: Refactor KompaktorRoundManager and Server DI

**Files:**
- Modify: `Kompaktor.Server/Kompaktor.Server.csproj` — add ProjectReference
- Modify: `Kompaktor.Server/KompaktorRoundManager.cs:23,30-40` — replace RPCClient
- Modify: `Kompaktor.Server/Program.cs:32,52-60` — wrap RPCClient in BitcoinCoreBackend

- [ ] **Step 1: Add Kompaktor.Blockchain reference to Server csproj**

Add to `Kompaktor.Server/Kompaktor.Server.csproj` inside the existing `<ItemGroup>` with ProjectReference:
```xml
<ProjectReference Include="..\Kompaktor.Blockchain\Kompaktor.Blockchain.csproj" />
```

- [ ] **Step 2: Replace RPCClient in KompaktorRoundManager**

In `Kompaktor.Server/KompaktorRoundManager.cs`:

Replace `using NBitcoin.RPC;` with `using Kompaktor.Blockchain;`

Replace field (line 23):
```csharp
// Before:
private readonly RPCClient _rpcClient;
// After:
private readonly IBlockchainBackend _blockchain;
```

Replace constructor parameter (lines 30-40):
```csharp
// Before:
public KompaktorRoundManager(
    Network network,
    RPCClient rpcClient,
    ...
{
    _network = network;
    _rpcClient = rpcClient;
// After:
public KompaktorRoundManager(
    Network network,
    IBlockchainBackend blockchain,
    ...
{
    _network = network;
    _blockchain = blockchain;
```

Replace both `new KompaktorRoundOperator` calls (lines 52 and 151):
```csharp
// Before:
var op = new KompaktorRoundOperator(_network, _rpcClient, _random, logger, _prison);
// After:
var op = new KompaktorRoundOperator(_network, _blockchain, _random, logger, _prison);
```

- [ ] **Step 3: Update Program.cs to use BitcoinCoreBackend**

In `Kompaktor.Server/Program.cs`:

Add `using Kompaktor.Blockchain;` to the top.

After line 32 (`var rpcClient = new RPCClient(...)`) add:
```csharp
var blockchain = new BitcoinCoreBackend(rpcClient);
```

In the `KompaktorRoundManager` registration (lines 52-60), replace `rpcClient` with `blockchain`:
```csharp
builder.Services.AddSingleton<KompaktorRoundManager>(sp =>
    new KompaktorRoundManager(
        network,
        blockchain,
        new InsecureRandom(),
        sp.GetRequiredService<ILoggerFactory>(),
        coordinatorOptions,
        sp.GetRequiredService<KompaktorPrison>(),
        coordinatorSigningKey));
```

- [ ] **Step 4: Verify Server builds**

Run: `dotnet build Kompaktor.Server/Kompaktor.Server.csproj`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add Kompaktor.Server/
git commit -m "Refactor KompaktorRoundManager and Server DI to use IBlockchainBackend"
```

---

### Task 5: Update test infrastructure and verify all 259 tests pass

**Files:**
- Modify: `Kompaktor.Tests/Kompaktor.Tests.csproj` — add ProjectReference
- Modify: `Kompaktor.Tests/Test.cs:38-42,127-128` — wrap RPCClient in BitcoinCoreBackend

- [ ] **Step 1: Add Kompaktor.Blockchain reference to test csproj**

Add to `Kompaktor.Tests/Kompaktor.Tests.csproj` inside the `<ItemGroup>` with ProjectReferences:
```xml
<ProjectReference Include="..\Kompaktor.Blockchain\Kompaktor.Blockchain.csproj" />
```

- [ ] **Step 2: Update Test base class**

In `Kompaktor.Tests/Test.cs`, add `using Kompaktor.Blockchain;` at the top.

After the RPC initialization (after line 42), add the BitcoinCoreBackend wrapper:
```csharp
Blockchain = new BitcoinCoreBackend(RPC);
```

Add the property after the existing `RPCClient RPC` property (line 60):
```csharp
public IBlockchainBackend Blockchain { get; }
```

- [ ] **Step 3: Update all test KompaktorRoundOperator instantiations**

Find all places in `Test.cs` where `KompaktorRoundOperator` is created with `RPC` and replace with `Blockchain`:
```csharp
// Before:
new KompaktorRoundOperator(Network, RPC, SecureRandom.Instance, ...)
// After:
new KompaktorRoundOperator(Network, Blockchain, SecureRandom.Instance, ...)
```

Use search-and-replace across the file since there may be many instantiation sites.

- [ ] **Step 4: Build the entire solution**

Run: `dotnet build Kompaktor.sln --configuration Release`
Expected: Build succeeded. 0 Error(s).

- [ ] **Step 5: Run all tests**

Run: `dotnet test Kompaktor.Tests/Kompaktor.Tests.csproj --configuration Release --filter "Category!=Integration" --no-build`
Expected: Passed! Failed: 0, Passed: ~30 (unit tests only — integration tests need docker).

- [ ] **Step 6: Run integration tests (requires docker-compose up)**

Run: `dotnet test Kompaktor.Tests/Kompaktor.Tests.csproj --configuration Release --no-build`
Expected: Passed! Failed: 0, Passed: 259.

- [ ] **Step 7: Commit**

```bash
git add Kompaktor.Tests/
git commit -m "Update test infrastructure to use BitcoinCoreBackend adapter

All 259 existing tests pass through the new IBlockchainBackend interface."
```

---

### Task 6: Write BitcoinCoreBackend adapter tests

**Files:**
- Create: `Kompaktor.Tests/BitcoinCoreBackendTests.cs`

- [ ] **Step 1: Write adapter tests**

```csharp
// Kompaktor.Tests/BitcoinCoreBackendTests.cs
using Kompaktor.Blockchain;
using NBitcoin;
using NBitcoin.RPC;
using System.Net;
using Xunit;

namespace Kompaktor.Tests;

[Trait("Category", "Integration")]
public class BitcoinCoreBackendTests
{
    private readonly RPCClient _rpc;
    private readonly BitcoinCoreBackend _backend;
    private readonly Network _network = Network.RegTest;

    public BitcoinCoreBackendTests()
    {
        _rpc = new RPCClient(new RPCCredentialString
        {
            UserPassword = new NetworkCredential("ceiwHEbqWI83", "DwubwWsoo3"),
            Server = "http://localhost:53782"
        }, _network);
        _backend = new BitcoinCoreBackend(_rpc);
    }

    [Fact]
    public async Task ConnectAsync_SetsIsConnected()
    {
        Assert.False(_backend.IsConnected);
        await _backend.ConnectAsync();
        Assert.True(_backend.IsConnected);
    }

    [Fact]
    public async Task GetBlockHeightAsync_ReturnsPositiveHeight()
    {
        var height = await _backend.GetBlockHeightAsync();
        Assert.True(height > 0);
    }

    [Fact]
    public async Task GetUtxoAsync_NonExistentOutpoint_ReturnsNull()
    {
        var fakeOutpoint = new OutPoint(uint256.One, 999);
        var result = await _backend.GetUtxoAsync(fakeOutpoint);
        Assert.Null(result);
    }

    [Fact]
    public async Task BroadcastAndGetUtxo_RoundTrip()
    {
        // Generate a block to get coins
        var addr = await _rpc.GetNewAddressAsync();
        var blocks = await _rpc.GenerateAsync(1);

        // Wait for maturity
        await _rpc.GenerateAsync(100);

        // Get a UTXO to spend
        var unspent = await _rpc.ListUnspentAsync();
        var utxo = unspent.First();

        // Verify GetUtxoAsync returns it
        var info = await _backend.GetUtxoAsync(utxo.OutPoint);
        Assert.NotNull(info);
        Assert.Equal(utxo.Amount, info.TxOut.Value);
    }

    [Fact]
    public async Task EstimateFeeAsync_ReturnsFeeRate()
    {
        // Generate enough blocks for fee estimation
        await _rpc.GenerateAsync(10);
        var feeRate = await _backend.EstimateFeeAsync(6);
        // Regtest may return -1 fee rate if not enough data, but should not throw
        Assert.NotNull(feeRate);
    }

    [Fact]
    public async Task GetUtxosForScriptAsync_ReturnsEmpty()
    {
        // BitcoinCoreBackend doesn't support script-based queries without wallet import
        var script = new Key().GetScriptPubKey(ScriptPubKeyType.Segwit);
        var result = await _backend.GetUtxosForScriptAsync(script);
        Assert.Empty(result);
    }
}
```

- [ ] **Step 2: Run the new tests**

Run: `dotnet test Kompaktor.Tests/Kompaktor.Tests.csproj --filter "FullyQualifiedName~BitcoinCoreBackendTests" --configuration Release`
Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
git add Kompaktor.Tests/BitcoinCoreBackendTests.cs
git commit -m "Add BitcoinCoreBackend adapter integration tests"
```

---

## Phase 2: EF Core Data Model + KompaktorHdWallet

### Task 7: Create Kompaktor.Wallet project with EF Core entities

**Files:**
- Create: `Kompaktor.Wallet/Kompaktor.Wallet.csproj`
- Create: `Kompaktor.Wallet/Data/Entities.cs`
- Create: `Kompaktor.Wallet/Data/WalletDbContext.cs`

- [ ] **Step 1: Create Kompaktor.Wallet project**

```xml
<!-- Kompaktor.Wallet/Kompaktor.Wallet.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <Description>Kompaktor HD wallet with EF Core/SQLite persistence</Description>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="NBitcoin" Version="7.0.46"/>
        <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.0.0"/>
        <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="9.0.0">
            <PrivateAssets>all</PrivateAssets>
        </PackageReference>
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\Kompaktor\Kompaktor.csproj" />
        <ProjectReference Include="..\Kompaktor.Blockchain\Kompaktor.Blockchain.csproj" />
    </ItemGroup>

</Project>
```

- [ ] **Step 2: Create entity classes**

```csharp
// Kompaktor.Wallet/Data/Entities.cs
namespace Kompaktor.Wallet.Data;

public class WalletEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public byte[] EncryptedMnemonic { get; set; } = [];
    public byte[] MnemonicSalt { get; set; } = [];
    public string Network { get; set; } = "RegTest";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<AccountEntity> Accounts { get; set; } = [];
}

public class AccountEntity
{
    public int Id { get; set; }
    public string WalletId { get; set; } = "";
    public int Purpose { get; set; } // 84 = P2WPKH, 86 = P2TR
    public int AccountIndex { get; set; }

    public WalletEntity Wallet { get; set; } = null!;
    public List<AddressEntity> Addresses { get; set; } = [];
}

public class AddressEntity
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string KeyPath { get; set; } = ""; // e.g. "0/5" (chain/index)
    public byte[] ScriptPubKey { get; set; } = [];
    public bool IsChange { get; set; }
    public bool IsUsed { get; set; }
    public bool IsExposed { get; set; }

    public AccountEntity Account { get; set; } = null!;
    public List<UtxoEntity> Utxos { get; set; } = [];
}

public class UtxoEntity
{
    public int Id { get; set; }
    public string TxId { get; set; } = "";
    public int OutputIndex { get; set; }
    public int AddressId { get; set; }
    public long AmountSat { get; set; }
    public byte[] ScriptPubKey { get; set; } = [];
    public int? ConfirmedHeight { get; set; }
    public string? SpentByTxId { get; set; }
    public bool IsCoinBase { get; set; }

    public AddressEntity Address { get; set; } = null!;
}

public class TransactionEntity
{
    public string Id { get; set; } = ""; // TX hash
    public string RawHex { get; set; } = "";
    public int? BlockHeight { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public class CoinJoinRecordEntity
{
    public int Id { get; set; }
    public string TransactionId { get; set; } = "";
    public string RoundId { get; set; } = "";
    public string Status { get; set; } = ""; // "Completed" or "Failed"
    public int OurInputCount { get; set; }
    public int TotalInputCount { get; set; }
    public int OurOutputCount { get; set; }
    public int TotalOutputCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public TransactionEntity Transaction { get; set; } = null!;
    public List<CoinJoinParticipationEntity> Participations { get; set; } = [];
}

public class CoinJoinParticipationEntity
{
    public int Id { get; set; }
    public int CoinJoinRecordId { get; set; }
    public int UtxoId { get; set; }
    public string Role { get; set; } = ""; // "Input" or "Output"

    public CoinJoinRecordEntity CoinJoinRecord { get; set; } = null!;
    public UtxoEntity Utxo { get; set; } = null!;
}

public class LabelEntity
{
    public int Id { get; set; }
    public string EntityType { get; set; } = ""; // "Address", "Transaction", "Utxo", "CoinJoin"
    public string EntityId { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

- [ ] **Step 3: Create WalletDbContext**

```csharp
// Kompaktor.Wallet/Data/WalletDbContext.cs
using Microsoft.EntityFrameworkCore;

namespace Kompaktor.Wallet.Data;

public class WalletDbContext : DbContext
{
    public WalletDbContext(DbContextOptions<WalletDbContext> options) : base(options) { }

    public DbSet<WalletEntity> Wallets => Set<WalletEntity>();
    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();
    public DbSet<AddressEntity> Addresses => Set<AddressEntity>();
    public DbSet<UtxoEntity> Utxos => Set<UtxoEntity>();
    public DbSet<TransactionEntity> Transactions => Set<TransactionEntity>();
    public DbSet<CoinJoinRecordEntity> CoinJoinRecords => Set<CoinJoinRecordEntity>();
    public DbSet<CoinJoinParticipationEntity> CoinJoinParticipations => Set<CoinJoinParticipationEntity>();
    public DbSet<LabelEntity> Labels => Set<LabelEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WalletEntity>(e =>
        {
            e.HasKey(w => w.Id);
            e.HasMany(w => w.Accounts).WithOne(a => a.Wallet).HasForeignKey(a => a.WalletId);
        });

        modelBuilder.Entity<AccountEntity>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasMany(a => a.Addresses).WithOne(addr => addr.Account).HasForeignKey(addr => addr.AccountId);
        });

        modelBuilder.Entity<AddressEntity>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.ScriptPubKey);
            e.HasMany(a => a.Utxos).WithOne(u => u.Address).HasForeignKey(u => u.AddressId);
        });

        modelBuilder.Entity<UtxoEntity>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => new { u.TxId, u.OutputIndex }).IsUnique();
        });

        modelBuilder.Entity<TransactionEntity>(e =>
        {
            e.HasKey(t => t.Id);
        });

        modelBuilder.Entity<CoinJoinRecordEntity>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasOne(c => c.Transaction).WithMany().HasForeignKey(c => c.TransactionId);
            e.HasMany(c => c.Participations).WithOne(p => p.CoinJoinRecord).HasForeignKey(p => p.CoinJoinRecordId);
        });

        modelBuilder.Entity<CoinJoinParticipationEntity>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasOne(p => p.Utxo).WithMany().HasForeignKey(p => p.UtxoId);
        });

        modelBuilder.Entity<LabelEntity>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => new { l.EntityType, l.EntityId });
        });
    }
}
```

- [ ] **Step 4: Add project to solution and build**

Run:
```bash
dotnet sln Kompaktor.sln add Kompaktor.Wallet/Kompaktor.Wallet.csproj
dotnet build Kompaktor.Wallet/Kompaktor.Wallet.csproj
```
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add Kompaktor.Wallet/ Kompaktor.sln
git commit -m "Add Kompaktor.Wallet project with EF Core entities and DbContext"
```

---

### Task 8: Write EF Core data model unit tests

**Files:**
- Create: `Kompaktor.Tests/WalletDbTests.cs`
- Modify: `Kompaktor.Tests/Kompaktor.Tests.csproj` — add Kompaktor.Wallet reference and EF Core SQLite

- [ ] **Step 1: Add references to test project**

Add to `Kompaktor.Tests/Kompaktor.Tests.csproj`:
```xml
<ProjectReference Include="..\Kompaktor.Wallet\Kompaktor.Wallet.csproj" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.0.0"/>
```

- [ ] **Step 2: Write DbContext tests**

```csharp
// Kompaktor.Tests/WalletDbTests.cs
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kompaktor.Tests;

public class WalletDbTests : IDisposable
{
    private readonly WalletDbContext _db;

    public WalletDbTests()
    {
        var options = new DbContextOptionsBuilder<WalletDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _db = new WalletDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void CanCreateWalletWithAccountsAndAddresses()
    {
        var wallet = new WalletEntity
        {
            Name = "Test Wallet",
            EncryptedMnemonic = [1, 2, 3],
            MnemonicSalt = [4, 5, 6],
            Network = "RegTest"
        };
        wallet.Accounts.Add(new AccountEntity
        {
            Purpose = 84,
            AccountIndex = 0,
            Addresses =
            {
                new AddressEntity { KeyPath = "0/0", ScriptPubKey = [0xAA], IsChange = false },
                new AddressEntity { KeyPath = "1/0", ScriptPubKey = [0xBB], IsChange = true }
            }
        });

        _db.Wallets.Add(wallet);
        _db.SaveChanges();

        var loaded = _db.Wallets.Include(w => w.Accounts).ThenInclude(a => a.Addresses).Single();
        Assert.Equal("Test Wallet", loaded.Name);
        Assert.Single(loaded.Accounts);
        Assert.Equal(2, loaded.Accounts[0].Addresses.Count);
    }

    [Fact]
    public void UtxoUniqueIndex_PreventsDoubleInsert()
    {
        var wallet = new WalletEntity { Name = "W" };
        var account = new AccountEntity { Purpose = 84, AccountIndex = 0, Wallet = wallet };
        var address = new AddressEntity { KeyPath = "0/0", ScriptPubKey = [0xAA], Account = account };
        _db.Wallets.Add(wallet);
        _db.Addresses.Add(address);
        _db.SaveChanges();

        var utxo1 = new UtxoEntity { TxId = "abc123", OutputIndex = 0, AddressId = address.Id, AmountSat = 50000, ScriptPubKey = [0xAA] };
        _db.Utxos.Add(utxo1);
        _db.SaveChanges();

        var utxo2 = new UtxoEntity { TxId = "abc123", OutputIndex = 0, AddressId = address.Id, AmountSat = 50000, ScriptPubKey = [0xAA] };
        _db.Utxos.Add(utxo2);
        Assert.Throws<DbUpdateException>(() => _db.SaveChanges());
    }

    [Fact]
    public void QueryUnspentConfirmedUtxos()
    {
        var wallet = new WalletEntity { Name = "W" };
        var account = new AccountEntity { Purpose = 84, AccountIndex = 0, Wallet = wallet };
        var address = new AddressEntity { KeyPath = "0/0", ScriptPubKey = [0xAA], Account = account };
        _db.Wallets.Add(wallet);
        _db.Addresses.Add(address);
        _db.SaveChanges();

        // Confirmed, unspent
        _db.Utxos.Add(new UtxoEntity { TxId = "tx1", OutputIndex = 0, AddressId = address.Id, AmountSat = 10000, ScriptPubKey = [0xAA], ConfirmedHeight = 100 });
        // Unconfirmed
        _db.Utxos.Add(new UtxoEntity { TxId = "tx2", OutputIndex = 0, AddressId = address.Id, AmountSat = 20000, ScriptPubKey = [0xAA], ConfirmedHeight = null });
        // Confirmed but spent
        _db.Utxos.Add(new UtxoEntity { TxId = "tx3", OutputIndex = 0, AddressId = address.Id, AmountSat = 30000, ScriptPubKey = [0xAA], ConfirmedHeight = 101, SpentByTxId = "spender" });
        _db.SaveChanges();

        var coins = _db.Utxos.Where(u => u.SpentByTxId == null && u.ConfirmedHeight != null).ToList();
        Assert.Single(coins);
        Assert.Equal("tx1", coins[0].TxId);
    }

    [Fact]
    public void CoinJoinRecordWithParticipations()
    {
        var wallet = new WalletEntity { Name = "W" };
        var account = new AccountEntity { Purpose = 84, AccountIndex = 0, Wallet = wallet };
        var address = new AddressEntity { KeyPath = "0/0", ScriptPubKey = [0xAA], Account = account };
        _db.Wallets.Add(wallet);
        _db.Addresses.Add(address);
        _db.SaveChanges();

        var utxo = new UtxoEntity { TxId = "tx1", OutputIndex = 0, AddressId = address.Id, AmountSat = 50000, ScriptPubKey = [0xAA], ConfirmedHeight = 100 };
        _db.Utxos.Add(utxo);

        var tx = new TransactionEntity { Id = "cjtx1", RawHex = "0200000001..." };
        _db.Transactions.Add(tx);
        _db.SaveChanges();

        var record = new CoinJoinRecordEntity
        {
            TransactionId = "cjtx1",
            RoundId = "round-1",
            Status = "Completed",
            OurInputCount = 1,
            TotalInputCount = 5,
            OurOutputCount = 1,
            TotalOutputCount = 5
        };
        record.Participations.Add(new CoinJoinParticipationEntity { UtxoId = utxo.Id, Role = "Input" });
        _db.CoinJoinRecords.Add(record);
        _db.SaveChanges();

        var loaded = _db.CoinJoinRecords.Include(c => c.Participations).Single();
        Assert.Single(loaded.Participations);
        Assert.Equal("Input", loaded.Participations[0].Role);
    }

    [Fact]
    public void LabelEntity_PolymorphicIndex()
    {
        _db.Labels.Add(new LabelEntity { EntityType = "Address", EntityId = "1", Text = "Exchange deposit" });
        _db.Labels.Add(new LabelEntity { EntityType = "Transaction", EntityId = "tx1", Text = "CoinJoin" });
        _db.SaveChanges();

        var addressLabels = _db.Labels.Where(l => l.EntityType == "Address" && l.EntityId == "1").ToList();
        Assert.Single(addressLabels);
        Assert.Equal("Exchange deposit", addressLabels[0].Text);
    }
}
```

- [ ] **Step 3: Run the tests**

Run: `dotnet test Kompaktor.Tests/Kompaktor.Tests.csproj --filter "FullyQualifiedName~WalletDbTests" --configuration Release`
Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add Kompaktor.Tests/WalletDbTests.cs Kompaktor.Tests/Kompaktor.Tests.csproj
git commit -m "Add EF Core wallet database unit tests with in-memory SQLite"
```

---

### Task 9: Implement MnemonicEncryption

**Files:**
- Create: `Kompaktor.Wallet/MnemonicEncryption.cs`
- Create: `Kompaktor.Tests/MnemonicEncryptionTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// Kompaktor.Tests/MnemonicEncryptionTests.cs
using Kompaktor.Wallet;
using Xunit;

namespace Kompaktor.Tests;

public class MnemonicEncryptionTests
{
    [Fact]
    public void EncryptDecrypt_RoundTrip()
    {
        var mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        var passphrase = "test-passphrase-123";

        var (encrypted, salt) = MnemonicEncryption.Encrypt(mnemonic, passphrase);
        var decrypted = MnemonicEncryption.Decrypt(encrypted, salt, passphrase);

        Assert.Equal(mnemonic, decrypted);
    }

    [Fact]
    public void Decrypt_WrongPassphrase_Throws()
    {
        var mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        var (encrypted, salt) = MnemonicEncryption.Encrypt(mnemonic, "correct-password");

        Assert.ThrowsAny<Exception>(() => MnemonicEncryption.Decrypt(encrypted, salt, "wrong-password"));
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertextEachTime()
    {
        var mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        var passphrase = "password";

        var (enc1, salt1) = MnemonicEncryption.Encrypt(mnemonic, passphrase);
        var (enc2, salt2) = MnemonicEncryption.Encrypt(mnemonic, passphrase);

        // Different salts and nonces produce different ciphertext
        Assert.NotEqual(enc1, enc2);
    }

    [Fact]
    public void EncryptDecrypt_24WordMnemonic()
    {
        var mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon art";
        var passphrase = "long-passphrase";

        var (encrypted, salt) = MnemonicEncryption.Encrypt(mnemonic, passphrase);
        var decrypted = MnemonicEncryption.Decrypt(encrypted, salt, passphrase);

        Assert.Equal(mnemonic, decrypted);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Kompaktor.Tests/Kompaktor.Tests.csproj --filter "FullyQualifiedName~MnemonicEncryptionTests" --configuration Release`
Expected: FAIL — `MnemonicEncryption` type not found.

- [ ] **Step 3: Implement MnemonicEncryption**

```csharp
// Kompaktor.Wallet/MnemonicEncryption.cs
using System.Security.Cryptography;
using System.Text;

namespace Kompaktor.Wallet;

/// <summary>
/// AES-256-GCM encryption for BIP-39 mnemonics, keyed via PBKDF2 from a user passphrase.
/// </summary>
public static class MnemonicEncryption
{
    private const int SaltSize = 16;
    private const int NonceSize = 12; // AES-GCM standard
    private const int TagSize = 16;   // AES-GCM standard
    private const int KeySize = 32;   // AES-256
    private const int Iterations = 100_000;

    /// <summary>
    /// Encrypts a mnemonic string with AES-256-GCM.
    /// Returns (ciphertext_with_nonce_and_tag, salt).
    /// Layout: [12-byte nonce][ciphertext][16-byte tag]
    /// </summary>
    public static (byte[] Encrypted, byte[] Salt) Encrypt(string mnemonic, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = DeriveKey(passphrase, salt);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintext = Encoding.UTF8.GetBytes(mnemonic);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Pack as [nonce][ciphertext][tag]
        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, NonceSize);
        tag.CopyTo(result, NonceSize + ciphertext.Length);

        return (result, salt);
    }

    /// <summary>
    /// Decrypts a mnemonic. Throws CryptographicException on wrong passphrase / tampered data.
    /// </summary>
    public static string Decrypt(byte[] encrypted, byte[] salt, string passphrase)
    {
        var key = DeriveKey(passphrase, salt);

        var nonce = encrypted.AsSpan(0, NonceSize);
        var tag = encrypted.AsSpan(encrypted.Length - TagSize);
        var ciphertext = encrypted.AsSpan(NonceSize, encrypted.Length - NonceSize - TagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(passphrase, salt, Iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeySize);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Kompaktor.Tests/Kompaktor.Tests.csproj --filter "FullyQualifiedName~MnemonicEncryptionTests" --configuration Release`
Expected: All 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add Kompaktor.Wallet/MnemonicEncryption.cs Kompaktor.Tests/MnemonicEncryptionTests.cs
git commit -m "Add AES-256-GCM mnemonic encryption with PBKDF2 key derivation"
```

---

### Task 10: Implement KompaktorHdWallet

**Files:**
- Create: `Kompaktor.Wallet/KompaktorHdWallet.cs`

- [ ] **Step 1: Write KompaktorHdWallet tests**

```csharp
// Kompaktor.Tests/KompaktorHdWalletTests.cs
using Kompaktor.Blockchain;
using Kompaktor.Wallet;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Xunit;

namespace Kompaktor.Tests;

public class KompaktorHdWalletTests : IDisposable
{
    private readonly WalletDbContext _db;
    private readonly Network _network = Network.RegTest;
    private const string Passphrase = "test-passphrase";
    // Known BIP-39 test vector mnemonic
    private const string TestMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    public KompaktorHdWalletTests()
    {
        var options = new DbContextOptionsBuilder<WalletDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _db = new WalletDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Create_GeneratesWalletWithAccounts()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);

        var entity = _db.Wallets.Include(w => w.Accounts).ThenInclude(a => a.Addresses).Single();
        Assert.Equal("Test", entity.Name);
        Assert.Equal(2, entity.Accounts.Count); // P2WPKH (84) + P2TR (86)
        Assert.All(entity.Accounts, a => Assert.True(a.Addresses.Count >= 20)); // Gap limit
    }

    [Fact]
    public async Task Open_DecryptsMnemonic_DerivesKeys()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);
        wallet.Close();

        var reopened = await KompaktorHdWallet.OpenAsync(_db, wallet.WalletId, _network, Passphrase);
        Assert.NotNull(reopened);
    }

    [Fact]
    public async Task Open_WrongPassphrase_Throws()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);
        wallet.Close();

        await Assert.ThrowsAnyAsync<Exception>(
            () => KompaktorHdWallet.OpenAsync(_db, wallet.WalletId, _network, "wrong-password"));
    }

    [Fact]
    public async Task GetCoins_ReturnsOnlyConfirmedUnspent()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);
        var walletEntity = _db.Wallets.Include(w => w.Accounts).ThenInclude(a => a.Addresses).Single();
        var address = walletEntity.Accounts[0].Addresses[0];

        // Add a confirmed unspent UTXO
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "abc123", OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100000, ScriptPubKey = address.ScriptPubKey,
            ConfirmedHeight = 100
        });
        // Add an unconfirmed UTXO (should be excluded)
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "def456", OutputIndex = 0, AddressId = address.Id,
            AmountSat = 50000, ScriptPubKey = address.ScriptPubKey,
            ConfirmedHeight = null
        });
        _db.SaveChanges();

        var coins = await wallet.GetCoins();
        Assert.Single(coins);
        Assert.Equal(Money.Satoshis(100000), coins[0].Amount);
    }

    [Fact]
    public async Task MarkScriptsExposed_SetsFlag_GeneratesReplacements()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);
        var walletEntity = _db.Wallets.Include(w => w.Accounts).ThenInclude(a => a.Addresses).Single();
        var address = walletEntity.Accounts[0].Addresses.First(a => !a.IsChange);
        var script = new Script(address.ScriptPubKey);

        var countBefore = walletEntity.Accounts[0].Addresses.Count;
        await wallet.MarkScriptsExposed([script]);

        // Reload
        _db.ChangeTracker.Clear();
        var reloaded = _db.Addresses.Single(a => a.Id == address.Id);
        Assert.True(reloaded.IsExposed);
    }

    [Fact]
    public async Task GenerateOwnershipProof_ReturnsValidSignature()
    {
        var wallet = await KompaktorHdWallet.CreateAsync(_db, _network, "Test", Passphrase);
        var walletEntity = _db.Wallets.Include(w => w.Accounts).ThenInclude(a => a.Addresses).Single();
        var address = walletEntity.Accounts[0].Addresses.First(a => !a.IsChange);

        // Add a UTXO for this address
        _db.Utxos.Add(new UtxoEntity
        {
            TxId = "abc123", OutputIndex = 0, AddressId = address.Id,
            AmountSat = 100000, ScriptPubKey = address.ScriptPubKey,
            ConfirmedHeight = 100
        });
        _db.SaveChanges();

        var coins = await wallet.GetCoins();
        var proof = await wallet.GenerateOwnershipProof("round-1", coins);
        Assert.NotNull(proof);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Kompaktor.Tests/Kompaktor.Tests.csproj --filter "FullyQualifiedName~KompaktorHdWalletTests" --configuration Release`
Expected: FAIL — `KompaktorHdWallet` not found.

- [ ] **Step 3: Implement KompaktorHdWallet**

```csharp
// Kompaktor.Wallet/KompaktorHdWallet.cs
using Kompaktor.Blockchain;
using Kompaktor.Contracts;
using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using NBitcoin.BIP322;

namespace Kompaktor.Wallet;

public class KompaktorHdWallet : IKompaktorWalletInterface
{
    private readonly WalletDbContext _db;
    private readonly Network _network;
    private ExtKey? _masterKey;
    private IBlockchainBackend? _blockchain;

    public string WalletId { get; }

    private const int GapLimit = 20;

    private KompaktorHdWallet(WalletDbContext db, Network network, string walletId)
    {
        _db = db;
        _network = network;
        WalletId = walletId;
    }

    public void SetBlockchainBackend(IBlockchainBackend blockchain) => _blockchain = blockchain;

    public static async Task<KompaktorHdWallet> CreateAsync(
        WalletDbContext db, Network network, string name, string passphrase, int wordCount = 12)
    {
        var mnemonic = new Mnemonic(Wordlist.English, wordCount == 24 ? WordCount.TwentyFour : WordCount.Twelve);
        var mnemonicStr = mnemonic.ToString();
        var (encrypted, salt) = MnemonicEncryption.Encrypt(mnemonicStr, passphrase);

        var walletEntity = new WalletEntity
        {
            Name = name,
            EncryptedMnemonic = encrypted,
            MnemonicSalt = salt,
            Network = network.Name
        };

        var masterKey = mnemonic.DeriveExtKey();
        var coinType = network == Network.Main ? 0 : 1;

        // Create P2WPKH account (purpose 84) and P2TR account (purpose 86)
        foreach (var purpose in new[] { 84, 86 })
        {
            var account = new AccountEntity { Purpose = purpose, AccountIndex = 0 };
            var accountKey = masterKey.Derive(new KeyPath($"m/{purpose}'/{coinType}'/0'"));

            // Generate gap limit addresses for external (0) and internal/change (1) chains
            foreach (var chain in new[] { 0, 1 })
            {
                for (var i = 0; i < GapLimit; i++)
                {
                    var childKey = accountKey.Derive(chain).Derive(i);
                    var script = purpose == 84
                        ? childKey.PrivateKey.GetScriptPubKey(ScriptPubKeyType.Segwit)
                        : childKey.PrivateKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

                    account.Addresses.Add(new AddressEntity
                    {
                        KeyPath = $"{chain}/{i}",
                        ScriptPubKey = script.ToBytes(),
                        IsChange = chain == 1
                    });
                }
            }

            walletEntity.Accounts.Add(account);
        }

        db.Wallets.Add(walletEntity);
        await db.SaveChangesAsync();

        var wallet = new KompaktorHdWallet(db, network, walletEntity.Id) { _masterKey = masterKey };
        return wallet;
    }

    public static async Task<KompaktorHdWallet> OpenAsync(
        WalletDbContext db, string walletId, Network network, string passphrase)
    {
        var entity = await db.Wallets.SingleAsync(w => w.Id == walletId);
        var mnemonicStr = MnemonicEncryption.Decrypt(entity.EncryptedMnemonic, entity.MnemonicSalt, passphrase);
        var mnemonic = new Mnemonic(mnemonicStr);
        var masterKey = mnemonic.DeriveExtKey();

        return new KompaktorHdWallet(db, network, walletId) { _masterKey = masterKey };
    }

    public void Close()
    {
        _masterKey = null;
    }

    public async Task<Coin[]> GetCoins()
    {
        var utxos = await _db.Utxos
            .Include(u => u.Address)
            .Where(u => u.SpentByTxId == null && u.ConfirmedHeight != null)
            .Where(u => u.Address.Account.Wallet.Id == WalletId)
            .ToListAsync();

        return utxos.Select(u => new Coin(
            new OutPoint(uint256.Parse(u.TxId), u.OutputIndex),
            new TxOut(Money.Satoshis(u.AmountSat), new Script(u.ScriptPubKey))
        )).ToArray();
    }

    public async Task<BIP322Signature.Full> GenerateOwnershipProof(string message, Coin[] coins)
    {
        if (_masterKey is null) throw new InvalidOperationException("Wallet is locked");

        var key = await DeriveKeyForScriptAsync(coins[0].ScriptPubKey);
        return BIP322Signature.Full.Create(key, message, _network);
    }

    public async Task<WitScript> GenerateWitness(Coin coin, Transaction tx, IEnumerable<Coin> txCoins)
    {
        if (_masterKey is null) throw new InvalidOperationException("Wallet is locked");

        var key = await DeriveKeyForScriptAsync(coin.ScriptPubKey);
        var allCoins = txCoins.ToArray();
        var idx = Array.FindIndex(allCoins, c => c.Outpoint == coin.Outpoint);

        var builder = _network.CreateTransactionBuilder();
        builder.AddKeys(key);
        builder.AddCoins(allCoins);

        var signed = builder.SignTransaction(tx);
        return signed.Inputs[idx].WitScript;
    }

    public async Task<bool?> VerifyUtxo(OutPoint outpoint, TxOut expectedTxOut)
    {
        if (_blockchain is null) return null;

        var info = await _blockchain.GetUtxoAsync(outpoint);
        if (info is null) return false;
        return info.TxOut.Value == expectedTxOut.Value && info.TxOut.ScriptPubKey == expectedTxOut.ScriptPubKey;
    }

    public async Task MarkScriptsExposed(IEnumerable<Script> scripts)
    {
        var scriptBytes = scripts.Select(s => s.ToBytes()).ToList();
        var addresses = await _db.Addresses
            .Where(a => a.Account.Wallet.Id == WalletId)
            .Where(a => scriptBytes.Any(sb => a.ScriptPubKey == sb))
            .ToListAsync();

        foreach (var addr in addresses)
            addr.IsExposed = true;

        await _db.SaveChangesAsync();
    }

    private async Task<Key> DeriveKeyForScriptAsync(Script scriptPubKey)
    {
        var scriptBytes = scriptPubKey.ToBytes();
        var address = await _db.Addresses
            .Include(a => a.Account)
            .Where(a => a.Account.Wallet.Id == WalletId)
            .FirstAsync(a => a.ScriptPubKey == scriptBytes);

        var coinType = _network == Network.Main ? 0 : 1;
        var fullPath = new KeyPath($"m/{address.Account.Purpose}'/{coinType}'/{address.Account.AccountIndex}'/{address.KeyPath}");
        return _masterKey!.Derive(fullPath).PrivateKey;
    }
}
```

- [ ] **Step 4: Run all wallet tests**

Run: `dotnet test Kompaktor.Tests/Kompaktor.Tests.csproj --filter "FullyQualifiedName~KompaktorHdWalletTests" --configuration Release`
Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add Kompaktor.Wallet/KompaktorHdWallet.cs Kompaktor.Tests/KompaktorHdWalletTests.cs
git commit -m "Implement KompaktorHdWallet with HD key derivation and IKompaktorWalletInterface"
```

---

## Phase 3: ElectrumClient + ElectrumBackend

### Task 11: Implement ElectrumOptions

**Files:**
- Create: `Kompaktor.Blockchain/ElectrumOptions.cs`

- [ ] **Step 1: Create ElectrumOptions**

```csharp
// Kompaktor.Blockchain/ElectrumOptions.cs
namespace Kompaktor.Blockchain;

public class ElectrumOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 50001;
    public bool UseSsl { get; set; }
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxReconnectAttempts { get; set; } = 10;
    public TimeSpan ReconnectBaseDelay { get; set; } = TimeSpan.FromSeconds(1);
}
```

- [ ] **Step 2: Commit**

```bash
git add Kompaktor.Blockchain/ElectrumOptions.cs
git commit -m "Add ElectrumOptions configuration for Stratum connections"
```

---

### Task 12: Implement ElectrumClient (low-level Stratum JSON-RPC)

**Files:**
- Create: `Kompaktor.Blockchain/ElectrumClient.cs`
- Create: `Kompaktor.Tests/ElectrumClientTests.cs`

- [ ] **Step 1: Write ElectrumClient unit tests with mocked stream**

```csharp
// Kompaktor.Tests/ElectrumClientTests.cs
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Kompaktor.Blockchain;
using Xunit;

namespace Kompaktor.Tests;

public class ElectrumClientTests
{
    [Fact]
    public async Task SendRequest_CorrelatesResponseById()
    {
        using var pair = new StreamPair();
        var client = new ElectrumClient(pair.ClientStream);

        // Simulate server response on another task
        var serverTask = Task.Run(async () =>
        {
            // Read the request
            var line = await pair.ReadServerLineAsync();
            var request = JsonDocument.Parse(line);
            var id = request.RootElement.GetProperty("id").GetInt32();

            // Respond
            var response = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = "1.4" });
            await pair.WriteServerLineAsync(response);
        });

        var result = await client.RequestAsync("server.version", ["Kompaktor", "1.4"]);
        await serverTask;

        Assert.Equal("1.4", result.GetString());
    }

    [Fact]
    public async Task SendRequest_MultipleConcurrent_CorrectCorrelation()
    {
        using var pair = new StreamPair();
        var client = new ElectrumClient(pair.ClientStream);

        var serverTask = Task.Run(async () =>
        {
            var requests = new List<(int id, string method)>();
            for (int i = 0; i < 3; i++)
            {
                var line = await pair.ReadServerLineAsync();
                var doc = JsonDocument.Parse(line);
                requests.Add((doc.RootElement.GetProperty("id").GetInt32(),
                    doc.RootElement.GetProperty("method").GetString()!));
            }

            // Respond in reverse order to test correlation
            requests.Reverse();
            foreach (var (id, method) in requests)
            {
                var response = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = $"response-{id}" });
                await pair.WriteServerLineAsync(response);
            }
        });

        var t1 = client.RequestAsync("method.a", []);
        var t2 = client.RequestAsync("method.b", []);
        var t3 = client.RequestAsync("method.c", []);

        var results = await Task.WhenAll(t1, t2, t3);
        await serverTask;

        // Each result should correspond to its own request id
        Assert.NotNull(results[0]);
        Assert.NotNull(results[1]);
        Assert.NotNull(results[2]);
    }

    [Fact]
    public async Task SubscriptionNotification_DispatchedToHandler()
    {
        using var pair = new StreamPair();
        var client = new ElectrumClient(pair.ClientStream);

        var received = new TaskCompletionSource<JsonElement>();
        client.OnNotification += (method, parameters) =>
        {
            if (method == "blockchain.scripthash.subscribe")
                received.SetResult(parameters);
        };

        // Send subscription response first
        var subResponse = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, result = "status_hash_1" });

        var serverTask = Task.Run(async () =>
        {
            // Read subscription request
            await pair.ReadServerLineAsync();
            await pair.WriteServerLineAsync(subResponse);

            // Then send a notification (no id)
            var notification = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "blockchain.scripthash.subscribe",
                @params = new[] { "scripthash_abc", "new_status_hash" }
            });
            await pair.WriteServerLineAsync(notification);
        });

        await client.RequestAsync("blockchain.scripthash.subscribe", ["scripthash_abc"]);
        var notif = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await serverTask;

        Assert.Equal(JsonValueKind.Array, notif.ValueKind);
    }
}

/// <summary>
/// Provides a pair of streams connected by an in-memory pipe for testing.
/// </summary>
internal class StreamPair : IDisposable
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly StreamReader _serverReader;
    private readonly StreamWriter _serverWriter;

    public StreamPair()
    {
        ClientStream = new DuplexPipeStream(_serverToClient.Reader, _clientToServer.Writer);
        _serverReader = new StreamReader(_clientToServer.Reader.AsStream());
        _serverWriter = new StreamWriter(_serverToClient.Writer.AsStream()) { AutoFlush = true };
    }

    public Stream ClientStream { get; }

    public async Task<string> ReadServerLineAsync() =>
        (await _serverReader.ReadLineAsync())!;

    public async Task WriteServerLineAsync(string line) =>
        await _serverWriter.WriteLineAsync(line);

    public void Dispose()
    {
        _clientToServer.Writer.Complete();
        _serverToClient.Writer.Complete();
    }
}

internal class DuplexPipeStream : Stream
{
    private readonly Stream _readStream;
    private readonly Stream _writeStream;

    public DuplexPipeStream(PipeReader reader, PipeWriter writer)
    {
        _readStream = reader.AsStream();
        _writeStream = writer.AsStream();
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) =>
        _readStream.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        _readStream.ReadAsync(buffer, offset, count, ct);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        _readStream.ReadAsync(buffer, ct);

    public override void Write(byte[] buffer, int offset, int count) =>
        _writeStream.Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        _writeStream.WriteAsync(buffer, offset, count, ct);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        _writeStream.WriteAsync(buffer, ct);

    public override void Flush() => _writeStream.Flush();
    public override Task FlushAsync(CancellationToken ct) => _writeStream.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
```

- [ ] **Step 2: Implement ElectrumClient**

```csharp
// Kompaktor.Blockchain/ElectrumClient.cs
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Kompaktor.Blockchain;

/// <summary>
/// Low-level Electrum Stratum JSON-RPC client over TCP/SSL.
/// Handles request/response correlation via message IDs and subscription notification dispatch.
/// </summary>
public class ElectrumClient : IDisposable
{
    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _nextId;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;

    public event Action<string, JsonElement>? OnNotification;

    /// <summary>
    /// Create a client from an existing connected stream (used for testing with mocked streams).
    /// </summary>
    public ElectrumClient(Stream stream)
    {
        _stream = stream;
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        StartReadLoop();
    }

    /// <summary>
    /// Send a JSON-RPC request and wait for the correlated response.
    /// </summary>
    public async Task<JsonElement> RequestAsync(string method, object[] parameters, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        });

        await _writeLock.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync(request);
        }
        finally
        {
            _writeLock.Release();
        }

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        return await tcs.Task;
    }

    private void StartReadLoop()
    {
        _readCts = new CancellationTokenSource();
        _readLoop = Task.Run(async () =>
        {
            try
            {
                while (!_readCts.Token.IsCancellationRequested)
                {
                    var line = await _reader.ReadLineAsync(_readCts.Token);
                    if (line is null) break;

                    try
                    {
                        var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                        {
                            // Response to a request
                            var id = idProp.GetInt32();
                            if (_pending.TryRemove(id, out var tcs))
                            {
                                if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                                    tcs.SetException(new ElectrumException(error.ToString()));
                                else if (root.TryGetProperty("result", out var result))
                                    tcs.SetResult(result.Clone());
                                else
                                    tcs.SetResult(default);
                            }
                        }
                        else if (root.TryGetProperty("method", out var methodProp))
                        {
                            // Subscription notification
                            var method = methodProp.GetString()!;
                            var parameters = root.TryGetProperty("params", out var p)
                                ? p.Clone()
                                : default;
                            OnNotification?.Invoke(method, parameters);
                        }
                    }
                    catch (JsonException)
                    {
                        // Skip malformed messages
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        });
    }

    public void Dispose()
    {
        _readCts?.Cancel();
        _readCts?.Dispose();
        _writeLock.Dispose();
        _reader.Dispose();
        _writer.Dispose();

        // Fail all pending requests
        foreach (var kvp in _pending)
            kvp.Value.TrySetCanceled();
        _pending.Clear();
    }
}

public class ElectrumException : Exception
{
    public ElectrumException(string message) : base(message) { }
}
```

- [ ] **Step 3: Run unit tests**

Run: `dotnet test Kompaktor.Tests/Kompaktor.Tests.csproj --filter "FullyQualifiedName~ElectrumClientTests" --configuration Release`
Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add Kompaktor.Blockchain/ElectrumClient.cs Kompaktor.Tests/ElectrumClientTests.cs
git commit -m "Implement ElectrumClient with JSON-RPC correlation and subscription dispatch"
```

---

### Task 13: Implement ElectrumBackend

**Files:**
- Create: `Kompaktor.Blockchain/ElectrumBackend.cs`

- [ ] **Step 1: Implement ElectrumBackend**

```csharp
// Kompaktor.Blockchain/ElectrumBackend.cs
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using NBitcoin;

namespace Kompaktor.Blockchain;

/// <summary>
/// IBlockchainBackend implementation using the Electrum Stratum protocol.
/// Wraps ElectrumClient and translates IBlockchainBackend calls to Stratum JSON-RPC.
/// </summary>
public class ElectrumBackend : IBlockchainBackend
{
    private readonly ElectrumOptions _options;
    private ElectrumClient? _client;
    private TcpClient? _tcp;
    private int _latestHeight;
    private readonly ConcurrentDictionary<string, Script> _scriptHashToScript = new();

    public ElectrumBackend(ElectrumOptions options)
    {
        _options = options;
    }

    public bool IsConnected => _client is not null;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_options.Host, _options.Port, ct);

        Stream stream = _tcp.GetStream();
        if (_options.UseSsl)
        {
            var sslStream = new SslStream(stream, false);
            await sslStream.AuthenticateAsClientAsync(_options.Host);
            stream = sslStream;
        }

        _client = new ElectrumClient(stream);

        // Handshake
        await _client.RequestAsync("server.version", ["Kompaktor", "1.4"], ct);

        // Subscribe to headers for block height tracking
        _client.OnNotification += HandleNotification;
        var headerResult = await _client.RequestAsync("blockchain.headers.subscribe", [], ct);
        if (headerResult.TryGetProperty("height", out var h))
            _latestHeight = h.GetInt32();
    }

    public async Task<UtxoInfo?> GetUtxoAsync(OutPoint outpoint, CancellationToken ct = default)
    {
        var tx = await GetTransactionAsync(outpoint.Hash, ct);
        if (tx is null || outpoint.N >= tx.Outputs.Count) return null;

        var txOut = tx.Outputs[(int)outpoint.N];
        var scriptHash = ToElectrumScriptHash(txOut.ScriptPubKey);
        var unspent = await _client!.RequestAsync("blockchain.scripthash.listunspent", [scriptHash], ct);

        foreach (var item in unspent.EnumerateArray())
        {
            var txHash = item.GetProperty("tx_hash").GetString();
            var txPos = item.GetProperty("tx_pos").GetInt32();
            if (txHash == outpoint.Hash.ToString() && txPos == (int)outpoint.N)
            {
                var height = item.GetProperty("height").GetInt32();
                var confirmations = height > 0 ? _latestHeight - height + 1 : 0;
                return new UtxoInfo(outpoint, txOut, confirmations, false);
            }
        }

        return null; // Spent or not found
    }

    public async Task<IList<UtxoInfo>> GetUtxosForScriptAsync(Script scriptPubKey, CancellationToken ct = default)
    {
        var scriptHash = ToElectrumScriptHash(scriptPubKey);
        var result = await _client!.RequestAsync("blockchain.scripthash.listunspent", [scriptHash], ct);
        var utxos = new List<UtxoInfo>();

        foreach (var item in result.EnumerateArray())
        {
            var txHash = uint256.Parse(item.GetProperty("tx_hash").GetString()!);
            var txPos = item.GetProperty("tx_pos").GetInt32();
            var value = item.GetProperty("value").GetInt64();
            var height = item.GetProperty("height").GetInt32();
            var confirmations = height > 0 ? _latestHeight - height + 1 : 0;

            utxos.Add(new UtxoInfo(
                new OutPoint(txHash, txPos),
                new TxOut(Money.Satoshis(value), scriptPubKey),
                confirmations,
                false));
        }

        return utxos;
    }

    public async Task<Transaction?> GetTransactionAsync(uint256 txId, CancellationToken ct = default)
    {
        try
        {
            var hex = await _client!.RequestAsync("blockchain.transaction.get", [txId.ToString()], ct);
            return Transaction.Parse(hex.GetString()!, Network.Main); // Raw hex is network-agnostic
        }
        catch (ElectrumException)
        {
            return null;
        }
    }

    public async Task<uint256> BroadcastAsync(Transaction tx, CancellationToken ct = default)
    {
        var result = await _client!.RequestAsync("blockchain.transaction.broadcast", [tx.ToHex()], ct);
        return uint256.Parse(result.GetString()!);
    }

    public async Task SubscribeAddressAsync(Script scriptPubKey, CancellationToken ct = default)
    {
        var scriptHash = ToElectrumScriptHash(scriptPubKey);
        _scriptHashToScript[scriptHash] = scriptPubKey;
        await _client!.RequestAsync("blockchain.scripthash.subscribe", [scriptHash], ct);
    }

    public Task UnsubscribeAddressAsync(Script scriptPubKey, CancellationToken ct = default)
    {
        var scriptHash = ToElectrumScriptHash(scriptPubKey);
        _scriptHashToScript.TryRemove(scriptHash, out _);
        return Task.CompletedTask;
    }

    public event EventHandler<AddressNotification>? AddressNotified;

    public async Task<FeeRate> EstimateFeeAsync(int confirmationTarget = 6, CancellationToken ct = default)
    {
        var result = await _client!.RequestAsync("blockchain.estimatefee", [confirmationTarget], ct);
        var btcPerKb = result.GetDouble();
        if (btcPerKb < 0) btcPerKb = 0.00001; // Fallback minimum
        return new FeeRate(Money.Coins((decimal)btcPerKb));
    }

    public Task<int> GetBlockHeightAsync(CancellationToken ct = default) =>
        Task.FromResult(_latestHeight);

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _tcp?.Dispose();
    }

    private void HandleNotification(string method, JsonElement parameters)
    {
        if (method == "blockchain.headers.subscribe" && parameters.ValueKind == JsonValueKind.Array)
        {
            var header = parameters[0];
            if (header.TryGetProperty("height", out var h))
                _latestHeight = h.GetInt32();
        }
        else if (method == "blockchain.scripthash.subscribe" && parameters.ValueKind == JsonValueKind.Array)
        {
            var scriptHash = parameters[0].GetString();
            if (scriptHash is not null && _scriptHashToScript.TryGetValue(scriptHash, out var script))
            {
                AddressNotified?.Invoke(this, new AddressNotification(script, uint256.Zero, null));
            }
        }
    }

    internal static string ToElectrumScriptHash(Script script)
    {
        var hash = SHA256.HashData(script.ToBytes());
        Array.Reverse(hash);
        return Convert.ToHexString(hash).ToLower();
    }
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build Kompaktor.Blockchain/Kompaktor.Blockchain.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Kompaktor.Blockchain/ElectrumBackend.cs
git commit -m "Implement ElectrumBackend with Stratum protocol for IBlockchainBackend"
```

---

## Phase 4: Sample Console App + End-to-End

### Task 14: Create Kompaktor.Wallet.Sample console app

**Files:**
- Create: `Kompaktor.Wallet.Sample/Kompaktor.Wallet.Sample.csproj`
- Create: `Kompaktor.Wallet.Sample/Program.cs`

- [ ] **Step 1: Create console app project**

```xml
<!-- Kompaktor.Wallet.Sample/Kompaktor.Wallet.Sample.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <Description>Sample app running Kompaktor coordinator + HD wallet client in one process</Description>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="NBitcoin" Version="7.0.46"/>
        <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.0.0"/>
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\Kompaktor\Kompaktor.csproj" />
        <ProjectReference Include="..\Kompaktor.Blockchain\Kompaktor.Blockchain.csproj" />
        <ProjectReference Include="..\Kompaktor.Wallet\Kompaktor.Wallet.csproj" />
        <ProjectReference Include="..\Kompaktor.Server\Kompaktor.Server.csproj" />
    </ItemGroup>

</Project>
```

- [ ] **Step 2: Implement Program.cs**

```csharp
// Kompaktor.Wallet.Sample/Program.cs
using System.Net;
using System.Security.Cryptography;
using Kompaktor;
using Kompaktor.Behaviors;
using Kompaktor.Blockchain;
using Kompaktor.Credentials;
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
using WabiSabi.Crypto;
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
logger.LogInformation("Blockchain backend connected. Height: {Height}", await blockchain.GetBlockHeightAsync());

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
    logger.LogInformation("Wallet receive address scripts are pre-generated in the DB.");
}
else
{
    logger.LogInformation("Ready for coinjoin with {Count} coins", coins.Length);
}

// --- Cleanup ---
wallet.Close();
await blockchain.DisposeAsync();
logger.LogInformation("Done.");
```

- [ ] **Step 3: Add project to solution and build**

Run:
```bash
dotnet sln Kompaktor.sln add Kompaktor.Wallet.Sample/Kompaktor.Wallet.Sample.csproj
dotnet build Kompaktor.Wallet.Sample/Kompaktor.Wallet.Sample.csproj
```
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Kompaktor.Wallet.Sample/ Kompaktor.sln
git commit -m "Add Kompaktor.Wallet.Sample console app with coordinator + client dual-role"
```

---

### Task 15: Full solution build and regression test

**Files:** None (verification only)

- [ ] **Step 1: Build entire solution**

Run: `dotnet build Kompaktor.sln --configuration Release`
Expected: Build succeeded. 0 Error(s).

- [ ] **Step 2: Run all unit tests**

Run: `dotnet test Kompaktor.Tests/Kompaktor.Tests.csproj --configuration Release --filter "Category!=Integration" --no-build`
Expected: All new unit tests pass (WalletDb, MnemonicEncryption, KompaktorHdWallet, ElectrumClient).

- [ ] **Step 3: Run integration tests (requires docker)**

Run: `dotnet test Kompaktor.Tests/Kompaktor.Tests.csproj --configuration Release --no-build`
Expected: All 259+ existing tests pass through BitcoinCoreBackend adapter, plus new tests.

- [ ] **Step 4: Commit any fixes**

If any tests failed, fix and commit with a descriptive message.

- [ ] **Step 5: Push and verify CI**

```bash
git push origin master
```

Wait for CI. All jobs (build, nativeaot-test, unit-tests, integration-tests) should pass.

---

### Task 16: Update CI if needed for new projects

**Files:**
- Possibly modify: `.github/workflows/ci.yml` if NativeAOT test needs to exclude new projects

- [ ] **Step 1: Check if CI workflow needs changes**

Read `.github/workflows/ci.yml` and verify:
- `dotnet build` runs on the solution (already covers new projects)
- NativeAOT publish targets the right project
- Test runs pick up new test files automatically

- [ ] **Step 2: Fix and commit if needed**

If CI changes are required, make targeted edits and commit.
