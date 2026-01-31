using AgenticMemory.Brain.Models;

namespace AgenticMemory.Brain.Interfaces;

/// <summary>
/// Core storage interface for memory nodes with CRUD, search, stats, and maintenance methods
/// </summary>
public interface IMemoryRepository : IDisposable
{
    /// <summary>
    /// Get a memory node by its unique identifier
    /// </summary>
    Task<MemoryNodeEntity?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all memory nodes (use sparingly for large datasets)
    /// </summary>
    Task<IReadOnlyList<MemoryNodeEntity>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Save a memory node (insert or update)
    /// </summary>
    Task SaveAsync(MemoryNodeEntity node, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a memory node by its unique identifier
    /// </summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Search memory nodes by text (uses trigram fuzzy matching)
    /// </summary>
    Task<IReadOnlyList<MemoryNodeEntity>> SearchByTextAsync(string query, int limit = 10, CancellationToken cancellationToken = default);

    /// <summary>
    /// Search memory nodes by tags
    /// </summary>
    Task<IReadOnlyList<MemoryNodeEntity>> SearchByTagsAsync(IEnumerable<string> tags, int limit = 10, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get memory nodes with strength below a threshold (for pruning)
    /// </summary>
    Task<IReadOnlyList<MemoryNodeEntity>> GetWeakMemoriesAsync(double strengthThreshold, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get statistics about the memory repository
    /// </summary>
    Task<RepositoryStats> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reinforce a memory node (update access time and increase strength)
    /// </summary>
    Task ReinforceAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prune weak memories below the strength threshold
    /// </summary>
    Task<int> PruneWeakMemoriesAsync(double strengthThreshold, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compact and optimize the database
    /// </summary>
    Task CompactAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Statistics about the memory repository
/// </summary>
public record RepositoryStats
{
    public int TotalNodes { get; init; }
    public double AverageStrength { get; init; }
    public int WeakMemoriesCount { get; init; }
    public DateTime? OldestMemory { get; init; }
    public DateTime? NewestMemory { get; init; }
    public long DatabaseSizeBytes { get; init; }
}
