using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Retrieval;

namespace AgenticMemory.Brain.Interfaces;

/// <summary>
/// Multi-channel memory retrieval.
/// </summary>
public interface ISearchService
{
    /// <summary>
    /// Full retrieval: scope filter, parallel candidate channels, rank fusion, diversification,
    /// and a calibrated confidence so a caller can hedge instead of confabulating.
    /// </summary>
    Task<MemoryRetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Convenience overload against <see cref="MemoryScope.Default"/> — the single-user store.
    /// Still scoped; it simply targets the default user rather than bypassing scoping.
    /// </summary>
    Task<IReadOnlyList<ScoredMemory>> SearchAsync(
        string query,
        int topN = 5,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default);

    bool SemanticSearchAvailable { get; }
}
