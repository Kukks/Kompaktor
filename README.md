# Kompaktor

[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/Kukks/Kompaktor)

A Bitcoin privacy protocol implementing collaborative transactions (coinjoins) using the [WabiSabi](https://eprint.iacr.org/2021/206) anonymous credential system. Kompaktor enables multiple participants to construct a joint transaction where inputs and outputs cannot be linked, providing strong on-chain privacy without trusted third parties.

## How It Works

Kompaktor uses WabiSabi's keyed-verification anonymous credentials to break the link between inputs and outputs in a collaborative transaction. The protocol proceeds through four phases:

```
InputRegistration → OutputRegistration → Signing → Broadcasting
```

1. **Input Registration** — Participants register their UTXOs with the coordinator, proving ownership via BIP-322 signatures. The coordinator issues anonymous credentials representing the input value. A randomized pre-registration delay (0-3s) prevents timing-based client fingerprinting.

2. **Output Registration** — Participants use their anonymous credentials (reissued through a binary merge tree for O(n log n) efficiency) to register outputs. The coordinator cannot link which outputs belong to which inputs because the credentials are unlinkable.

3. **Signing** — Each participant signs the jointly constructed transaction. Before signing, clients verify fee transparency (detecting coordinator surplus extraction) and optionally verify other participants' UTXOs via full-node lookups. A "ready to sign" mechanism ensures all participants have completed output registration before signing begins.

4. **Broadcasting** — Once all signatures are collected, the coordinator broadcasts the final transaction to the Bitcoin network.

### Interactive Payments

Kompaktor supports **interactive payments** within coinjoin rounds — a sender can pay a receiver during the round itself, with the payment hidden among all other coinjoin outputs. This is coordinated through a 6-message protocol (P1-P6) between sender and receiver behavior traits, where the sender transfers credential value to the receiver who registers the payment output.

### Security & Privacy Hardening

Kompaktor includes multiple layers of defense against malicious coordinators and passive analysis:

| Feature | Protection |
|---------|-----------|
| **Deterministic round IDs** | Round parameters are hashed to produce IDs — any parameter change is detectable |
| **Input script type validation** | Enforces P2TR/P2WPKH inputs; warns when no Taproot inputs are present (BIP-341 scriptPubKey commitment) |
| **Equivocation detection** | Clients verify round parameters over a separate circuit, detecting coordinator parameter-serving attacks |
| **Coordinator transcript signatures** | BIP-340 Schnorr signatures over round event transcripts provide non-repudiable equivocation proof |
| **Fee transparency audit** | Clients verify fee breakdown before signing — detects coordinator fee extraction attacks |
| **Fresh addresses after failure** | Output scripts disclosed during failed rounds are marked as exposed and never reused |
| **Input cluster memory** | `RoundHistoryTracker` uses union-find connected components to prevent cross-round intersection attacks |
| **Coin selection shuffle** | Fisher-Yates shuffle of coin candidates prevents deterministic selection profiling |
| **UTXO verification** | Clients with full-node access verify other participants' inputs exist in the UTXO set |
| **Timing randomization** | Pre-registration delays and randomized task scheduling prevent client fingerprinting |
| **Tor stream isolation** | `TorCircuitFactory` routes each identity through a separate Tor circuit via SOCKS5 credential-based stream isolation |
| **NativeAOT compatibility** | Full ahead-of-time compilation support for deployment without .NET runtime |

### Credential System

WabiSabi credentials use a 2-in-2-out issuance model: each reissuance takes exactly 2 credential inputs and produces exactly 2 credential outputs. The `DependencyGraph2` preprocessor builds a binary merge tree to efficiently combine credentials from multiple inputs down to the exact output denominations needed, achieving O(n log n) reissuance operations.

## Architecture

```
Kompaktor.sln
├── Kompaktor/              # Core library — protocol logic, models, behaviors
│   ├── Behaviors/          # Pluggable client behavior traits
│   │   ├── InteractivePayments/  # Interactive payment sender/receiver flows
│   │   ├── SelfSendChangeBehaviorTrait.cs
│   │   ├── ConsolidationBehaviorTrait.cs
│   │   ├── StaticPaymentBehaviorTrait.cs
│   │   └── PrivacyAwareCoinSelectionTrait.cs
│   ├── Contracts/          # Interfaces (IKompaktorRoundApi, ICircuit, IKompaktorWalletInterface)
│   ├── Credentials/        # Credential types and reissuance requests
│   ├── Errors/             # Structured error types (KompaktorProtocolException)
│   ├── JsonConverters/     # Custom serializers for GroupElement, Scalar, OutPoint, Money
│   ├── Mapper/             # BlindedCredential, key types
│   ├── Models/             # Round events, configuration, request/response types
│   ├── Prison/             # Abuse prevention — ban system for misbehaving participants
│   └── Utils/              # DependencyGraph2, RetryHelper, TaskScheduler, RoundHistoryTracker
├── Kompaktor.Server/       # ASP.NET Core coordinator server
│   ├── Program.cs          # Server entry point with DI configuration
│   ├── KompaktorEndpoints.cs    # Minimal API route mappings
│   └── KompaktorRoundManager.cs # Multi-round lifecycle management
├── Kompaktor.Blockchain/   # Blockchain abstraction layer
│   ├── IBlockchainBackend.cs           # Interface replacing direct RPCClient usage
│   ├── BitcoinCoreBackend.cs           # RPCClient adapter for Bitcoin Core
│   ├── ElectrumClient.cs              # Stratum JSON-RPC over TCP/SSL
│   ├── ElectrumBackend.cs             # IBlockchainBackend via Electrum protocol
│   └── MultiServerBackend.cs         # Split-server routing across multiple Electrum servers
├── Kompaktor.Wallet/       # HD wallet library with EF Core persistence
│   ├── KompaktorHdWallet.cs           # IKompaktorWalletInterface with BIP-39/84/86
│   ├── MnemonicEncryption.cs          # AES-256-GCM mnemonic encryption
│   └── Data/                          # EF Core entities and WalletDbContext
├── Kompaktor.Wallet.Sample/ # Console app: coordinator + client in one process
├── Kompaktor.Scoring/      # Anonymity scoring, label clustering, coin selection
│   ├── AnonymityScorer.cs              # Per-UTXO anonymity score calculator
│   ├── LabelClusterAnalyzer.cs         # Label propagation and cluster detection
│   ├── CoinSelectionAdvisor.cs         # Privacy-aware coin selection
│   └── CredentialFlowTracker.cs       # Credential lifecycle flow analysis
├── Kompaktor.Client/       # HTTP client for remote coordinator communication
│   ├── KompaktorCoordinatorClient.cs   # High-level entry point: round discovery, status, factory creation
│   ├── HttpKompaktorRoundApi.cs        # IKompaktorRoundApi over HTTP with event polling
│   ├── HttpKompaktorRoundApiFactory.cs # Factory with per-identity circuit isolation
│   └── RemoteKompaktorRound.cs        # Event-polling round state for remote participation
├── Kompaktor.Web/          # Combined coordinator + wallet dashboard
│   ├── Program.cs                     # ASP.NET Core host with coordinator + dashboard APIs
│   └── wwwroot/index.html             # Dark-themed single-page dashboard
└── Kompaktor.Tests/        # Integration tests against regtest bitcoind
```

## Key Components

### `KompaktorRoundOperator`

The coordinator. Manages the round state machine, validates inputs/outputs, issues and verifies WabiSabi credentials, constructs the final transaction, and handles phase timeouts. Extends `KompaktorRound` with full coordinator logic.

### `KompaktorRoundClient`

The participant. Manages credential lifecycle — acquiring credentials via input registration, reissuing through the DependencyGraph2 merge tree, registering outputs, draining leftover credentials, and signing. Orchestrates behavior traits for payments, change, and consolidation.

### `KompaktorRound`

Shared round state (events, inputs, outputs, status) with thread-safe caching. Events are enqueued under a semaphore to prevent TOCTOU races. Derived properties (`Status`, `Inputs`, `Outputs`, `SignatureCount`) are cached and invalidated per event type.

### Behavior Traits

Pluggable client behaviors that compose to define what a participant does in a round:

| Trait | Purpose |
|-------|---------|
| `SelfSendChangeBehaviorTrait` | Registers a change output after all planned outputs are placed. Uses a kill switch to prevent signing until change is registered. |
| `StaticPaymentBehaviorTrait` | Registers predetermined payment outputs. |
| `ConsolidationBehaviorTrait` | Consolidates multiple small UTXOs into fewer larger ones. |
| `InteractivePaymentSenderBehaviorTrait` | Sends payments within the coinjoin round via the P1-P6 interactive protocol. |
| `InteractivePaymentReceiverBehaviorTrait` | Receives interactive payments — waits until output registration is complete before signaling readiness. |
| `PrivacyAwareCoinSelectionTrait` | Prioritizes low-anonymity-score coins for coinjoin, ensuring coins that need mixing are selected first. Takes a `Func<OutPoint, double?>` score lookup to stay decoupled from the scoring library. |

### `IBlockchainBackend`

Abstraction over blockchain data access that replaces the hard `RPCClient` dependency. Two implementations: `BitcoinCoreBackend` (wraps NBitcoin's `RPCClient`) and `ElectrumBackend` (Stratum JSON-RPC over TCP/SSL). The coordinator, wallet, and all tests use this interface — swapping backends requires no code changes.

### `KompaktorHdWallet`

HD wallet implementing `IKompaktorWalletInterface` with BIP-39 mnemonic generation, BIP-84 (P2WPKH) and BIP-86 (P2TR) key derivation, AES-256-GCM mnemonic encryption at rest, and EF Core/SQLite persistence for addresses, UTXOs, transactions, and coinjoin history. Gap limit of 20 addresses per chain with automatic exposed-address tracking for failed rounds.

### `AnonymityScorer`

Per-UTXO anonymity scoring engine. Computes raw anonymity set from coinjoin participant counts with multiplicative composition across rounds. Applies penalties for amount distinguishability (unique/rare output values), label clustering (known external sources like exchanges), and address reuse. Paired with `LabelClusterAnalyzer` (union-find label propagation) and `CoinSelectionAdvisor` (privacy-first, fee-saver, and consolidation strategies with cluster mixing warnings).

### `MultiServerBackend`

Split-server Electrum routing for wallet address privacy. Distributes address subscriptions across multiple independent Electrum servers so no single server sees all wallet addresses. Script-linked operations (subscribe, UTXO queries) are pinned to their assigned server via routing groups. Non-address operations (tx lookup, fee estimation) are load-balanced. Broadcasts go to all servers for redundancy and to prevent timing analysis.

### `CredentialFlowTracker`

Analyzes WabiSabi credential lifecycle events (Acquired → Reissued → Spent) to produce per-payment flow summaries. Walks the credential event DAG from root to leaf, calculating input/output amounts, change, fees, reissuance steps, and merge tree depth. Paired with `CredentialEventEntity` for EF Core persistence.

### `DependencyGraph2`

Binary merge tree for credential reissuance. Given N input credentials and M desired outputs, it builds a DAG of reissuance operations that efficiently combines and splits credential values. Nodes execute concurrently via `Task.Run` with `Interlocked` tracking of in-flight operations.

### `RoundHistoryTracker`

Client-side defense against intersection attacks. Tracks which inputs were co-registered in failed rounds and builds connected components using union-find. When selecting coins for a new round, it ensures at most one coin per previously-observed cluster is registered, limiting the information a malicious coordinator can learn by deliberately failing rounds.

### `KompaktorPrison`

Abuse prevention system. Bans misbehaving coins (failed to sign, double-spend attempts, failed verification) with configurable durations and exponential penalty escalation for repeat offenders. Thread-safe via `ConcurrentDictionary`.

### `ICircuit` / `ICircuitFactory`

Network identity isolation abstraction. `TorCircuitFactory` routes each identity through a separate Tor circuit via SOCKS5 credential-based stream isolation, preventing the coordinator or network observers from linking a participant's input registration to their output registration. `DefaultCircuitFactory` provides a no-op implementation for development/testing.

### `Kompaktor.Web`

Combined coordinator and wallet dashboard in a single ASP.NET Core process. Runs the full coordinator (round management, scheduling) alongside dashboard API endpoints that expose wallet balance, scored UTXOs with anonymity badges, and coinjoin history. Supports both Bitcoin Core RPC and Electrum backends via configuration. The frontend is a vanilla JS single-page dashboard with dark theme, auto-refresh, and color-coded anonymity score indicators.

### Error Handling

`KompaktorProtocolException` with `KompaktorProtocolErrorCode` enum provides structured error types across all protocol operations. `OperationResult<T>` offers a non-throwing alternative for operations where failure is expected.

## Configuration

### Coordinator Options (`KompaktorCoordinatorOptions`)

| Option | Default | Description |
|--------|---------|-------------|
| `FeeRate` | 2 sat/vB | Transaction fee rate |
| `InputTimeout` | 60s | Input registration phase duration |
| `OutputTimeout` | 60s | Output registration phase duration |
| `SigningTimeout` | 60s | Signing phase duration |
| `MinInputCount` / `MaxInputCount` | 1 / 1000 | Input count bounds |
| `MinInputAmount` / `MaxInputAmount` | 10,000 sats / 100 BTC | Input value bounds |
| `MinOutputAmount` / `MaxOutputAmount` | 10,000 sats / 100 BTC | Output value bounds |
| `MaxCredentialValue` | ~43 BTC | Maximum WabiSabi credential value |
| `CredentialCount` | 2 | Credentials per issuance step (k in WabiSabi paper) |
| `UseBulletproofs` | true | Use Bulletproofs++ for range proofs (O(log n) vs O(n), 39% faster at scale) |
| `AllowP2wpkh` / `AllowP2tr` | true | Allowed input/output script types |
| `InputRegistrationSoftTimeout` | null | Optional early transition when minimum inputs met |
| `CoordinatorSigningKeyHex` | null | Persistent BIP-340 signing key for transcript signatures |
| `MaxConcurrentRounds` | 10 | Concurrent round limit |

### Client Options (`KompaktorClientOptions`)

| Option | Default | Description |
|--------|---------|-------------|
| `MaxCoinsPerRound` | 10 | Maximum coins to register per round |
| `AutoConsolidate` | true | Automatically consolidate small UTXOs |
| `ConsolidationThreshold` | 3 | Minimum coins to trigger consolidation |
| `MaxConcurrentInteractiveFlows` | 50 | Concurrent interactive payment limit |
| `ApiCallTimeout` | 30s | Per-request timeout |
| `MaxRetries` | 3 | Retry attempts for transient failures |

### Prison Options (`PrisonOptions`)

| Offense | Default Ban | Description |
|---------|------------|-------------|
| `FailedToSign` | 1 hour | Disrupted round by not signing |
| `FailedToVerify` | 24 hours | Failed ownership proof |
| `DoubleSpend` | 7 days | Registered coin in multiple rounds |
| `RepeatedFailure` | 30 minutes | Repeated registration failures |
| Penalty factor | 2x | Multiplier per repeat offense |
| Max ban | 30 days | Ban duration cap |

## Coordinator Server API

The `Kompaktor.Server` project exposes the coordinator as an HTTP API using ASP.NET Core Minimal APIs:

```
POST /api/round/{roundId}/pre-register-input         # Get a fee quote for input registration
POST /api/round/{roundId}/register-input              # Register a UTXO with ownership proof
POST /api/round/{roundId}/reissue-credentials         # Reissue WabiSabi credentials
POST /api/round/{roundId}/register-output             # Register an output using credentials
POST /api/round/{roundId}/sign                        # Submit a signature
POST /api/round/{roundId}/ready-to-sign               # Signal ready to sign
POST /api/round/{roundId}/send-message                # Send peer-to-peer message
POST /api/round/{roundId}/batch-pre-register-input    # Batch: multiple input quotes in one call
POST /api/round/{roundId}/batch-register-input        # Batch: multiple input registrations
POST /api/round/{roundId}/batch-sign                  # Batch: multiple signatures in one call
POST /api/round/{roundId}/batch-ready-to-sign         # Batch: signal readiness for multiple inputs
GET  /api/rounds                                      # List active rounds
GET  /api/round/{roundId}/info                        # Get round parameters
GET  /api/round/{roundId}/status                      # Get round status with event stream
GET  /health                                          # Coordinator health check
GET  /openapi/v1.json                                 # OpenAPI specification
```

Batch endpoints return per-item results (success/failure) so partial failures don't reject the entire batch. The client uses batch signing and batch ready-to-sign internally — batch input registration is available for consolidation scenarios but the default client uses per-coin circuits for privacy.

### Server Configuration

The coordinator reads configuration from `appsettings.json`, environment variables, or CLI args:

```json
{
  "Bitcoin": {
    "Network": "regtest",
    "RpcUri": "http://localhost:53782",
    "RpcUser": "ceiwHEbqWI83",
    "RpcPassword": "DwubwWsoo3"
  },
  "Kompaktor": {
    "CoordinatorSigningKeyHex": "...",
    "MinInputCount": 2,
    "FeeRate": 2
  }
}
```

**Production checklist:**
- Set `Bitcoin:Network` to `main` or `testnet` (defaults to `regtest`)
- Configure a persistent `CoordinatorSigningKeyHex` for transcript signature continuity
- Place behind a reverse proxy with TLS termination
- HTTP rate limiting is enabled by default (200 req/min protocol, 60 req/min discovery per IP)

### Client Integration

External wallets integrate by referencing `Kompaktor.Client`:

```csharp
// 1. Connect to coordinator with Tor privacy
var tor = new TorCircuitFactory(new TorOptions { SocksPort = 9050 });
var coordinator = new KompaktorCoordinatorClient(
    new Uri("http://coordinator.onion"), tor);

// 2. Discover rounds and get parameters
var rounds = await coordinator.GetActiveRoundsAsync();
var info = await coordinator.GetRoundInfoAsync(rounds[0]);

// 3. Create event-synced round from remote coordinator
var factory = coordinator.CreateRoundApiFactory(rounds[0]);
var api = (HttpKompaktorRoundApi)factory.Create();
var round = new RemoteKompaktorRound(api, pollInterval: TimeSpan.FromSeconds(1));
await round.StartPollingAsync(cts.Token);

// 4. Join round with wallet
var client = new KompaktorRoundClient(
    SecureRandom.Instance, network, round, factory,
    behaviorTraits, wallet, logger);
await client.PhasesTask; // Runs through all phases
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/) (for running regtest bitcoind in tests)

## Getting Started

### 1. Clone and Build

```bash
git clone <repo-url>
cd Kompaktor
dotnet restore
dotnet build
```

### 2. Start Regtest Bitcoin Node

```bash
docker compose up -d
```

This starts a `bitcoind` regtest node on port 53782 with RPC credentials `ceiwHEbqWI83:DwubwWsoo3`.

### 3. Run Tests

```bash
dotnet test
```

The test suite includes 310+ tests covering:
- Round lifecycle (input registration, output registration, signing, broadcasting)
- Multi-participant coinjoins (up to 100 participants)
- Interactive payments between participants during rounds
- Interactive payments at scale (200 senders)
- Credential reissuance via DependencyGraph2
- Edge cases (double registration, wrong phase, insufficient funds)
- Equivocation detection across round parameter fields and credential issuers
- BIP-340 transcript signature creation, verification, and tamper detection
- Fee transparency audit with surplus detection and dust thresholds
- Input cluster memory with union-find connected components
- Coin selection Fisher-Yates shuffle distribution and determinism
- P2TR/P2WPKH input script type validation
- Timing randomization delay formulas and bounds
- UTXO verification interface for fabricated input detection
- Fresh address tracking after failed rounds
- Deterministic round ID hashing with all parameter coverage
- EF Core wallet database model with unique UTXO constraints and coinjoin tracking
- AES-256-GCM mnemonic encryption round-trip and wrong-passphrase rejection
- HD wallet key derivation (BIP-84/86), address generation, coin queries
- Electrum Stratum JSON-RPC framing, request correlation, and notification dispatch
- Anonymity scoring with multiplicative composition, amount penalties, and label penalties
- Label cluster analysis with union-find grouping and external source detection
- Privacy-aware coin selection with cluster mixing warnings
- Multi-server Electrum routing with round-robin assignment and script pinning
- Credential lifecycle flow analysis with merge tree depth and fee calculation

### 4. Run the Coordinator Server

```bash
cd Kompaktor.Server
dotnet run
```

The server starts on the default ASP.NET Core port and creates an initial round on startup. Configure via `appsettings.json`:

```json
{
  "Kompaktor": {
    "FeeRate": 2,
    "InputTimeout": "00:01:00",
    "OutputTimeout": "00:01:00",
    "SigningTimeout": "00:01:00"
  },
  "Bitcoin": {
    "RpcUri": "http://localhost:53782",
    "RpcUser": "ceiwHEbqWI83",
    "RpcPassword": "DwubwWsoo3"
  }
}
```

### 5. Run the Dashboard (Coordinator + Wallet)

```bash
cd Kompaktor.Web
dotnet run
```

This starts a combined coordinator and wallet dashboard. The web UI at the root URL shows wallet balance, anonymity-scored UTXOs, active rounds, and coinjoin history. The `/api/dashboard/credential-flows/{roundId}` endpoint exposes per-payment credential flow analysis. Supports Electrum as an alternative backend:

```json
{
  "Electrum": {
    "Host": "127.0.0.1",
    "Port": 50001,
    "UseSsl": false
  }
}
```

For split-server privacy routing (no single Electrum server sees all wallet addresses):

```json
{
  "Electrum": {
    "Servers": [
      { "Name": "Server1", "Host": "electrum1.example.com", "Port": 50002, "UseSsl": true },
      { "Name": "Server2", "Host": "electrum2.example.com", "Port": 50002, "UseSsl": true }
    ],
    "RoutingStrategy": "RoundRobin"
  }
}
```

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| [WabiSabi](https://github.com/WabiSabiProject/WabiSabi) | 1.0.1.2 | Anonymous credential system |
| [NBitcoin](https://github.com/MetacoSA/NBitcoin) | 7.0.46 | Bitcoin primitives, transaction construction, RPC |
| [NBitcoin.Secp256k1](https://github.com/MetacoSA/NBitcoin) | 3.1.6 | Elliptic curve cryptography |

## CI

GitHub Actions CI runs on push/PR to `master`:
- **Build** — `dotnet build --configuration Release`
- **NativeAOT** — Verifies ahead-of-time compilation (`dotnet publish -r linux-x64`)
- **Unit tests** — Tests filtered by `Category!=Integration`
- **Integration tests** — Full suite against a regtest bitcoind service container

## License

See [LICENSE](LICENSE) for details.
