# Anonymity Scoring Engine — Sub-Project B Design Spec

**Date:** 2026-04-17
**Scope:** Per-UTXO anonymity scores, label-based cluster analysis, coinjoin history tracking
**Out of scope:** Split-server routing (D), credential tracking (E), Web UI (C)

## Goal

Build a library (`Kompaktor.Scoring`) that assigns each wallet UTXO a quantified anonymity score based on its coinjoin participation history, label cluster analysis, and amount-based distinguishability. The scoring engine integrates with the existing EF Core wallet persistence and the `CoinJoinRecordEntity` / `LabelEntity` data model from Sub-Project A.

## Background: WabiSabi Anonymity Model

Unlike traditional equal-output coinjoins (e.g., Wasabi 1.x), Kompaktor uses WabiSabi credentials where outputs can have arbitrary values. The anonymity guarantee comes from the credential system breaking the input-output link — the coordinator cannot determine which output belongs to which input.

This means:
- **Anonymity set per output** ≈ number of participants in the round (not equal-value outputs)
- **Amount analysis** can narrow the set if an output value is unique or rare among all outputs
- **Cross-round analysis** compounds: a coin that has passed through multiple rounds has a multiplicatively larger anonymity set
- **Labels** (send/receive annotations) create clusters that reduce effective anonymity when external observers know certain addresses belong to the same entity

## Architecture

### New Project

| Project | Purpose | Dependencies |
|---------|---------|-------------|
| `Kompaktor.Scoring` | Anonymity scoring, label clustering, coin selection advisor | Kompaktor.Wallet (EF Core entities) |

### Key Types

| Type | Responsibility |
|------|---------------|
| `AnonymityScore` | Value object: raw anon-set size, effective score, confidence level |
| `AnonymityScorer` | Stateless calculator: takes a UTXO + its coinjoin history → score |
| `LabelClusterAnalyzer` | Groups UTXOs by label propagation to detect cluster leaks |
| `CoinSelectionAdvisor` | Recommends which coins to spend together to minimize privacy loss |

## 1. AnonymityScore Value Object

```csharp
public readonly record struct AnonymityScore(
    int RawAnonSet,        // Number of participants in the round (or 1 for non-coinjoin coins)
    double EffectiveScore,  // Adjusted score after amount analysis and label clustering
    int CoinJoinCount,     // How many coinjoin rounds this coin has passed through
    ConfidenceLevel Confidence // High, Medium, Low — based on data completeness
);

public enum ConfidenceLevel { High, Medium, Low }
```

**Interpretation:**
- `RawAnonSet = 1` → virgin coin, never coinjoined (e.g., direct receive from exchange)
- `RawAnonSet = N` → the coin was an output of a round with N participants
- `EffectiveScore` applies penalties for amount distinguishability, label clustering, and address reuse
- `CoinJoinCount` tracks how many rounds the coin's lineage has passed through

## 2. AnonymityScorer

The core scoring algorithm. Stateless — receives all data it needs via parameters.

### Inputs

For a given UTXO, the scorer needs:
1. **CoinJoin participation records** — which rounds the UTXO (and its ancestors) participated in
2. **Round metadata** — participant count, total input/output counts, output value distribution
3. **Label set** — all labels attached to the UTXO, its address, and its transaction
4. **Ancestor chain** — if this UTXO was created as output of a coinjoin whose input was also a coinjoin output, we walk the chain

### Algorithm

```
Score(utxo):
  if utxo has no coinjoin history:
    return AnonymityScore(RawAnonSet=1, EffectiveScore=1.0, CoinJoinCount=0, High)

  // Walk ancestor chain
  chain = BuildAncestorChain(utxo)  // list of CoinJoinRecords from oldest to newest

  rawAnonSet = 1
  for each round in chain:
    participantCount = round.TotalInputCount  // each input = one participant identity
    rawAnonSet = rawAnonSet * participantCount  // multiplicative composition

  // Cap to prevent unrealistic scores
  rawAnonSet = min(rawAnonSet, 10_000)

  // Amount distinguishability penalty
  amountPenalty = ComputeAmountPenalty(utxo, latestRound)

  // Label cluster penalty
  clusterPenalty = ComputeClusterPenalty(utxo, labels)

  // Address reuse penalty
  reusePenalty = utxo.Address.IsUsed && HasMultipleTransactions(utxo.Address) ? 0.5 : 1.0

  effectiveScore = rawAnonSet * amountPenalty * clusterPenalty * reusePenalty

  return AnonymityScore(rawAnonSet, effectiveScore, chain.Count, confidence)
```

### Amount Distinguishability Penalty

When a UTXO's value is unique among all outputs in its coinjoin round, an observer can trivially link it to its input. The penalty is:

```
ComputeAmountPenalty(utxo, round):
  outputValues = round.AllOutputValues  // all output amounts in the round
  matchingOutputs = outputValues.Count(v => v == utxo.Amount)

  if matchingOutputs == 1:
    return 0.1  // severe penalty — unique amount, nearly identifiable
  elif matchingOutputs <= 3:
    return 0.5  // moderate — small anonymity set within the round
  else:
    return 1.0  // no penalty — good amount coverage
```

Note: With WabiSabi's arbitrary amounts, exact matches are rare. We use a **bucket tolerance** (default ±1%) to group "similar enough" amounts, since blockchain observers use the same heuristic.

### Label Cluster Penalty

Labels propagate: if UTXO A has label "Coinbase withdrawal" and UTXO A is coinjoined with UTXO B to produce UTXO C, then UTXO C inherits the cluster association. When a cluster has external-facing labels (e.g., exchange names, KYC sources), the effective anonymity decreases.

```
ComputeClusterPenalty(utxo, labels):
  if no labels on utxo or its ancestors:
    return 1.0  // no known associations

  externalLabels = labels.Where(l => l.IsExternalSource)  // "Exchange", "KYC", etc.
  if externalLabels.Any():
    return 0.3  // known source significantly reduces effective anonymity
  else:
    return 0.8  // internal labels have mild impact
```

## 3. LabelClusterAnalyzer

Implements label propagation across coinjoin boundaries to detect when coins that appear anonymous are actually clustered.

### Label Propagation Rules

1. **Input clustering**: All inputs to a non-coinjoin transaction are assumed owned by the same entity (common-input-ownership heuristic). Labels propagate across all inputs.
2. **Coinjoin breaks clustering**: Inputs in a coinjoin round do NOT cluster with other participants' inputs — the whole point of coinjoin.
3. **Change detection**: Our own change outputs inherit the label cluster of our inputs (since we know they're ours).
4. **External labels**: Labels like "Exchange:Coinbase", "P2P:Alice" mark UTXOs with known external associations. These propagate through change outputs.

### Interface

```csharp
public class LabelClusterAnalyzer
{
    /// Returns groups of UTXOs that share a label cluster.
    /// UTXOs in the same cluster should not be spent together with
    /// unlabeled (anonymous) coins, as it re-links them.
    public List<LabelCluster> AnalyzeClusters(
        IReadOnlyList<UtxoEntity> utxos,
        IReadOnlyList<LabelEntity> labels,
        IReadOnlyList<CoinJoinRecordEntity> coinjoins);
}

public record LabelCluster(
    HashSet<int> UtxoIds,      // UtxoEntity.Id values in this cluster
    HashSet<string> Labels,     // Propagated label texts
    bool HasExternalSource      // Whether any label indicates a known external entity
);
```

## 4. CoinSelectionAdvisor

Recommends which coins to spend together (or register together in a coinjoin) to minimize privacy loss. Builds on `AnonymityScorer` and `LabelClusterAnalyzer`.

### Rules

1. **Don't mix clusters**: Never spend a labeled coin alongside an unlabeled (anonymous) coin — it taints the anonymous coin by association.
2. **Prefer high-anon coins**: When multiple coins can satisfy a payment, prefer those with higher effective scores.
3. **Consolidation threshold**: Only consolidate coins within the same label cluster.
4. **Coinjoin priority**: Coins with `EffectiveScore < threshold` (configurable, default 5.0) should be prioritized for coinjoin registration.

### Interface

```csharp
public class CoinSelectionAdvisor
{
    /// Given available coins and a target amount, returns a recommended
    /// selection that minimizes privacy loss.
    public CoinSelectionResult SelectCoins(
        IReadOnlyList<ScoredUtxo> availableCoins,
        Money targetAmount,
        CoinSelectionStrategy strategy = CoinSelectionStrategy.PrivacyFirst);

    /// Returns coins that should be prioritized for the next coinjoin round.
    public IReadOnlyList<ScoredUtxo> GetCoinjoinCandidates(
        IReadOnlyList<ScoredUtxo> allCoins,
        double minEffectiveScore = 5.0);
}

public record ScoredUtxo(UtxoEntity Utxo, AnonymityScore Score);

public record CoinSelectionResult(
    IReadOnlyList<ScoredUtxo> Selected,
    Money TotalAmount,
    string[] Warnings  // e.g., "Mixing labeled and unlabeled coins"
);

public enum CoinSelectionStrategy
{
    PrivacyFirst,     // Maximize anonymity preservation
    FeeSaver,         // Minimize transaction size (may sacrifice some privacy)
    Consolidation     // Consolidate same-cluster coins
}
```

## 5. Ancestor Chain Tracking

To compute compound anonymity scores, we need to walk the UTXO's coinjoin ancestry. The existing `CoinJoinParticipationEntity` with Role "Input"/"Output" already provides this: given an output UTXO, find its CoinJoinRecord, then find the input UTXOs of that record, and recurse.

### Data Flow

```
UTXO (current) → CoinJoinParticipation(Role=Output) → CoinJoinRecord
    → CoinJoinParticipation(Role=Input) → UTXO (ancestor)
        → CoinJoinParticipation(Role=Output) → ... (recurse)
```

The walker stops when:
- A UTXO has no CoinJoinParticipation with Role=Output (it's a virgin coin)
- Max depth reached (configurable, default 10 rounds deep)

### Persistence

The existing schema is sufficient. We add one new column to `CoinJoinRecordEntity`:

```csharp
// Add to existing CoinJoinRecordEntity:
public int ParticipantCount { get; set; }  // Distinct participant identities in the round
```

This disambiguates `TotalInputCount` (which may include multiple inputs from the same participant) from the actual number of distinct participants, which is the true anonymity set bound.

## 6. Integration with KompaktorHdWallet

The wallet exposes scoring through a new method:

```csharp
// In KompaktorHdWallet:
public async Task<ScoredUtxo[]> GetScoredCoins()
{
    var coins = await GetCoinsWithHistory();  // coins + their coinjoin records + labels
    var scorer = new AnonymityScorer();
    return coins.Select(c => new ScoredUtxo(c.Utxo, scorer.Score(c))).ToArray();
}
```

The `KompaktorRoundClient` records coinjoin participation after each completed round:

```csharp
// After successful round completion:
wallet.RecordCoinJoin(roundId, txId, ourInputUtxos, ourOutputUtxos,
    round.Inputs.Count, round.Outputs.Count, participantCount);
```

## 7. Testing Strategy

| Test Category | What to Test |
|--------------|-------------|
| `AnonymityScorerTests` | Virgin coin = score 1, single round = participant count, multi-round = multiplicative, amount penalty cases, cap at 10K |
| `LabelClusterTests` | Cluster propagation, coinjoin breaks clusters, change inherits labels, external source detection |
| `CoinSelectionAdvisorTests` | Don't mix clusters, prefer high-anon, consolidation within cluster, privacy-first vs fee-saver |
| `AncestorChainTests` | Chain walking, depth limit, missing records handled gracefully |

All tests are pure unit tests — no blockchain or database needed. The scorer and analyzer operate on in-memory entity collections.

## 8. Configuration

```csharp
public class ScoringOptions
{
    public int MaxAncestorDepth { get; set; } = 10;
    public int MaxRawAnonSet { get; set; } = 10_000;
    public double AmountBucketTolerance { get; set; } = 0.01; // ±1%
    public double MinEffectiveScoreForCoinjoin { get; set; } = 5.0;
    public double ExternalLabelPenalty { get; set; } = 0.3;
    public double InternalLabelPenalty { get; set; } = 0.8;
    public double UniqueAmountPenalty { get; set; } = 0.1;
    public double RareAmountPenalty { get; set; } = 0.5;
    public int RareAmountThreshold { get; set; } = 3;
    public double AddressReusePenalty { get; set; } = 0.5;
}
```
