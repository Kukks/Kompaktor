# Credential Tracking — Sub-Project E Design Spec

**Date:** 2026-04-17
**Scope:** Credential lifecycle tracking, reissuance event persistence, payment flow visualization data
**Out of scope:** Web UI (C)

## Goal

Track the lifecycle of WabiSabi credentials through coinjoin rounds — from acquisition (input registration) through reissuance (DependencyGraph2 merge tree) to spending (output registration). This provides the wallet with a complete provenance chain showing how input values were merged, split, and recombined into output values, enabling users to understand how their payments were "compacted" through the credential system.

## Background

WabiSabi credentials represent value amounts with algebraic MACs. Each credential has:
- A **value** (amount in satoshis)
- A **randomness** (Pedersen blinding factor)
- A **MAC** (algebraic message authentication code, uniquely identifiable by `Serial()`)

The credential lifecycle in a round:
1. **Acquire**: Input registration produces credentials representing input values
2. **Reissue**: DependencyGraph2 merges/splits credentials through a binary tree of reissuance operations
3. **Spend**: Output registration consumes credentials to register outputs

Currently, this data is ephemeral — it exists only during a round and is discarded after. Sub-Project E persists it.

## Architecture

### New Types

| Type | Location | Purpose |
|------|----------|---------|
| `CredentialEvent` | `Kompaktor.Wallet.Data` | EF Core entity for credential lifecycle events |
| `CredentialFlowTracker` | `Kompaktor.Scoring` | Analyzes credential flow to produce per-payment provenance |
| `PaymentFlow` | `Kompaktor.Scoring` | Record describing how a payment's value moved through reissuance |

### Modified Types

| Type | Change |
|------|--------|
| `WalletDbContext` | Add `CredentialEvents` DbSet |
| `CoinJoinRecordEntity` | Already has the round metadata we need |

## 1. CredentialEvent Entity

Records each credential lifecycle event. One row per credential state transition.

```csharp
public class CredentialEventEntity
{
    public int Id { get; set; }
    public int CoinJoinRecordId { get; set; }
    public string CredentialSerial { get; set; } = "";  // MAC.Serial() hex
    public string EventType { get; set; } = "";         // "Acquired", "Reissued", "Spent"
    public long AmountSat { get; set; }
    public int? ParentEventId { get; set; }             // Links reissued → source credential
    public int? OutputUtxoId { get; set; }              // Links spent → output UTXO
    public int GraphDepth { get; set; }                 // Depth in the DependencyGraph2 merge tree
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public CoinJoinRecordEntity CoinJoinRecord { get; set; } = null!;
    public CredentialEventEntity? ParentEvent { get; set; }
    public UtxoEntity? OutputUtxo { get; set; }
}
```

### Event Types

| EventType | Meaning | ParentEventId | OutputUtxoId |
|-----------|---------|---------------|--------------|
| `Acquired` | Credential created via input registration | null | null |
| `Reissued` | Credential created via reissuance (merge/split) | Points to source | null |
| `Spent` | Credential consumed to register an output | Points to source | Points to output UTXO |

### Example Flow

A coin with 1 BTC enters a round with 10 participants, gets merged with zero-credentials, and produces a 0.8 BTC output + 0.2 BTC change:

```
CredentialEvent(Acquired, 1.0 BTC, depth=0)
  → CredentialEvent(Reissued, 1.0 BTC, depth=1, parent=above)
    → CredentialEvent(Reissued, 0.8 BTC, depth=2, parent=above)
      → CredentialEvent(Spent, 0.8 BTC, depth=3, parent=above, output=payment_utxo)
    → CredentialEvent(Reissued, 0.2 BTC, depth=2, parent=above)
      → CredentialEvent(Spent, 0.2 BTC, depth=3, parent=above, output=change_utxo)
```

## 2. CredentialFlowTracker

Stateless analyzer that takes a round's credential events and produces human-readable payment flow summaries.

```csharp
public class CredentialFlowTracker
{
    /// Given all credential events for a round, produces per-payment flow summaries.
    public List<PaymentFlow> AnalyzeFlows(IReadOnlyList<CredentialEventEntity> events);
}

public record PaymentFlow(
    long InputAmountSat,          // Total input amount
    long OutputAmountSat,         // Payment output amount
    long ChangeAmountSat,         // Change amount
    long FeeSat,                  // Fee portion
    int ReissuanceSteps,          // Number of reissuance hops
    int GraphDepth,               // Max depth in the merge tree
    string? OutputLabel            // Label on the payment output, if any
);
```

### Analysis Logic

1. Walk the event DAG from `Acquired` events (roots)
2. Trace through `Reissued` events (intermediate nodes)
3. Terminate at `Spent` events (leaves with output UTXOs)
4. Group by input source → output destination to produce per-payment summaries
5. Calculate fee as `InputAmountSat - OutputAmountSat - ChangeAmountSat`

## 3. Wallet Integration

After a successful coinjoin round, the wallet records credential events:

```csharp
// In KompaktorHdWallet or called by round completion handler:
public async Task RecordCredentialFlow(
    int coinJoinRecordId,
    IReadOnlyList<CredentialEventEntity> events)
{
    _db.CredentialEvents.AddRange(events);
    await _db.SaveChangesAsync();
}
```

The credential events are constructed from the `KompaktorIdentity` data that's available at round completion:
- `identity.CreatedCredentials` → `Acquired` events
- DependencyGraph2 node trace → `Reissued` events
- `identity.SpentCredentials` with output mapping → `Spent` events

## 4. Testing Strategy

| Test Category | What to Test |
|--------------|-------------|
| `CredentialFlowTrackerTests` | Single-input single-output flow, split flow (payment + change), multi-depth merge tree, fee calculation |
| `CredentialEventEntityTests` | EF Core persistence round-trip, parent-child relationships |

All pure unit tests using in-memory entity collections.

## 5. Schema Addition

In `WalletDbContext.OnModelCreating`:
```csharp
modelBuilder.Entity<CredentialEventEntity>(e =>
{
    e.HasKey(ce => ce.Id);
    e.HasOne(ce => ce.CoinJoinRecord).WithMany().HasForeignKey(ce => ce.CoinJoinRecordId);
    e.HasOne(ce => ce.ParentEvent).WithMany().HasForeignKey(ce => ce.ParentEventId);
    e.HasOne(ce => ce.OutputUtxo).WithMany().HasForeignKey(ce => ce.OutputUtxoId);
    e.HasIndex(ce => ce.CoinJoinRecordId);
    e.HasIndex(ce => ce.CredentialSerial);
});
```
