using AgenticMemory.Brain.Models;

namespace AgenticMemory.Brain.Interfaces;

/// <summary>
/// High-level search service with multi-signal scoring
/// </summary>
public interface ISearchService
{
    /// <summary>
    /// Search for memory nodes using multi-signal scoring
    /// </summary>
    /// <param name="query">The search query text</param>
    /// <param name="topN">Maximum number of results to return</param>
    /// <param name="tags">Optional tag filters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Scored search results</returns>
    Task<IReadOnlyList<ScoredMemory>> SearchAsync(
        string query,
        int topN = 5,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A memory node with its computed relevance score
/// </summary>
public record ScoredMemory
{
    public required MemoryNodeEntity Memory { get; init; }
    public double Score { get; init; }
    public double FuzzyScore { get; init; }
    public double StrengthScore { get; init; }
    public double RecencyScore { get; init; }
    public double SemanticScore { get; init; }
}
