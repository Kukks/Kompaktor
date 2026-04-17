# Kompaktor Wallet Library — Sub-Project A Design Spec

**Date:** 2026-04-17
**Scope:** Blockchain abstraction, Electrum client, HD wallet, EF Core persistence, sample app
**Out of scope:** Anonymity scoring (B), Web UI (C), split-server routing (D), credential tracking (E)

## Goal

Create a modular wallet library (`Kompaktor.Wallet`) and blockchain abstraction (`Kompaktor.Blockchain`) that replace the current hard dependency on Bitcoin Core RPC. The library manages HD keys, persists state via EF Core/SQLite, and implements `IKompaktorWalletInterface`. A sample console app demonstrates both coordinator and client roles in a single process.

## Architecture

### New Projects

| Project | Purpose | Dependencies |
|---------|---------|-------------|
| `Kompaktor.Blockchain` | `IBlockchainBackend` interface, `ElectrumBackend`, `BitcoinCoreBackend` | NBitcoin |
| `Kompaktor.Wallet` | HD wallet, EF Core persistence, `IKompaktorWalletInterface` impl | Kompaktor, Kompaktor.Blockchain, EF Core |
| `Kompaktor.Wallet.Sample` | Console app: coordinator + client in one process | All above + Kompaktor.Server |

### Modified Projects

| Project | Change |
|---------|--------|
| `Kompaktor` | `KompaktorRoundOperator` takes `IBlockchainBackend` instead of `RPCClient` |
| `Kompaktor.Server` | `KompaktorRoundManager` and DI use `IBlockchainBackend` |
| `Kompaktor.Tests` | Add `BitcoinCoreBackend` adapter so existing tests pass through new interface |

## 1. IBlockchainBackend

Abstraction over blockchain data access, replacing `RPCClient` in both coordinator and wallet.

```csharp
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

// Wire DTO for blockchain queries — distinct from UtxoEntity (persisted wallet state)
public record UtxoInfo(OutPoint OutPoint, TxOut TxOut, int Confirmations, bool IsCoinBase);
public record AddressNotification(Script ScriptPubKey, uint256 TxId, int? Height);
```

### Coordinator Integration

`KompaktorRoundOperator` constructor changes:

```csharp
// Before
public KompaktorRoundOperator(Network network, RPCClient rpcClient, ...)

// After
public KompaktorRoundOperator(Network network, IBlockchainBackend blockchain, ...)
```

Two call sites change:
- `_rpcClient.GetTxOutAsync(hash, n)` → `_blockchain.GetUtxoAsync(new OutPoint(hash, n))`
- `_rpcClient.SendCommandAsync("sendrawtransaction", tx.ToHex(), 0)` → `_blockchain.BroadcastAsync(tx)`

## 2. ElectrumBackend

Implements `IBlockchainBackend` using the Electrum Stratum protocol (JSON-RPC over TCP/SSL).

### Stratum Method Mapping

| IBlockchainBackend | Stratum Method |
|---|---|
| `GetUtxoAsync` | `blockchain.scripthash.listunspent` + filter by outpoint |
| `GetUtxosForScriptAsync` | `blockchain.scripthash.listunspent` |
| `GetTransactionAsync` | `blockchain.transaction.get` (verbose) |
| `BroadcastAsync` | `blockchain.transaction.broadcast` |
| `SubscribeAddressAsync` | `blockchain.scripthash.subscribe` |
| `EstimateFeeAsync` | `blockchain.estimatefee` |
| `GetBlockHeightAsync` | `blockchain.headers.subscribe` (caches latest) |

### ElectrumClient (internal)

Low-level TCP/SSL client handling:
- Connection handshake (`server.version`)
- JSON-RPC request/response correlation via message IDs
- Subscription notification dispatch
- Automatic reconnection with exponential backoff
- Concurrent request pipelining (multiple in-flight requests)

`ElectrumBackend` wraps `ElectrumClient` and implements `IBlockchainBackend`. This separation enables sub-project D to manage multiple `ElectrumClient` instances behind a routing backend.

### Configuration

```csharp
public class ElectrumOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 50001;
    public bool UseSsl { get; set; } = false;
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxReconnectAttempts { get; set; } = 10;
    public TimeSpan ReconnectBaseDelay { get; set; } = TimeSpan.FromSeconds(1);
}
```

### Script Hash Conversion

Electrum uses reversed SHA256 of the scriptPubKey as the subscription key:

```csharp
static string ToElectrumScriptHash(Script script)
{
    var hash = SHA256.HashData(script.ToBytes());
    Array.Reverse(hash);
    return Convert.ToHexString(hash).ToLower();
}
```

## 3. BitcoinCoreBackend

Thin adapter wrapping NBitcoin's `RPCClient` into `IBlockchainBackend`.

- `GetUtxoAsync` → `RPCClient.GetTxOutAsync`
- `BroadcastAsync` → `RPCClient.SendCommandAsync("sendrawtransaction")`
- `SubscribeAddressAsync` → polling via `RPCClient.GetTxOutAsync` on a background timer (Bitcoin Core has no push notifications)
- `EstimateFeeAsync` → `RPCClient.EstimateSmartFeeAsync`
- `GetBlockHeightAsync` → `RPCClient.GetBlockCountAsync`

This adapter keeps the existing test infrastructure working against the docker-compose regtest bitcoind.

## 4. EF Core Data Model

SQLite database with the following entities:

### WalletEntity

| Column | Type | Notes |
|--------|------|-------|
| Id | string (GUID) | PK |
| Name | string | User-friendly name |
| EncryptedMnemonic | byte[] | AES-256-GCM encrypted BIP-39 mnemonic |
| MnemonicSalt | byte[] | PBKDF2 salt for passphrase derivation |
| Network | string | "Main", "TestNet", "RegTest" |
| CreatedAt | DateTimeOffset | |

### AccountEntity

| Column | Type | Notes |
|--------|------|-------|
| Id | int | PK, auto-increment |
| WalletId | string | FK → Wallet |
| Purpose | int | 84 (P2WPKH) or 86 (P2TR) |
| AccountIndex | int | Usually 0 |

### AddressEntity

| Column | Type | Notes |
|--------|------|-------|
| Id | int | PK |
| AccountId | int | FK → Account |
| KeyPath | string | e.g. "0/5" (chain/index) |
| ScriptPubKey | byte[] | Indexed for lookups |
| IsChange | bool | Internal (1) vs external (0) chain |
| IsUsed | bool | Has received funds |
| IsExposed | bool | Disclosed in a failed round |

### UtxoEntity

| Column | Type | Notes |
|--------|------|-------|
| Id | int | PK |
| TxId | string | Transaction hash |
| OutputIndex | int | |
| AddressId | int | FK → Address |
| AmountSat | long | |
| ScriptPubKey | byte[] | |
| ConfirmedHeight | int? | null = unconfirmed |
| SpentByTxId | string? | null = unspent |
| IsCoinBase | bool | |

Unique index on (TxId, OutputIndex).

### TransactionEntity

| Column | Type | Notes |
|--------|------|-------|
| Id | string | TX hash |
| RawHex | string | Full serialized transaction |
| BlockHeight | int? | |
| Timestamp | DateTimeOffset | |

### CoinJoinRecordEntity

| Column | Type | Notes |
|--------|------|-------|
| Id | int | PK |
| TransactionId | string | FK → Transaction |
| RoundId | string | Kompaktor round ID |
| Status | string | "Completed", "Failed" |
| OurInputCount | int | How many of our inputs participated |
| TotalInputCount | int | Total round inputs |
| OurOutputCount | int | |
| TotalOutputCount | int | |
| CreatedAt | DateTimeOffset | |

### CoinJoinParticipationEntity

| Column | Type | Notes |
|--------|------|-------|
| Id | int | PK |
| CoinJoinRecordId | int | FK → CoinJoinRecord |
| UtxoId | int | FK → Utxo |
| Role | string | "Input" or "Output" |

### LabelEntity

| Column | Type | Notes |
|--------|------|-------|
| Id | int | PK |
| EntityType | string | "Address", "Transaction", "Utxo", "CoinJoin" |
| EntityId | string | Polymorphic FK |
| Text | string | Label content |
| CreatedAt | DateTimeOffset | |

Index on (EntityType, EntityId).

### DbContext

```csharp
public class WalletDbContext : DbContext
{
    public DbSet<WalletEntity> Wallets { get; set; }
    public DbSet<AccountEntity> Accounts { get; set; }
    public DbSet<AddressEntity> Addresses { get; set; }
    public DbSet<UtxoEntity> Utxos { get; set; }
    public DbSet<TransactionEntity> Transactions { get; set; }
    public DbSet<CoinJoinRecordEntity> CoinJoinRecords { get; set; }
    public DbSet<CoinJoinParticipationEntity> CoinJoinParticipations { get; set; }
    public DbSet<LabelEntity> Labels { get; set; }
}
```

## 5. KompaktorHdWallet

Implements `IKompaktorWalletInterface` using HD keys and EF Core persistence.

### Key Management

- BIP-39 mnemonic (12 or 24 words) generated on wallet creation
- Mnemonic encrypted at rest with AES-256-GCM, key derived via PBKDF2 from user passphrase
- HD derivation paths:
  - P2WPKH: `m/84'/coin'/0'/chain/index`
  - P2TR: `m/86'/coin'/0'/chain/index`
  - `coin'` = 0 for mainnet, 1 for testnet/regtest
- Mnemonic held in memory only while wallet is unlocked
- Gap limit: 20 addresses pre-generated per chain (receive external, change internal)

### IKompaktorWalletInterface Implementation

| Method | Implementation |
|--------|---------------|
| `GetCoins()` | Query `Utxos` where `SpentByTxId == null && ConfirmedHeight != null` |
| `GenerateOwnershipProof(roundId, coins)` | Derive private key from coin's address KeyPath, create BIP-322 signature |
| `GenerateWitness(coin, tx, allCoins)` | Derive private key, sign transaction input (P2WPKH or P2TR key-spend) |
| `VerifyUtxo(outpoint, txOut)` | Call `IBlockchainBackend.GetUtxoAsync`, compare amount and script |
| `MarkScriptsExposed(scripts)` | Set `IsExposed = true` on matching addresses, derive fresh replacements |

### Address Management

- On startup: subscribe all unused addresses + gap limit to `IBlockchainBackend`
- On `AddressNotified`: update UTXO set, mark address as used, extend gap if needed
- Fresh address derivation: skip any address where `IsUsed || IsExposed`
- Change addresses: separate internal chain (index 1), same gap limit logic

### Wallet Lifecycle

```
Create(passphrase) → generates mnemonic, encrypts, creates accounts + initial addresses
Open(passphrase) → decrypts mnemonic, derives master key, subscribes addresses
Sync() → queries all subscribed addresses for current UTXOs
Close() → clears mnemonic from memory, unsubscribes
```

## 6. Sample Console App

`Kompaktor.Wallet.Sample` — a console app that runs both coordinator and participant in one process.

### Startup Flow

1. Parse CLI args: `--network`, `--electrum-host`, `--electrum-port`, `--rpc-uri`, `--wallet-path`
2. If `--electrum-host` specified, create `ElectrumBackend`; otherwise create `BitcoinCoreBackend` from `--rpc-uri`
3. Create or open wallet from `--wallet-path` (prompt for passphrase)
4. Start `KompaktorRoundManager` (coordinator) with blockchain backend
5. Wait for wallet sync
6. Create `KompaktorRoundClient` with `KompaktorHdWallet` + behavior traits
7. Run a coinjoin round
8. Print results

### For Regtest Testing

Uses the existing docker-compose bitcoind:
```bash
dotnet run --project Kompaktor.Wallet.Sample -- \
  --network regtest \
  --rpc-uri http://localhost:53782 \
  --rpc-user ceiwHEbqWI83 \
  --rpc-password DwubwWsoo3 \
  --wallet-path ./wallet.db
```

### For Testnet/Mainnet

```bash
dotnet run --project Kompaktor.Wallet.Sample -- \
  --network testnet \
  --electrum-host electrum.blockstream.info \
  --electrum-port 60002 \
  --electrum-ssl \
  --wallet-path ./wallet.db
```

## 7. Testing Strategy

| Layer | Type | Approach |
|-------|------|----------|
| `ElectrumClient` | Unit | Mock TCP stream, verify JSON-RPC framing, subscription dispatch |
| `ElectrumBackend` | Integration | Real Electrs instance via docker-compose |
| `BitcoinCoreBackend` | Integration | Existing regtest bitcoind |
| `KompaktorHdWallet` | Unit | Key derivation correctness, BIP-322 signatures, address rotation |
| EF Core model | Unit | In-memory SQLite, migrations, UTXO state queries |
| Coordinator refactor | Integration | Existing test suite passes through `BitcoinCoreBackend` adapter |
| End-to-end | Integration | Full round with HD wallet, blockchain backend, behavior traits |

### Regression Safety

The existing 260+ tests must continue passing. The `BitcoinCoreBackend` adapter ensures the coordinator refactor is transparent — all existing tests use `RPCClient` through the new interface without behavior changes.

## 8. Migration Path

### Phase 1: Interface + BitcoinCoreBackend (non-breaking)

1. Add `IBlockchainBackend` to `Kompaktor.Blockchain`
2. Implement `BitcoinCoreBackend` wrapping existing `RPCClient`
3. Refactor `KompaktorRoundOperator` to use `IBlockchainBackend`
4. Verify all existing tests pass

### Phase 2: Wallet Library

5. Create EF Core model and migrations
6. Implement `KompaktorHdWallet`
7. Unit test key derivation, signing, address management

### Phase 3: Electrum Client

8. Implement `ElectrumClient` (Stratum JSON-RPC over TCP/SSL)
9. Implement `ElectrumBackend`
10. Integration test against Electrs docker container

### Phase 4: Sample App + End-to-End

11. Build sample console app
12. End-to-end test: full coinjoin round with HD wallet
13. Test against both Bitcoin Core and Electrum backends

## 9. Future Sub-Project Hooks

The design includes deliberate extension points for future sub-projects:

- **Anonymity scoring (B):** `CoinJoinRecordEntity` + `CoinJoinParticipationEntity` + `LabelEntity` provide the data foundation. A scoring service queries coinjoin history per UTXO.
- **Split-server (D):** `ElectrumClient` is separated from `ElectrumBackend`. A `RoutingElectrumBackend` can manage multiple `ElectrumClient` instances and route `SubscribeAddressAsync` calls to designated servers.
- **Credential tracking (E):** `IKompaktorWalletInterface` can be extended with credential lifecycle hooks. The wallet DB can add a `CredentialChainEntity` table.
- **Web UI (C):** The wallet library exposes a service API that a web frontend consumes. No UI coupling in the library itself.
