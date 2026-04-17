using Kompaktor.Scoring;
using Kompaktor.Wallet.Data;
using Xunit;

namespace Kompaktor.Tests;

public class CredentialFlowTrackerTests
{
    private readonly CredentialFlowTracker _tracker = new();

    [Fact]
    public void EmptyEvents_ReturnsEmptyFlows()
    {
        var flows = _tracker.AnalyzeFlows([]);
        Assert.Empty(flows);
    }

    [Fact]
    public void SingleInput_SingleOutput_SimpleFlow()
    {
        var events = new CredentialEventEntity[]
        {
            new() { Id = 1, EventType = "Acquired", AmountSat = 100_000, GraphDepth = 0 },
            new() { Id = 2, EventType = "Spent", AmountSat = 99_000, ParentEventId = 1, GraphDepth = 1 },
        };

        var flows = _tracker.AnalyzeFlows(events);

        Assert.Single(flows);
        Assert.Equal(100_000, flows[0].InputAmountSat);
        Assert.Equal(99_000, flows[0].OutputAmountSat);
        Assert.Equal(0, flows[0].ChangeAmountSat);
        Assert.Equal(1_000, flows[0].FeeSat);
    }

    [Fact]
    public void SingleInput_PaymentPlusChange_SplitFlow()
    {
        var events = new CredentialEventEntity[]
        {
            new() { Id = 1, EventType = "Acquired", AmountSat = 100_000, GraphDepth = 0 },
            new() { Id = 2, EventType = "Reissued", AmountSat = 100_000, ParentEventId = 1, GraphDepth = 1 },
            new() { Id = 3, EventType = "Spent", AmountSat = 80_000, ParentEventId = 2, GraphDepth = 2 },
            new() { Id = 4, EventType = "Spent", AmountSat = 19_000, ParentEventId = 2, GraphDepth = 2 },
        };

        var flows = _tracker.AnalyzeFlows(events);

        Assert.Single(flows);
        Assert.Equal(100_000, flows[0].InputAmountSat);
        Assert.Equal(80_000, flows[0].OutputAmountSat);   // Largest = payment
        Assert.Equal(19_000, flows[0].ChangeAmountSat);    // Smaller = change
        Assert.Equal(1_000, flows[0].FeeSat);              // 100k - 80k - 19k = 1k
        Assert.Equal(1, flows[0].ReissuanceSteps);
    }

    [Fact]
    public void DeepMergeTree_TracksDepthAndSteps()
    {
        var events = new CredentialEventEntity[]
        {
            new() { Id = 1, EventType = "Acquired", AmountSat = 100_000, GraphDepth = 0 },
            new() { Id = 2, EventType = "Reissued", AmountSat = 100_000, ParentEventId = 1, GraphDepth = 1 },
            new() { Id = 3, EventType = "Reissued", AmountSat = 80_000, ParentEventId = 2, GraphDepth = 2 },
            new() { Id = 4, EventType = "Reissued", AmountSat = 80_000, ParentEventId = 3, GraphDepth = 3 },
            new() { Id = 5, EventType = "Spent", AmountSat = 80_000, ParentEventId = 4, GraphDepth = 4 },
        };

        var flows = _tracker.AnalyzeFlows(events);

        Assert.Single(flows);
        Assert.Equal(4, flows[0].GraphDepth);
        Assert.Equal(3, flows[0].ReissuanceSteps);
    }

    [Fact]
    public void MultipleRoots_ProducesMultipleFlows()
    {
        var events = new CredentialEventEntity[]
        {
            // Root 1: 50k input → 49k output
            new() { Id = 1, EventType = "Acquired", AmountSat = 50_000, GraphDepth = 0 },
            new() { Id = 2, EventType = "Spent", AmountSat = 49_000, ParentEventId = 1, GraphDepth = 1 },
            // Root 2: 30k input → 29k output
            new() { Id = 3, EventType = "Acquired", AmountSat = 30_000, GraphDepth = 0 },
            new() { Id = 4, EventType = "Spent", AmountSat = 29_000, ParentEventId = 3, GraphDepth = 1 },
        };

        var flows = _tracker.AnalyzeFlows(events);

        Assert.Equal(2, flows.Count);
        Assert.Contains(flows, f => f.InputAmountSat == 50_000 && f.OutputAmountSat == 49_000);
        Assert.Contains(flows, f => f.InputAmountSat == 30_000 && f.OutputAmountSat == 29_000);
    }

    [Fact]
    public void FeeOnly_NoOutputs_RecordsFeeFlow()
    {
        var events = new CredentialEventEntity[]
        {
            new() { Id = 1, EventType = "Acquired", AmountSat = 10_000, GraphDepth = 0 },
            // Credential drained as fee — no Spent event
        };

        var flows = _tracker.AnalyzeFlows(events);

        Assert.Single(flows);
        Assert.Equal(10_000, flows[0].InputAmountSat);
        Assert.Equal(0, flows[0].OutputAmountSat);
        Assert.Equal(10_000, flows[0].FeeSat);
    }

    [Fact]
    public void CredentialEventEntity_HasCorrectDefaults()
    {
        var entity = new CredentialEventEntity();
        Assert.Equal("", entity.CredentialSerial);
        Assert.Equal("", entity.EventType);
        Assert.Equal(0, entity.AmountSat);
        Assert.Null(entity.ParentEventId);
        Assert.Null(entity.OutputUtxoId);
        Assert.Equal(0, entity.GraphDepth);
    }
}
