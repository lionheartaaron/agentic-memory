namespace AgenticMemory.Brain.Models;

public record ScoredMemory
{
    public required MemoryNodeEntity Memory { get; init; }

    /// <summary>Fused relevance, normalised to [0, 1] against the best result in this query.</summary>
    public double Score { get; init; }

    public double FuzzyScore { get; init; }
    public double StrengthScore { get; init; }
    public double RecencyScore { get; init; }

    /// <summary>Raw cosine similarity in [-1, 1], or null when this memory's vector was not
    /// comparable to the query's (missing, or produced by a different model).</summary>
    public double? SemanticScore { get; init; }

    /// <summary>Which retrieval channels surfaced this memory. Agreement across channels is the
    /// main input to the retrieval confidence reported with the result set.</summary>
    public IReadOnlyList<string> MatchedChannels { get; init; } = [];

    /// <summary>True when this came from the always-on core context rather than the query.</summary>
    public bool IsCoreContext { get; init; }

    /// <summary>
    /// How many previous turns <em>this companion</em> has already drawn on this memory. Zero means
    /// it is new to her, whatever the other companions have said. Lets a companion introduce a fact
    /// once and refer back to it afterwards, instead of announcing it every time it is relevant.
    /// </summary>
    public int TimesSurfacedToCompanion { get; init; }

    /// <summary>When this companion last used the memory, or null if she never has.</summary>
    public DateTime? LastSurfacedToCompanionAt { get; init; }
}
