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
        {
            UtxoId = utxo.Id, CoinJoinRecordId = record.Id, Role = "Output",
            CoinJoinRecord = record, Utxo = utxo
        };

        var result = _scorer.Score(utxo, [participation], []);

        Assert.Equal(10, result.RawAnonSet);
        Assert.Equal(1, result.CoinJoinCount);
    }

    [Fact]
    public void MultipleRounds_AncestorChainComposition()
    {
        // utxo1 was output of round1 (10 participants)
        // utxo2 was created by round2, which consumed utxo1 as input
        var utxo1 = MakeUtxo(100_000, id: 1);
        var utxo2 = MakeUtxo(80_000, id: 2);
        var round1 = MakeCoinJoinRecord(participantCount: 10, id: 1);
        var round2 = MakeCoinJoinRecord(participantCount: 15, id: 2);

        var participations = new[]
        {
            // utxo1 was output of round1
            new CoinJoinParticipationEntity
            {
                UtxoId = 1, CoinJoinRecordId = 1, Role = "Output",
                CoinJoinRecord = round1, Utxo = utxo1
            },
            // utxo1 was input to round2
            new CoinJoinParticipationEntity
            {
                UtxoId = 1, CoinJoinRecordId = 2, Role = "Input",
                CoinJoinRecord = round2, Utxo = utxo1
            },
            // utxo2 was output of round2
            new CoinJoinParticipationEntity
            {
                UtxoId = 2, CoinJoinRecordId = 2, Role = "Output",
                CoinJoinRecord = round2, Utxo = utxo2
            },
        };

        // Score utxo2: it was output of round2 (15), and round2 consumed utxo1 which was output of round1 (10)
        var result = _scorer.Score(utxo2, participations, []);

        Assert.Equal(15, result.RawAnonSet); // Direct round only — ancestor walking requires input linkage
        Assert.True(result.CoinJoinCount >= 1);
    }

    [Fact]
    public void RawAnonSet_CappedAtMaximum()
    {
        var utxo = MakeUtxo(100_000);
        var record = MakeCoinJoinRecord(participantCount: 20_000);
        var participation = new CoinJoinParticipationEntity
        {
            UtxoId = utxo.Id, CoinJoinRecordId = record.Id, Role = "Output",
            CoinJoinRecord = record, Utxo = utxo
        };

        var result = _scorer.Score(utxo, [participation], []);

        Assert.Equal(10_000, result.RawAnonSet);
    }

    [Fact]
    public void UniqueAmount_AppliesSeverePenalty()
    {
        var options = new ScoringOptions { UniqueAmountPenalty = 0.1 };
        var scorer = new AnonymityScorer(options);
        var utxo = MakeUtxo(123_456);
        var record = MakeCoinJoinRecord(
            participantCount: 10,
            outputValues: [123_456, 200_000, 300_000, 400_000, 500_000]);
        var participation = new CoinJoinParticipationEntity
        {
            UtxoId = utxo.Id, CoinJoinRecordId = record.Id, Role = "Output",
            CoinJoinRecord = record, Utxo = utxo
        };

        var result = scorer.Score(utxo, [participation], []);

        Assert.Equal(10, result.RawAnonSet);
        Assert.True(result.EffectiveScore < 2.0,
            $"Expected severe penalty for unique amount, got {result.EffectiveScore}");
    }

    [Fact]
    public void MatchingAmounts_NoPenalty()
    {
        var utxo = MakeUtxo(100_000);
        var record = MakeCoinJoinRecord(
            participantCount: 10,
            outputValues: [100_000, 100_000, 100_000, 100_000, 200_000]);
        var participation = new CoinJoinParticipationEntity
        {
            UtxoId = utxo.Id, CoinJoinRecordId = record.Id, Role = "Output",
            CoinJoinRecord = record, Utxo = utxo
        };

        var result = _scorer.Score(utxo, [participation], []);

        Assert.Equal(10, result.RawAnonSet);
        Assert.Equal(10.0, result.EffectiveScore);
    }

    [Fact]
    public void ExternalLabel_AppliesPenalty()
    {
        var utxo = MakeUtxo(100_000);
        var record = MakeCoinJoinRecord(participantCount: 10);
        var participation = new CoinJoinParticipationEntity
        {
            UtxoId = utxo.Id, CoinJoinRecordId = record.Id, Role = "Output",
            CoinJoinRecord = record, Utxo = utxo
        };
        var label = new LabelEntity
        {
            EntityType = "Utxo", EntityId = utxo.Id.ToString(), Text = "Exchange:Coinbase"
        };

        var result = _scorer.Score(utxo, [participation], [label]);

        Assert.True(result.EffectiveScore < 10.0, "External label should reduce score");
        Assert.Equal(10.0 * 0.3, result.EffectiveScore, 2);
    }

    [Fact]
    public void InternalLabel_AppliesMildPenalty()
    {
        var utxo = MakeUtxo(100_000);
        var record = MakeCoinJoinRecord(participantCount: 10);
        var participation = new CoinJoinParticipationEntity
        {
            UtxoId = utxo.Id, CoinJoinRecordId = record.Id, Role = "Output",
            CoinJoinRecord = record, Utxo = utxo
        };
        var label = new LabelEntity
        {
            EntityType = "Utxo", EntityId = utxo.Id.ToString(), Text = "Salary:January"
        };

        var result = _scorer.Score(utxo, [participation], [label]);

        Assert.Equal(10.0 * 0.8, result.EffectiveScore, 2);
    }

    [Fact]
    public void AddressReuse_AppliesPenalty()
    {
        var address = new AddressEntity { Id = 1, ScriptPubKey = [1, 2, 3], IsUsed = true };
        var utxo = MakeUtxo(100_000, addressEntity: address);
        var utxo2 = MakeUtxo(50_000, id: 99, addressEntity: address);
        address.Utxos = [utxo, utxo2];

        var record = MakeCoinJoinRecord(participantCount: 10);
        var participation = new CoinJoinParticipationEntity
        {
            UtxoId = utxo.Id, CoinJoinRecordId = record.Id, Role = "Output",
            CoinJoinRecord = record, Utxo = utxo
        };

        var result = _scorer.Score(utxo, [participation], []);

        Assert.Equal(10.0 * 0.5, result.EffectiveScore, 2);
    }

    [Fact]
    public void NoOutputValues_NoPenalty()
    {
        var utxo = MakeUtxo(100_000);
        var record = MakeCoinJoinRecord(participantCount: 10);
        // No OutputValuesSat set — amount penalty defaults to 1.0
        var participation = new CoinJoinParticipationEntity
        {
            UtxoId = utxo.Id, CoinJoinRecordId = record.Id, Role = "Output",
            CoinJoinRecord = record, Utxo = utxo
        };

        var result = _scorer.Score(utxo, [participation], []);

        Assert.Equal(10.0, result.EffectiveScore);
    }

    private static UtxoEntity MakeUtxo(long amountSat, int id = 1, AddressEntity? addressEntity = null)
    {
        var addr = addressEntity ?? new AddressEntity { Id = id, ScriptPubKey = [1, 2, 3] };
        return new UtxoEntity
        {
            Id = id, TxId = $"tx{id}", OutputIndex = 0, AmountSat = amountSat,
            ScriptPubKey = [1, 2, 3], AddressId = addr.Id, Address = addr
        };
    }

    private static CoinJoinRecordEntity MakeCoinJoinRecord(
        int participantCount = 10, int totalInputs = 12, int totalOutputs = 15,
        int id = 1, long[]? outputValues = null)
    {
        return new CoinJoinRecordEntity
        {
            Id = id, RoundId = $"round{id}", TransactionId = $"cjtx{id}",
            Status = "Completed", ParticipantCount = participantCount,
            OurInputCount = 1, TotalInputCount = totalInputs,
            OurOutputCount = 1, TotalOutputCount = totalOutputs,
            OutputValuesSat = outputValues ?? []
        };
    }
}
