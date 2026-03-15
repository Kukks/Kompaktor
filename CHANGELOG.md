# Changelog

## [Unreleased] - 2026-03-13

### Added
- **Structured error types** — `KompaktorProtocolException` with `KompaktorProtocolErrorCode` enum replacing bare `InvalidOperationException` throws throughout the codebase
- **`OperationResult<T>`** — Result type for non-throwing error paths
- **Configuration system** — `KompaktorCoordinatorOptions` and `KompaktorClientOptions` for extracting magic numbers into configurable parameters
- **Network identity isolation** — `ICircuit` / `ICircuitFactory` abstraction layer for pluggable Tor circuit isolation (DefaultCircuitFactory for development)
- **Abuse prevention** — `KompaktorPrison` with configurable ban durations, penalty factors, and support for ban reasons (FailedToSign, DoubleSpend, etc.)
- **Retry helper** — `RetryHelper.ExecuteWithRetryAsync` with exponential backoff + jitter for transient failures
- **GitHub Actions CI** — CI workflow with build, unit tests, and integration tests against regtest bitcoind
- **Build infrastructure** — `Directory.Build.props` for shared project properties

### Changed
- **Upgraded to .NET 10** — Updated all projects from .NET 8 to .NET 10, updated test infrastructure packages
- **Fixed `AddEvent` concurrency** — Event enqueue now acquires `_lock` semaphore, eliminating TOCTOU race between event addition and `Events.ToArray()` reads
- **Added caching to `KompaktorRound`** — `Status`, `Inputs`, `Outputs`, `RoundEventCreated`, and `SignatureCount` are now cached with targeted invalidation per event type, turning O(n) LINQ scans into O(1) reads
- **Fixed fire-and-forget timeout handlers** — Replaced `Task.Delay().ContinueWith()` with proper `Task.Run(async () => { try/catch })` pattern in `HandleStatusChange`, preventing silent exception swallowing
- **Fixed output registration race** — Added `_outputRegistrationSemaphore` to serialize credential issuance in `RegisterOutput`, preventing credential balance overdraw under concurrent access
- **Removed `PublishAot`** — AOT compilation is incompatible with WabiSabi's reflection-based serialization

### Fixed
- **CS0161 build error** — `GetEvents` method had empty body; now throws `NotImplementedException`
- **Unreachable code** — Removed `return (pair.Key, null)` after `throw` in `RegisterOutput`
- **Missing xunit runner** — Added `xunit.runner.visualstudio` package for test discovery on .NET 10
