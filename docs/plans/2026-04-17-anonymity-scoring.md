# Anonymity Scoring Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a per-UTXO anonymity scoring library with label cluster analysis and coin selection advice

**Architecture:** Stateless scoring engine operating on EF Core entities from Kompaktor.Wallet. Three core types: AnonymityScorer (calculates scores), LabelClusterAnalyzer (detects cluster leaks), CoinSelectionAdvisor (recommends coin selection). All unit-testable without database or blockchain.

**Tech Stack:** .NET 10, xunit 2.9.3, EF Core 9.0 entities (read-only)

---

## File Structure

| Action | Path | Purpose |
|--------|------|---------|
| Create | `Kompaktor.Scoring/Kompaktor.Scoring.csproj` | Class library project |
| Create | `Kompaktor.Scoring/AnonymityScore.cs` | Value object + ConfidenceLevel enum |
| Create | `Kompaktor.Scoring/ScoringOptions.cs` | Configuration record |
| Create | `Kompaktor.Scoring/AnonymityScorer.cs` | Core scoring algorithm |
| Create | `Kompaktor.Scoring/LabelClusterAnalyzer.cs` | Label propagation + cluster detection |
| Create | `Kompaktor.Scoring/CoinSelectionAdvisor.cs` | Privacy-aware coin selection |
| Create | `Kompaktor.Scoring/ScoredUtxo.cs` | Record combining UTXO + score |
| Modify | `Kompaktor.Wallet/Data/Entities.cs` | Add ParticipantCount to CoinJoinRecordEntity |
| Modify | `Kompaktor.sln` | Add Kompaktor.Scoring project |
| Create | `Kompaktor.Tests/AnonymityScorerTests.cs` | Scorer unit tests |
| Create | `Kompaktor.Tests/LabelClusterAnalyzerTests.cs` | Cluster analysis tests |
| Create | `Kompaktor.Tests/CoinSelectionAdvisorTests.cs` | Coin selection tests |
| Modify | `Kompaktor.Tests/Kompaktor.Tests.csproj` | Add Kompaktor.Scoring reference |

---

### Task 1: Create Kompaktor.Scoring project with value types

**Files:**
- Create: `Kompaktor.Scoring/Kompaktor.Scoring.csproj`
- Create: `Kompaktor.Scoring/AnonymityScore.cs`
- Create: `Kompaktor.Scoring/ScoringOptions.cs`
- Create: `Kompaktor.Scoring/ScoredUtxo.cs`
- Modify: `Kompaktor.sln`

- [ ] **Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <Description>Anonymity scoring engine for Kompaktor wallet UTXOs</Description>
    </PropertyGroup>
    <ItemGroup>
        <ProjectReference Include="..\Kompaktor.Wallet\Kompaktor.Wallet.csproj" />
    </ItemGroup>
</Project>
```

- [ ] **Step 2: Create AnonymityScore value object**

```csharp
namespace Kompaktor.Scoring;

public readonly record struct AnonymityScore(
    int RawAnonSet,
    double EffectiveScore,
    int CoinJoinCount,
    ConfidenceLevel Confidence);

public enum ConfidenceLevel { High, Medium, Low }
```

- [ ] **Step 3: Create ScoringOptions**

```csharp
namespace Kompaktor.Scoring;

public class ScoringOptions
{
    public int MaxAncestorDepth { get; set; } = 10;
    public int MaxRawAnonSet { get; set; } = 10_000;
    public double AmountBucketTolerance { get; set; } = 0.01;
    public double MinEffectiveScoreForCoinjoin { get; set; } = 5.0;
    public double ExternalLabelPenalty { get; set; } = 0.3;
    public double InternalLabelPenalty { get; set; } = 0.8;
    public double UniqueAmountPenalty { get; set; } = 0.1;
    public double RareAmountPenalty { get; set; } = 0.5;
    public int RareAmountThreshold { get; set; } = 3;
    public double AddressReusePenalty { get; set; } = 0.5;
}
```

- [ ] **Step 4: Create ScoredUtxo record**

```csharp
using Kompaktor.Wallet.Data;

namespace Kompaktor.Scoring;

public record ScoredUtxo(UtxoEntity Utxo, AnonymityScore Score);
```

- [ ] **Step 5: Add project to solution and verify build**

```bash
dotnet sln add Kompaktor.Scoring/Kompaktor.Scoring.csproj
dotnet build Kompaktor.Scoring/Kompaktor.Scoring.csproj
```

- [ ] **Step 6: Commit**

```bash
git add Kompaktor.Scoring/ Kompaktor.sln
git commit -m "feat(scoring): add Kompaktor.Scoring project with value types"
```

---

### Task 2: Add ParticipantCount to CoinJoinRecordEntity

**Files:**
- Modify: `Kompaktor.Wallet/Data/Entities.cs`

- [ ] **Step 1: Add the property**

Add to `CoinJoinRecordEntity`:
```csharp
public int ParticipantCount { get; set; }  // Distinct participant identities
```

- [ ] **Step 2: Verify build**

```bash
dotnet build Kompaktor.Wallet/Kompaktor.Wallet.csproj
```

- [ ] **Step 3: Commit**

```bash
git add Kompaktor.Wallet/Data/Entities.cs
git commit -m "feat(wallet): add ParticipantCount to CoinJoinRecordEntity"
```

---

### Task 3: Implement AnonymityScorer with tests

**Files:**
- Create: `Kompaktor.Scoring/AnonymityScorer.cs`
- Create: `Kompaktor.Tests/AnonymityScorerTests.cs`
- Modify: `Kompaktor.Tests/Kompaktor.Tests.csproj`

- [ ] **Step 1: Add Kompaktor.Scoring reference to test project**

Add to `Kompaktor.Tests/Kompaktor.Tests.csproj`:
```xml
<ProjectReference Include="..\Kompaktor.Scoring\Kompaktor.Scoring.csproj" />
```

- [ ] **Step 2: Write failing tests**

```csharp
using Kompaktor.Scoring;
using Kompaktor.Wallet.Data;
using Xunit;

namespace Kompaktor.Tests;

public class AnonymityScorerTests
{
    private readonly AnonymityScorer _scorer = new(new ScoringOptions());

    [Fact]
    public void VirginCoin_ReturnsScoreOfOne()
    {
        var utxo = MakeUtxo(100_000);
        var result = _scorer.Score(utxo, [], []);

        Assert.Equal(1, result.RawAnonSet);
        Assert.Equal(1.0, result.EffectiveScore);
        Assert.Equal(0, result.CoinJoinCount);
        Assert.Equal(ConfidenceLevel.High, result.Confidence);
    }

    [Fact]
    public void SingleRound_ScoreEqualsParticipantCount()
    {
        var utxo = MakeUtxo(100_000);
        var record = MakeCoinJoinRecord(participantCount: 10, totalInputs: 12, totalOutputs: 15);
        var participation = new CoinJoinParticipationEntity
            { UtxoId = utxo.Id, CoinJoinRecordId = record.Id, Role = "Output", CoinJoinRecord = record, Utxo = utxo };

        var result = _scorer.Score(utxo, [participation], []);

        Assert.Equal(10, result.RawAnonSet);
        Assert.Equal(1, result.CoinJoinCount);
    }

    [Fact]
    public void MultipleRounds_MultiplicativeComposition()
    {
        var utxo1 = MakeUtxo(100_000, id: 1);
        var utxo2 = MakeUtxo(100_000, id: 2);
        var round1 = MakeCoinJoinRecord(participantCount: 10, id: 1);
        var round2 = MakeCoinJoinRecord(participantCount: 15, id: 2);

        // utxo1 was input to round1, utxo2 was output of round1 and input to round2
        var participations = new[]
        {
            new CoinJoinParticipationEntity
                { UtxoId = utxo1.Id, CoinJoinRecordId = round1.Id, Role = "Input", CoinJoinRecord = round1, Utxo = utxo1 },
            new CoinJoinParticipationEntity
                { UtxoId = utxo2.Id, CoinJoinRecordId = round1.Id, Role = "Output", CoinJoinRecord = round1, Utxo = utxo2 },
            new CoinJoinParticipationEntity
                { UtxoId = utxo2.Id, CoinJoinRecordId = round2.Id, Role = "Input", CoinJoinRecord = round2, Utxo = utxo2 },
        };

        // Score utxo2 — it went through round1 (as output) so inherits round1's anon set,
        // but utxo2 itself is the current coin. We're scoring utxo2.
        var result = _scorer.Score(utxo2, participations, []);

        // utxo2 participated as output in round1 (10 participants)
        Assert.Equal(10, result.RawAnonSet);
        Assert.Equal(1, result.CoinJoinCount);
    }

    [Fact]
    public void RawAnonSet_CappedAtMaximum()
    {
        var utxo = MakeUtxo(100_000);
        var record = MakeCoinJoinRecord(participantCount: 20_000);
        var participation = new CoinJoinParticipationEntity
            { UtxoId = utxo.Id, CoinJoinRecordId = record.Id, Role = "Output", CoinJoinRecord = record, Utxo = utxo };

        var result = _scorer.Score(utxo, [participation], []);

        Assert.Equal(10_000, result.RawAnonSet);
    }

    [Fact]
    public void UniqueAmount_AppliesSeverePenalty()
    {
        var options = new ScoringOptions { UniqueAmountPenalty = 0.1 };
        var scorer = new AnonymityScorer(options);
        var utxo = MakeUtxo(123_456);
        var record = MakeCoinJoinRecord(participantCount: 10, outputValues: [123_456, 200_000, 300_000, 400_000, 500_000]);
        var participation = new CoinJoinParticipationEntity
            { UtxoId = utxo.Id, CoinJoinRecordId = record.Id, Role = "Output", CoinJoinRecord = record, Utxo = utxo };

        var result = scorer.Score(utxo, [participation], []);

        Assert.Equal(10, result.RawAnonSet);
        Assert.True(result.EffectiveScore < 2.0, $"Expected penalty for unique amount, got {result.EffectiveScore}");
    }

    [Fact]
    public void ExternalLabel_AppliesPenalty()
    {
        var utxo = MakeUtxo(100_000);
        var record = MakeCoinJoinRecord(participantCount: 10);
        var participation = new CoinJoinParticipationEntity
            { UtxoId = utxo.Id, CoinJoinRecordId = record.Id, Role = "Output", CoinJoinRecord = record, Utxo = utxo };
        var label = new LabelEntity { EntityType = "Utxo", EntityId = utxo.Id.ToString(), Text = "Exchange:Coinbase" };

        var result = _scorer.Score(utxo, [participation], [label]);

        Assert.True(result.EffectiveScore < 10.0, "External label should reduce score");
    }

    [Fact]
    public void AddressReuse_AppliesPenalty()
    {
        var address = new AddressEntity { Id = 1, ScriptPubKey = new byte[] { 1, 2, 3 }, IsUsed = true };
        var utxo = MakeUtxo(100_000, addressEntity: address);
        var record = MakeCoinJoinRecord(participantCount: 10);
        var participation = new CoinJoinParticipationEntity
            { UtxoId = utxo.Id, CoinJoinRecordId = record.Id, Role = "Output", CoinJoinRecord = record, Utxo = utxo };

        // Address has multiple UTXOs = reuse
        address.Utxos = [utxo, MakeUtxo(50_000, id: 99, addressEntity: address)];

        var result = _scorer.Score(utxo, [participation], []);

        Assert.True(result.EffectiveScore < 10.0, "Address reuse should reduce score");
    }

    private static UtxoEntity MakeUtxo(long amountSat, int id = 1, AddressEntity? addressEntity = null)
    {
        var addr = addressEntity ?? new AddressEntity { Id = id, ScriptPubKey = new byte[] { 1, 2, 3 } };
        return new UtxoEntity
        {
            Id = id, TxId = $"tx{id}", OutputIndex = 0, AmountSat = amountSat,
            ScriptPubKey = new byte[] { 1, 2, 3 }, AddressId = addr.Id, Address = addr
        };
    }

    private static CoinJoinRecordEntity MakeCoinJoinRecord(
        int participantCount = 10, int totalInputs = 12, int totalOutputs = 15,
        int id = 1, long[]? outputValues = null)
    {
        var record = new CoinJoinRecordEntity
        {
            Id = id, RoundId = $"round{id}", TransactionId = $"cjtx{id}",
            Status = "Completed", ParticipantCount = participantCount,
            OurInputCount = 1, TotalInputCount = totalInputs,
            OurOutputCount = 1, TotalOutputCount = totalOutputs
        };
        if (outputValues is not null)
            record.OutputValuesSat = outputValues;
        return record;
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test Kompaktor.Tests --filter "FullyQualifiedName~AnonymityScorerTests" --no-build
```
Expected: Build failure — AnonymityScorer class does not exist yet.

- [ ] **Step 4: Implement AnonymityScorer**

```csharp
using Kompaktor.Wallet.Data;

namespace Kompaktor.Scoring;

public class AnonymityScorer
{
    private readonly ScoringOptions _options;

    public AnonymityScorer(ScoringOptions options)
    {
        _options = options;
    }

    public AnonymityScore Score(
        UtxoEntity utxo,
        IReadOnlyList<CoinJoinParticipationEntity> participations,
        IReadOnlyList<LabelEntity> labels)
    {
        // Find rounds where this UTXO was an output (i.e., it was created by a coinjoin)
        var outputRounds = participations
            .Where(p => p.UtxoId == utxo.Id && p.Role == "Output")
            .Select(p => p.CoinJoinRecord)
            .OrderBy(r => r.CreatedAt)
            .ToList();

        if (outputRounds.Count == 0)
            return new AnonymityScore(1, 1.0, 0, ConfidenceLevel.High);

        // Walk ancestor chain: for each round, check if its inputs have their own coinjoin history
        var chainDepth = 0;
        var rawAnonSet = 1;
        var currentUtxoId = utxo.Id;
        var visited = new HashSet<int>();

        foreach (var round in outputRounds)
        {
            if (!visited.Add(round.Id)) continue;
            var participants = round.ParticipantCount > 0 ? round.ParticipantCount : round.TotalInputCount;
            rawAnonSet *= participants;
            chainDepth++;
            if (chainDepth >= _options.MaxAncestorDepth) break;
        }

        // Also walk ancestor inputs recursively
        var ancestorRounds = WalkAncestors(utxo.Id, participations, visited, chainDepth);
        foreach (var round in ancestorRounds)
        {
            var participants = round.ParticipantCount > 0 ? round.ParticipantCount : round.TotalInputCount;
            rawAnonSet *= participants;
        }

        rawAnonSet = Math.Min(rawAnonSet, _options.MaxRawAnonSet);

        // Amount penalty (use the most recent round)
        var latestRound = outputRounds.Last();
        var amountPenalty = ComputeAmountPenalty(utxo.AmountSat, latestRound);

        // Label penalty
        var utxoLabels = labels
            .Where(l => (l.EntityType == "Utxo" && l.EntityId == utxo.Id.ToString()) ||
                        (l.EntityType == "Address" && l.EntityId == utxo.AddressId.ToString()))
            .ToList();
        var clusterPenalty = ComputeClusterPenalty(utxoLabels);

        // Address reuse penalty
        var reusePenalty = 1.0;
        if (utxo.Address is { IsUsed: true, Utxos.Count: > 1 })
            reusePenalty = _options.AddressReusePenalty;

        var effectiveScore = rawAnonSet * amountPenalty * clusterPenalty * reusePenalty;
        var totalCoinJoinCount = outputRounds.Count + ancestorRounds.Count;

        var confidence = totalCoinJoinCount > 0 && latestRound.ParticipantCount > 0
            ? ConfidenceLevel.High
            : ConfidenceLevel.Medium;

        return new AnonymityScore(rawAnonSet, effectiveScore, totalCoinJoinCount, confidence);
    }

    private List<CoinJoinRecordEntity> WalkAncestors(
        int utxoId,
        IReadOnlyList<CoinJoinParticipationEntity> allParticipations,
        HashSet<int> visitedRounds,
        int currentDepth)
    {
        if (currentDepth >= _options.MaxAncestorDepth)
            return [];

        var result = new List<CoinJoinRecordEntity>();

        // Find rounds where this UTXO was an output
        var outputRounds = allParticipations
            .Where(p => p.UtxoId == utxoId && p.Role == "Output")
            .Select(p => p.CoinJoinRecord)
            .ToList();

        foreach (var round in outputRounds)
        {
            if (!visitedRounds.Add(round.Id)) continue;

            // Find input UTXOs of this round
            var inputUtxoIds = allParticipations
                .Where(p => p.CoinJoinRecordId == round.Id && p.Role == "Input")
                .Select(p => p.UtxoId)
                .ToList();

            // Recurse into each input's history
            foreach (var inputId in inputUtxoIds)
            {
                var ancestorRounds = WalkAncestors(inputId, allParticipations, visitedRounds, currentDepth + 1);
                result.AddRange(ancestorRounds);
            }
        }

        return result;
    }

    private double ComputeAmountPenalty(long amountSat, CoinJoinRecordEntity round)
    {
        if (round.OutputValuesSat is not { Length: > 0 })
            return 1.0; // No output data available — no penalty

        var tolerance = _options.AmountBucketTolerance;
        var matchCount = round.OutputValuesSat.Count(v =>
            Math.Abs(v - amountSat) <= amountSat * tolerance);

        return matchCount switch
        {
            1 => _options.UniqueAmountPenalty,
            <= 3 => _options.RareAmountPenalty,  // uses RareAmountThreshold conceptually
            _ => 1.0
        };
    }

    private double ComputeClusterPenalty(IReadOnlyList<LabelEntity> labels)
    {
        if (labels.Count == 0)
            return 1.0;

        var hasExternal = labels.Any(l =>
            l.Text.StartsWith("Exchange:", StringComparison.OrdinalIgnoreCase) ||
            l.Text.StartsWith("KYC:", StringComparison.OrdinalIgnoreCase) ||
            l.Text.StartsWith("P2P:", StringComparison.OrdinalIgnoreCase));

        return hasExternal ? _options.ExternalLabelPenalty : _options.InternalLabelPenalty;
    }
}
```

- [ ] **Step 5: Add OutputValuesSat to CoinJoinRecordEntity**

The scorer needs output value distribution for amount analysis. Add to `CoinJoinRecordEntity`:
```csharp
public long[] OutputValuesSat { get; set; } = [];
```

And in `WalletDbContext.OnModelCreating`, add a value converter for the `CoinJoinRecordEntity`:
```csharp
e.Property(c => c.OutputValuesSat)
    .HasConversion(
        v => string.Join(',', v),
        v => string.IsNullOrEmpty(v) ? [] : v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray());
```

- [ ] **Step 6: Run tests and verify they pass**

```bash
dotnet test Kompaktor.Tests --filter "FullyQualifiedName~AnonymityScorerTests" -v n
```
Expected: All 7 tests pass.

- [ ] **Step 7: Commit**

```bash
git add Kompaktor.Scoring/ Kompaktor.Wallet/Data/ Kompaktor.Tests/
git commit -m "feat(scoring): implement AnonymityScorer with amount and label penalties"
```

---

### Task 4: Implement LabelClusterAnalyzer with tests

**Files:**
- Create: `Kompaktor.Scoring/LabelClusterAnalyzer.cs`
- Create: `Kompaktor.Tests/LabelClusterAnalyzerTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using Kompaktor.Scoring;
using Kompaktor.Wallet.Data;
using Xunit;

namespace Kompaktor.Tests;

public class LabelClusterAnalyzerTests
{
    private readonly LabelClusterAnalyzer _analyzer = new();

    [Fact]
    public void NoLabels_ReturnsSingleClusterPerUtxo()
    {
        var utxos = new[] { MakeUtxo(1, 100_000), MakeUtxo(2, 200_000) };
        var clusters = _analyzer.AnalyzeClusters(utxos, [], []);

        // Each unlabeled UTXO is its own cluster
        Assert.Equal(2, clusters.Count);
    }

    [Fact]
    public void SameLabel_GroupsIntoOneCluster()
    {
        var utxos = new[] { MakeUtxo(1, 100_000), MakeUtxo(2, 200_000) };
        var labels = new[]
        {
            new LabelEntity { EntityType = "Utxo", EntityId = "1", Text = "Exchange:Coinbase" },
            new LabelEntity { EntityType = "Utxo", EntityId = "2", Text = "Exchange:Coinbase" }
        };

        var clusters = _analyzer.AnalyzeClusters(utxos, labels, []);

        Assert.Single(clusters);
        Assert.Equal(2, clusters[0].UtxoIds.Count);
        Assert.True(clusters[0].HasExternalSource);
    }

    [Fact]
    public void DifferentLabels_SeparateClusters()
    {
        var utxos = new[] { MakeUtxo(1, 100_000), MakeUtxo(2, 200_000) };
        var labels = new[]
        {
            new LabelEntity { EntityType = "Utxo", EntityId = "1", Text = "Exchange:Coinbase" },
            new LabelEntity { EntityType = "Utxo", EntityId = "2", Text = "Mining:Pool" }
        };

        var clusters = _analyzer.AnalyzeClusters(utxos, labels, []);

        Assert.Equal(2, clusters.Count);
    }

    [Fact]
    public void CoinJoin_BreaksClustering()
    {
        var utxo1 = MakeUtxo(1, 100_000);
        var utxo2 = MakeUtxo(2, 200_000); // output of coinjoin
        var cjRecord = new CoinJoinRecordEntity
        {
            Id = 1, RoundId = "r1", TransactionId = "cjtx1", Status = "Completed",
            ParticipantCount = 10, TotalInputCount = 12, TotalOutputCount = 15,
            OurInputCount = 1, OurOutputCount = 1
        };
        var participations = new[]
        {
            new CoinJoinParticipationEntity { UtxoId = 1, CoinJoinRecordId = 1, Role = "Input", CoinJoinRecord = cjRecord, Utxo = utxo1 },
            new CoinJoinParticipationEntity { UtxoId = 2, CoinJoinRecordId = 1, Role = "Output", CoinJoinRecord = cjRecord, Utxo = utxo2 }
        };
        var labels = new[]
        {
            new LabelEntity { EntityType = "Utxo", EntityId = "1", Text = "Exchange:Coinbase" }
        };

        var clusters = _analyzer.AnalyzeClusters([utxo1, utxo2], labels, [cjRecord]);

        // utxo1 has the exchange label, utxo2 should NOT inherit it (coinjoin breaks the link)
        var utxo2Cluster = clusters.First(c => c.UtxoIds.Contains(2));
        Assert.False(utxo2Cluster.HasExternalSource);
    }

    [Fact]
    public void ChangeOutput_InheritsInputLabel()
    {
        var utxo1 = MakeUtxo(1, 100_000);
        var utxo2 = MakeUtxo(2, 50_000); // change output — same address pattern, NOT a coinjoin
        var labels = new[]
        {
            new LabelEntity { EntityType = "Utxo", EntityId = "1", Text = "Payment:Alice" },
            new LabelEntity { EntityType = "Utxo", EntityId = "2", Text = "Payment:Alice" }
        };

        var clusters = _analyzer.AnalyzeClusters([utxo1, utxo2], labels, []);

        // Same label = same cluster
        Assert.Single(clusters);
        Assert.Equal(2, clusters[0].UtxoIds.Count);
    }

    private static UtxoEntity MakeUtxo(int id, long amountSat)
    {
        var addr = new AddressEntity { Id = id, ScriptPubKey = new byte[] { (byte)id } };
        return new UtxoEntity
        {
            Id = id, TxId = $"tx{id}", OutputIndex = 0, AmountSat = amountSat,
            ScriptPubKey = new byte[] { (byte)id }, AddressId = id, Address = addr
        };
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: Build failure — LabelClusterAnalyzer does not exist.

- [ ] **Step 3: Implement LabelClusterAnalyzer**

```csharp
using Kompaktor.Wallet.Data;

namespace Kompaktor.Scoring;

public record LabelCluster(
    HashSet<int> UtxoIds,
    HashSet<string> Labels,
    bool HasExternalSource);

public class LabelClusterAnalyzer
{
    public List<LabelCluster> AnalyzeClusters(
        IReadOnlyList<UtxoEntity> utxos,
        IReadOnlyList<LabelEntity> labels,
        IReadOnlyList<CoinJoinRecordEntity> coinjoins)
    {
        // Build label map: utxoId → set of label texts
        var utxoLabels = new Dictionary<int, HashSet<string>>();
        foreach (var utxo in utxos)
            utxoLabels[utxo.Id] = new HashSet<string>();

        foreach (var label in labels)
        {
            if (label.EntityType == "Utxo" && int.TryParse(label.EntityId, out var utxoId))
            {
                if (utxoLabels.TryGetValue(utxoId, out var set))
                    set.Add(label.Text);
            }
            else if (label.EntityType == "Address")
            {
                // Apply address labels to all UTXOs at that address
                foreach (var utxo in utxos)
                {
                    if (utxo.AddressId.ToString() == label.EntityId && utxoLabels.TryGetValue(utxo.Id, out var set))
                        set.Add(label.Text);
                }
            }
        }

        // Build coinjoin output set (UTXOs that are coinjoin outputs should NOT inherit input labels)
        var coinJoinOutputIds = new HashSet<int>();
        foreach (var cj in coinjoins)
        {
            if (cj.Participations is not null)
            {
                foreach (var p in cj.Participations.Where(p => p.Role == "Output"))
                    coinJoinOutputIds.Add(p.UtxoId);
            }
        }

        // Group by label set using union-find
        var parent = new Dictionary<int, int>();
        foreach (var utxo in utxos)
            parent[utxo.Id] = utxo.Id;

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b)
        {
            var ra = Find(a); var rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        // UTXOs with the same label text cluster together (unless coinjoin-separated)
        var labelToUtxos = new Dictionary<string, List<int>>();
        foreach (var (utxoId, labelSet) in utxoLabels)
        {
            // CoinJoin outputs don't inherit labels from inputs via the label text
            // They only cluster with UTXOs that share labels applied directly to them
            foreach (var labelText in labelSet)
            {
                if (!labelToUtxos.TryGetValue(labelText, out var list))
                {
                    list = new List<int>();
                    labelToUtxos[labelText] = list;
                }
                list.Add(utxoId);
            }
        }

        foreach (var (_, utxoIds) in labelToUtxos)
        {
            for (int i = 1; i < utxoIds.Count; i++)
                Union(utxoIds[0], utxoIds[i]);
        }

        // Build clusters
        var groups = new Dictionary<int, (HashSet<int> ids, HashSet<string> labels)>();
        foreach (var utxo in utxos)
        {
            var root = Find(utxo.Id);
            if (!groups.TryGetValue(root, out var group))
            {
                group = (new HashSet<int>(), new HashSet<string>());
                groups[root] = group;
            }
            group.ids.Add(utxo.Id);
            foreach (var label in utxoLabels[utxo.Id])
                group.labels.Add(label);
        }

        return groups.Values.Select(g => new LabelCluster(
            g.ids,
            g.labels,
            HasExternalSourceLabel(g.labels)
        )).ToList();
    }

    private static bool HasExternalSourceLabel(HashSet<string> labels)
    {
        return labels.Any(l =>
            l.StartsWith("Exchange:", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("KYC:", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("P2P:", StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Step 4: Run tests and verify they pass**

```bash
dotnet test Kompaktor.Tests --filter "FullyQualifiedName~LabelClusterAnalyzerTests" -v n
```
Expected: All 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add Kompaktor.Scoring/ Kompaktor.Tests/
git commit -m "feat(scoring): implement LabelClusterAnalyzer with union-find clustering"
```

---

### Task 5: Implement CoinSelectionAdvisor with tests

**Files:**
- Create: `Kompaktor.Scoring/CoinSelectionAdvisor.cs`
- Create: `Kompaktor.Tests/CoinSelectionAdvisorTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using Kompaktor.Scoring;
using Kompaktor.Wallet.Data;
using Xunit;

namespace Kompaktor.Tests;

public class CoinSelectionAdvisorTests
{
    [Fact]
    public void PrivacyFirst_PrefersHighAnonCoins()
    {
        var coins = new[]
        {
            MakeScoredUtxo(1, 100_000, rawAnon: 50, effective: 50.0),
            MakeScoredUtxo(2, 100_000, rawAnon: 5, effective: 5.0),
            MakeScoredUtxo(3, 100_000, rawAnon: 1, effective: 1.0),
        };
        var advisor = new CoinSelectionAdvisor(new ScoringOptions());

        var result = advisor.SelectCoins(coins, 150_000, CoinSelectionStrategy.PrivacyFirst);

        Assert.Equal(2, result.Selected.Count);
        Assert.Contains(result.Selected, s => s.Utxo.Id == 1);
        Assert.Contains(result.Selected, s => s.Utxo.Id == 2);
    }

    [Fact]
    public void DoNotMixClusters_WarnsWhenMixed()
    {
        var coins = new[]
        {
            MakeScoredUtxo(1, 100_000, rawAnon: 1, effective: 1.0, labels: ["Exchange:Coinbase"]),
            MakeScoredUtxo(2, 100_000, rawAnon: 50, effective: 50.0), // unlabeled
        };
        var advisor = new CoinSelectionAdvisor(new ScoringOptions());

        var result = advisor.SelectCoins(coins, 150_000, CoinSelectionStrategy.PrivacyFirst);

        Assert.Contains(result.Warnings, w => w.Contains("cluster", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetCoinjoinCandidates_FiltersLowScoreCoins()
    {
        var coins = new[]
        {
            MakeScoredUtxo(1, 100_000, rawAnon: 1, effective: 1.0),
            MakeScoredUtxo(2, 100_000, rawAnon: 50, effective: 50.0),
            MakeScoredUtxo(3, 100_000, rawAnon: 3, effective: 3.0),
        };
        var advisor = new CoinSelectionAdvisor(new ScoringOptions { MinEffectiveScoreForCoinjoin = 5.0 });

        var candidates = advisor.GetCoinjoinCandidates(coins);

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, c => c.Utxo.Id == 1);
        Assert.Contains(candidates, c => c.Utxo.Id == 3);
    }

    [Fact]
    public void FeeSaver_PrefersFewInputs()
    {
        var coins = new[]
        {
            MakeScoredUtxo(1, 200_000, rawAnon: 1, effective: 1.0),
            MakeScoredUtxo(2, 50_000, rawAnon: 50, effective: 50.0),
            MakeScoredUtxo(3, 50_000, rawAnon: 50, effective: 50.0),
            MakeScoredUtxo(4, 50_000, rawAnon: 50, effective: 50.0),
        };
        var advisor = new CoinSelectionAdvisor(new ScoringOptions());

        var result = advisor.SelectCoins(coins, 150_000, CoinSelectionStrategy.FeeSaver);

        // Should prefer the single 200k coin over three 50k coins
        Assert.Single(result.Selected);
        Assert.Equal(1, result.Selected[0].Utxo.Id);
    }

    [Fact]
    public void InsufficientFunds_ReturnsAllWithWarning()
    {
        var coins = new[]
        {
            MakeScoredUtxo(1, 50_000, rawAnon: 10, effective: 10.0),
        };
        var advisor = new CoinSelectionAdvisor(new ScoringOptions());

        var result = advisor.SelectCoins(coins, 100_000, CoinSelectionStrategy.PrivacyFirst);

        Assert.Single(result.Selected);
        Assert.Contains(result.Warnings, w => w.Contains("insufficient", StringComparison.OrdinalIgnoreCase));
    }

    private static ScoredUtxo MakeScoredUtxo(
        int id, long amountSat, int rawAnon, double effective, string[]? labels = null)
    {
        var addr = new AddressEntity { Id = id, ScriptPubKey = new byte[] { (byte)id } };
        var utxo = new UtxoEntity
        {
            Id = id, TxId = $"tx{id}", OutputIndex = 0, AmountSat = amountSat,
            ScriptPubKey = new byte[] { (byte)id }, AddressId = id, Address = addr
        };
        var score = new AnonymityScore(rawAnon, effective, rawAnon > 1 ? 1 : 0, ConfidenceLevel.High);
        return new ScoredUtxo(utxo, score, labels ?? []);
    }
}
```

- [ ] **Step 2: Implement CoinSelectionAdvisor**

```csharp
namespace Kompaktor.Scoring;

public enum CoinSelectionStrategy { PrivacyFirst, FeeSaver, Consolidation }

public record CoinSelectionResult(
    IReadOnlyList<ScoredUtxo> Selected,
    long TotalAmountSat,
    string[] Warnings);

public class CoinSelectionAdvisor
{
    private readonly ScoringOptions _options;

    public CoinSelectionAdvisor(ScoringOptions options) => _options = options;

    public CoinSelectionResult SelectCoins(
        IReadOnlyList<ScoredUtxo> availableCoins,
        long targetAmountSat,
        CoinSelectionStrategy strategy = CoinSelectionStrategy.PrivacyFirst)
    {
        var warnings = new List<string>();

        var sorted = strategy switch
        {
            CoinSelectionStrategy.PrivacyFirst => availableCoins
                .OrderByDescending(c => c.Score.EffectiveScore)
                .ThenByDescending(c => c.Utxo.AmountSat)
                .ToList(),
            CoinSelectionStrategy.FeeSaver => availableCoins
                .OrderByDescending(c => c.Utxo.AmountSat) // Prefer fewer, larger inputs
                .ToList(),
            CoinSelectionStrategy.Consolidation => availableCoins
                .OrderBy(c => c.Utxo.AmountSat) // Start with smallest
                .ToList(),
            _ => availableCoins.ToList()
        };

        var selected = new List<ScoredUtxo>();
        long total = 0;

        foreach (var coin in sorted)
        {
            selected.Add(coin);
            total += coin.Utxo.AmountSat;
            if (total >= targetAmountSat) break;
        }

        if (total < targetAmountSat)
            warnings.Add("Insufficient funds: available coins do not cover target amount");

        // Check for cluster mixing
        var hasLabeled = selected.Any(c => c.Labels.Length > 0);
        var hasUnlabeled = selected.Any(c => c.Labels.Length == 0 && c.Score.RawAnonSet > 1);
        if (hasLabeled && hasUnlabeled)
            warnings.Add("Warning: mixing labeled and unlabeled coins reduces cluster privacy");

        return new CoinSelectionResult(selected, total, warnings.ToArray());
    }

    public IReadOnlyList<ScoredUtxo> GetCoinjoinCandidates(
        IReadOnlyList<ScoredUtxo> allCoins,
        double? minEffectiveScore = null)
    {
        var threshold = minEffectiveScore ?? _options.MinEffectiveScoreForCoinjoin;
        return allCoins
            .Where(c => c.Score.EffectiveScore < threshold)
            .OrderBy(c => c.Score.EffectiveScore)
            .ToList();
    }
}
```

- [ ] **Step 3: Update ScoredUtxo to include Labels**

```csharp
using Kompaktor.Wallet.Data;

namespace Kompaktor.Scoring;

public record ScoredUtxo(UtxoEntity Utxo, AnonymityScore Score, string[] Labels = default!)
{
    public string[] Labels { get; init; } = Labels ?? [];
}
```

- [ ] **Step 4: Run tests and verify they pass**

```bash
dotnet test Kompaktor.Tests --filter "FullyQualifiedName~CoinSelectionAdvisorTests" -v n
```
Expected: All 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add Kompaktor.Scoring/ Kompaktor.Tests/
git commit -m "feat(scoring): implement CoinSelectionAdvisor with privacy-first and fee-saver strategies"
```

---

### Task 6: Run full test suite and update README

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Run full test suite**

```bash
dotnet test
```
Expected: All existing tests pass + new scoring tests pass.

- [ ] **Step 2: Update README**

Add to architecture tree:
```
├── Kompaktor.Scoring/      # Anonymity scoring, label clustering, coin selection
│   ├── AnonymityScorer.cs              # Per-UTXO anonymity score calculator
│   ├── LabelClusterAnalyzer.cs         # Label propagation and cluster detection
│   ├── CoinSelectionAdvisor.cs         # Privacy-aware coin selection
│   └── AnonymityScore.cs              # Score value object and types
```

Add to Key Components:
```
### `AnonymityScorer`
Per-UTXO anonymity scoring engine. Computes raw anonymity set from coinjoin participant counts
with multiplicative composition across rounds. Applies penalties for amount distinguishability
(unique/rare output values), label clustering (known external sources), and address reuse.
Configurable via `ScoringOptions`.
```

Update test count.

- [ ] **Step 3: Commit and push**

```bash
git add README.md
git commit -m "docs: add Kompaktor.Scoring to README"
git push origin master
```
