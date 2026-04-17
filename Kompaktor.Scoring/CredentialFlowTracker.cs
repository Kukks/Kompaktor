using Kompaktor.Wallet.Data;

namespace Kompaktor.Scoring;

public record PaymentFlow(
    long InputAmountSat,
    long OutputAmountSat,
    long ChangeAmountSat,
    long FeeSat,
    int ReissuanceSteps,
    int GraphDepth,
    string? OutputLabel);

public class CredentialFlowTracker
{
    /// <summary>
    /// Analyzes credential events for a round and produces per-payment flow summaries.
    /// Walks the event DAG from acquired (root) through reissued (intermediate) to spent (leaf) events.
    /// </summary>
    public List<PaymentFlow> AnalyzeFlows(IReadOnlyList<CredentialEventEntity> events)
    {
        if (events.Count == 0)
            return [];

        // Build parent→children index
        var childrenOf = new Dictionary<int, List<CredentialEventEntity>>();
        var roots = new List<CredentialEventEntity>();

        foreach (var evt in events)
        {
            if (evt.ParentEventId is null)
            {
                roots.Add(evt);
            }
            else
            {
                if (!childrenOf.TryGetValue(evt.ParentEventId.Value, out var children))
                {
                    children = [];
                    childrenOf[evt.ParentEventId.Value] = children;
                }
                children.Add(evt);
            }
        }

        var flows = new List<PaymentFlow>();

        foreach (var root in roots)
        {
            // Walk from each root to its leaf Spent events
            var leaves = new List<CredentialEventEntity>();
            var maxDepth = 0;
            var reissuanceSteps = 0;
            CollectLeaves(root, childrenOf, leaves, ref maxDepth, ref reissuanceSteps);

            var inputAmount = root.AmountSat;

            // Separate outputs into payment vs change
            // Heuristic: the largest output is the payment, smaller is change
            // If only one output, it's the payment
            var spentLeaves = leaves.Where(l => l.EventType == "Spent").ToList();

            if (spentLeaves.Count == 0)
            {
                // All credentials drained with no registered output — fee-only flow
                flows.Add(new PaymentFlow(inputAmount, 0, 0, inputAmount, reissuanceSteps, maxDepth, null));
                continue;
            }

            if (spentLeaves.Count == 1)
            {
                var output = spentLeaves[0];
                var fee = inputAmount - output.AmountSat;
                flows.Add(new PaymentFlow(inputAmount, output.AmountSat, 0, Math.Max(fee, 0),
                    reissuanceSteps, maxDepth, null));
            }
            else
            {
                // Multiple outputs — largest is payment, rest is change
                var sorted = spentLeaves.OrderByDescending(l => l.AmountSat).ToList();
                var payment = sorted[0];
                var changeTotal = sorted.Skip(1).Sum(l => l.AmountSat);
                var fee = inputAmount - payment.AmountSat - changeTotal;
                flows.Add(new PaymentFlow(inputAmount, payment.AmountSat, changeTotal,
                    Math.Max(fee, 0), reissuanceSteps, maxDepth, null));
            }
        }

        return flows;
    }

    private static void CollectLeaves(
        CredentialEventEntity node,
        Dictionary<int, List<CredentialEventEntity>> childrenOf,
        List<CredentialEventEntity> leaves,
        ref int maxDepth,
        ref int reissuanceSteps)
    {
        if (node.GraphDepth > maxDepth)
            maxDepth = node.GraphDepth;

        if (node.EventType == "Reissued")
            reissuanceSteps++;

        if (!childrenOf.TryGetValue(node.Id, out var children) || children.Count == 0)
        {
            leaves.Add(node);
            return;
        }

        foreach (var child in children)
            CollectLeaves(child, childrenOf, leaves, ref maxDepth, ref reissuanceSteps);
    }
}
