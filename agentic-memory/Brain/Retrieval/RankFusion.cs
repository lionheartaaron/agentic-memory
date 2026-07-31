namespace AgenticMemory.Brain.Retrieval;

/// <summary>A single retrieval channel's ranked output.</summary>
public sealed record RankedChannel(string Name, double Weight, IReadOnlyList<(Guid Id, double Score)> Ranked);

public static class RankFusion
{
    /// <summary>
    /// Standard RRF damping constant. Large enough that the difference between rank 1 and rank 2
    /// does not swamp agreement between channels.
    /// </summary>
    public const double K = 60.0;

    /// <summary>
    /// Reciprocal Rank Fusion.
    ///
    /// Combines channel <em>rankings</em> rather than channel <em>scores</em>, which is what makes it
    /// the right tool here: the previous weighted sum mixed a cosine similarity whose floor for
    /// unrelated text is ~0.5 with a 0.0 sentinel meaning "no embedding stored". Under that scheme
    /// an irrelevant memory that happened to have a vector outranked a perfectly relevant one that
    /// did not. Ranks are ordinal, so no such cross-scale arithmetic is possible, and no weight
    /// re-tuning is needed when a channel is added.
    /// </summary>
    public static Dictionary<Guid, (double Score, List<string> Channels)> Fuse(IEnumerable<RankedChannel> channels)
    {
        var fused = new Dictionary<Guid, (double Score, List<string> Channels)>();

        foreach (var channel in channels)
        {
            for (var rank = 0; rank < channel.Ranked.Count; rank++)
            {
                var id           = channel.Ranked[rank].Id;
                var contribution = channel.Weight / (K + rank + 1);

                if (fused.TryGetValue(id, out var existing))
                {
                    existing.Channels.Add(channel.Name);
                    fused[id] = (existing.Score + contribution, existing.Channels);
                }
                else
                {
                    fused[id] = (contribution, [channel.Name]);
                }
            }
        }

        return fused;
    }

    /// <summary>
    /// Maximal Marginal Relevance: greedily pick the item that is most relevant while least similar
    /// to what has already been picked. Without it, a topically dense store returns several
    /// paraphrases of one fact and wastes the turn's context budget.
    /// </summary>
    /// <param name="lambda">1.0 is pure relevance; lower trades relevance for coverage.</param>
    public static List<T> Diversify<T>(
        IReadOnlyList<T> items,
        Func<T, double> relevance,
        Func<T, T, double> similarity,
        int take,
        double lambda)
    {
        if (items.Count <= 1 || take <= 1)
            return items.Take(Math.Max(0, take)).ToList();

        lambda = Math.Clamp(lambda, 0.0, 1.0);

        var remaining = items.ToList();
        var selected  = new List<T>(Math.Min(take, remaining.Count));

        // Seed with the most relevant item.
        var seedIndex = 0;
        for (var i = 1; i < remaining.Count; i++)
            if (relevance(remaining[i]) > relevance(remaining[seedIndex])) seedIndex = i;

        selected.Add(remaining[seedIndex]);
        remaining.RemoveAt(seedIndex);

        while (selected.Count < take && remaining.Count > 0)
        {
            var bestIndex = -1;
            var bestScore = double.NegativeInfinity;

            for (var i = 0; i < remaining.Count; i++)
            {
                var maxSim = 0.0;
                foreach (var s in selected)
                    maxSim = Math.Max(maxSim, similarity(remaining[i], s));

                var score = lambda * relevance(remaining[i]) - (1 - lambda) * maxSim;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0) break;
            selected.Add(remaining[bestIndex]);
            remaining.RemoveAt(bestIndex);
        }

        return selected;
    }
}
