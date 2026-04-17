using Kompaktor.Scoring;
using Kompaktor.Wallet.Data;
using Xunit;

namespace Kompaktor.Tests;

public class LabelClusterAnalyzerTests
{
    private readonly LabelClusterAnalyzer _analyzer = new();

    [Fact]
    public void NoLabels_EachUtxoIsOwnCluster()
    {
        var utxos = new[] { MakeUtxo(1, 100_000), MakeUtxo(2, 200_000) };
        var clusters = _analyzer.AnalyzeClusters(utxos, [], []);

        Assert.Equal(2, clusters.Count);
        Assert.All(clusters, c => Assert.Single(c.UtxoIds));
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
    public void ExternalSourceLabel_DetectedCorrectly()
    {
        var utxos = new[] { MakeUtxo(1, 100_000) };
        var labels = new[]
        {
            new LabelEntity { EntityType = "Utxo", EntityId = "1", Text = "KYC:Verification" }
        };

        var clusters = _analyzer.AnalyzeClusters(utxos, labels, []);

        Assert.Single(clusters);
        Assert.True(clusters[0].HasExternalSource);
    }

    [Fact]
    public void InternalLabel_NotExternalSource()
    {
        var utxos = new[] { MakeUtxo(1, 100_000) };
        var labels = new[]
        {
            new LabelEntity { EntityType = "Utxo", EntityId = "1", Text = "Salary:January" }
        };

        var clusters = _analyzer.AnalyzeClusters(utxos, labels, []);

        Assert.Single(clusters);
        Assert.False(clusters[0].HasExternalSource);
    }

    [Fact]
    public void AddressLabel_AppliesToAllUtxosAtAddress()
    {
        var addr = new AddressEntity { Id = 10, ScriptPubKey = [1, 2, 3] };
        var utxo1 = MakeUtxo(1, 100_000, addressId: 10);
        var utxo2 = MakeUtxo(2, 200_000, addressId: 10);
        var labels = new[]
        {
            new LabelEntity { EntityType = "Address", EntityId = "10", Text = "Exchange:Kraken" }
        };

        var clusters = _analyzer.AnalyzeClusters([utxo1, utxo2], labels, []);

        Assert.Single(clusters);
        Assert.Equal(2, clusters[0].UtxoIds.Count);
        Assert.True(clusters[0].HasExternalSource);
    }

    [Fact]
    public void TransitiveClustering_TwoLabelsLinkThreeUtxos()
    {
        var utxos = new[] { MakeUtxo(1, 100_000), MakeUtxo(2, 200_000), MakeUtxo(3, 300_000) };
        var labels = new[]
        {
            new LabelEntity { EntityType = "Utxo", EntityId = "1", Text = "LabelA" },
            new LabelEntity { EntityType = "Utxo", EntityId = "2", Text = "LabelA" },
            new LabelEntity { EntityType = "Utxo", EntityId = "2", Text = "LabelB" },
            new LabelEntity { EntityType = "Utxo", EntityId = "3", Text = "LabelB" }
        };

        var clusters = _analyzer.AnalyzeClusters(utxos, labels, []);

        // utxo1 ←LabelA→ utxo2 ←LabelB→ utxo3 → all in one cluster
        Assert.Single(clusters);
        Assert.Equal(3, clusters[0].UtxoIds.Count);
    }

    private static UtxoEntity MakeUtxo(int id, long amountSat, int? addressId = null)
    {
        var addrId = addressId ?? id;
        return new UtxoEntity
        {
            Id = id, TxId = $"tx{id}", OutputIndex = 0, AmountSat = amountSat,
            ScriptPubKey = [(byte)id], AddressId = addrId,
            Address = new AddressEntity { Id = addrId, ScriptPubKey = [(byte)id] }
        };
    }
}
