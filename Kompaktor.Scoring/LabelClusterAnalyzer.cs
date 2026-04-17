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
                foreach (var utxo in utxos)
                {
                    if (utxo.AddressId.ToString() == label.EntityId && utxoLabels.TryGetValue(utxo.Id, out var set))
                        set.Add(label.Text);
                }
            }
        }

        // Union-find for clustering UTXOs by shared labels
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

        // UTXOs sharing a label text are clustered together
        var labelToUtxos = new Dictionary<string, List<int>>();
        foreach (var (utxoId, labelSet) in utxoLabels)
        {
            foreach (var labelText in labelSet)
            {
                if (!labelToUtxos.TryGetValue(labelText, out var list))
                {
                    list = [];
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

        // Build clusters from union-find groups
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
