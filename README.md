# Kompaktor

A Bitcoin privacy protocol implementing collaborative transactions (coinjoins) using the [WabiSabi](https://eprint.iacr.org/2021/206) anonymous credential system. Kompaktor enables multiple participants to construct a joint transaction where inputs and outputs cannot be linked, providing strong on-chain privacy without trusted third parties.

## How It Works

Kompaktor uses WabiSabi's keyed-verification anonymous credentials to break the link between inputs and outputs in a collaborative transaction. The protocol proceeds through four phases:

```
InputRegistration → OutputRegistration → Signing → Broadcasting
```

1. **Input Registration** — Participants register their UTXOs with the coordinator, proving ownership via BIP-322 signatures. The coordinator issues anonymous credentials representing the input value.

2. **Output Registration** — Participants use their anonymous credentials (reissued through a binary merge tree for O(n log n) efficiency) to register outputs. The coordinator cannot link which outputs belong to which inputs because the credentials are unlinkable.

3. **Signing** — Each participant signs the jointly constructed transaction. A "ready to sign" mechanism ensures all participants have completed output registration before signing begins.

4. **Broadcasting** — Once all signatures are collected, the coordinator broadcasts the final transaction to the Bitcoin network.

### Interactive Payments

Kompaktor supports **interactive payments** within coinjoin rounds — a sender can pay a receiver during the round itself, with the payment hidden among all other coinjoin outputs. This is coordinated through a 6-message protocol (P1-P6) between sender and receiver behavior traits, where the sender transfers credential value to the receiver who registers the payment output.

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
│   │   └── StaticPaymentBehaviorTrait.cs
│   ├── Contracts/          # Interfaces (IKompaktorRoundApi, ICircuit, IKompaktorWalletInterface)
│   ├── Credentials/        # Credential types and reissuance requests
│   ├── Errors/             # Structured error types (KompaktorProtocolException)
│   ├── JsonConverters/     # Custom serializers for GroupElement, Scalar, OutPoint, Money
│   ├── Mapper/             # BlindedCredential, key types
│   ├── Models/             # Round events, configuration, request/response types
│   ├── Prison/             # Abuse prevention — ban system for misbehaving participants
│   └── Utils/              # DependencyGraph2, RetryHelper, TaskScheduler, TaskUtils
├── Kompaktor.Server/       # ASP.NET Core coordinator server
│   ├── Program.cs          # Server entry point with DI configuration
│   ├── KompaktorEndpoints.cs    # Minimal API route mappings
│   └── KompaktorRoundManager.cs # Multi-round lifecycle management
├── Kompaktor.Client/       # HTTP client for remote coordinator communication
│   ├── HttpKompaktorRoundApi.cs        # IKompaktorRoundApi over HTTP
│   └── HttpKompaktorRoundApiFactory.cs # Factory with per-identity circuit isolation
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

### `DependencyGraph2`

Binary merge tree for credential reissuance. Given N input credentials and M desired outputs, it builds a DAG of reissuance operations that efficiently combines and splits credential values. Nodes execute concurrently via `Task.Run` with `Interlocked` tracking of in-flight operations.

### `KompaktorPrison`

Abuse prevention system. Bans misbehaving coins (failed to sign, double-spend attempts, failed verification) with configurable durations and exponential penalty escalation for repeat offenders. Thread-safe via `ConcurrentDictionary`.

### `ICircuit` / `ICircuitFactory`

Network identity isolation abstraction. In production, each circuit maps to a separate Tor circuit, preventing the coordinator or network observers from linking a participant's input registration to their output registration. `DefaultCircuitFactory` provides a no-op implementation for development/testing.

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
POST /api/round/{roundId}/pre-register-input   # Get a fee quote for input registration
POST /api/round/{roundId}/register-input        # Register a UTXO with ownership proof
POST /api/round/{roundId}/reissue-credentials   # Reissue WabiSabi credentials
POST /api/round/{roundId}/register-output       # Register an output using credentials
POST /api/round/{roundId}/sign                  # Submit a signature
POST /api/round/{roundId}/ready-to-sign         # Signal ready to sign
POST /api/round/{roundId}/send-message          # Send peer-to-peer message
GET  /api/rounds                                # List active rounds
GET  /api/round/{roundId}/status                # Get round status
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

The test suite includes 52 tests covering:
- Round lifecycle (input registration, output registration, signing, broadcasting)
- Multi-participant coinjoins (up to 100 participants)
- Interactive payments between participants during rounds
- Interactive payments at scale (200 senders)
- Credential reissuance via DependencyGraph2
- Edge cases (double registration, wrong phase, insufficient funds)

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

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| [WabiSabi](https://github.com/WabiSabiProject/WabiSabi) | 1.0.1.2 | Anonymous credential system |
| [NBitcoin](https://github.com/MetacoSA/NBitcoin) | 7.0.46 | Bitcoin primitives, transaction construction, RPC |
| [NBitcoin.Secp256k1](https://github.com/MetacoSA/NBitcoin) | 3.1.6 | Elliptic curve cryptography |

## CI

GitHub Actions CI runs on push/PR to `master`:
- **Build** — `dotnet build --configuration Release`
- **Unit tests** — Tests filtered by `Category!=Integration`
- **Integration tests** — Full suite against a regtest bitcoind service container

## License

See [LICENSE](LICENSE) for details.
