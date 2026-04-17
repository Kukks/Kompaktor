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
        // Find rounds where this UTXO was created as an output
        var outputRounds = participations
            .Where(p => p.UtxoId == utxo.Id && p.Role == "Output")
            .Select(p => p.CoinJoinRecord)
            .OrderBy(r => r.CreatedAt)
            .ToList();

        if (outputRounds.Count == 0)
            return new AnonymityScore(1, 1.0, 0, ConfidenceLevel.High);

        // Compute raw anon set from direct output rounds
        var visited = new HashSet<int>();
        var rawAnonSet = 1;

        foreach (var round in outputRounds)
        {
            if (!visited.Add(round.Id)) continue;
            var participants = round.ParticipantCount > 0 ? round.ParticipantCount : round.TotalInputCount;
            rawAnonSet *= Math.Max(participants, 1);
        }

        // Walk ancestor chain for compound anonymity
        var ancestorRounds = WalkAncestors(utxo.Id, participations, visited, outputRounds.Count);
        foreach (var round in ancestorRounds)
        {
            var participants = round.ParticipantCount > 0 ? round.ParticipantCount : round.TotalInputCount;
            rawAnonSet *= Math.Max(participants, 1);
        }

        rawAnonSet = Math.Min(rawAnonSet, _options.MaxRawAnonSet);

        // Amount distinguishability penalty
        var latestRound = outputRounds[^1];
        var amountPenalty = ComputeAmountPenalty(utxo.AmountSat, latestRound);

        // Label cluster penalty
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

        // Find rounds where this UTXO was an input (meaning it was consumed)
        // Then find the rounds where those inputs were created as outputs (ancestor rounds)
        var inputRounds = allParticipations
            .Where(p => p.UtxoId == utxoId && p.Role == "Input")
            .Select(p => p.CoinJoinRecordId)
            .ToHashSet();

        foreach (var roundId in inputRounds)
        {
            // Find the input UTXOs of that round
            var inputUtxoIds = allParticipations
                .Where(p => p.CoinJoinRecordId == roundId && p.Role == "Input" && p.UtxoId != utxoId)
                .Select(p => p.UtxoId)
                .ToList();

            foreach (var inputId in inputUtxoIds)
            {
                // Check if this input was itself an output of an earlier coinjoin
                var ancestorOutputRounds = allParticipations
                    .Where(p => p.UtxoId == inputId && p.Role == "Output")
                    .Select(p => p.CoinJoinRecord)
                    .ToList();

                foreach (var round in ancestorOutputRounds)
                {
                    if (visitedRounds.Add(round.Id))
                    {
                        result.Add(round);
                        var deeper = WalkAncestors(inputId, allParticipations, visitedRounds, currentDepth + 1);
                        result.AddRange(deeper);
                    }
                }
            }
        }

        return result;
    }

    private double ComputeAmountPenalty(long amountSat, CoinJoinRecordEntity round)
    {
        if (round.OutputValuesSat is not { Length: > 0 })
            return 1.0;

        var tolerance = _options.AmountBucketTolerance;
        var matchCount = round.OutputValuesSat.Count(v =>
            Math.Abs(v - amountSat) <= amountSat * tolerance);

        return matchCount switch
        {
            <= 1 => _options.UniqueAmountPenalty,
            <= 3 => _options.RareAmountPenalty,
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
