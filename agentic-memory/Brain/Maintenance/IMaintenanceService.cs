namespace AgenticMemory.Brain.Maintenance;

/// <summary>
/// Interface for memory maintenance operations
/// </summary>
public interface IMaintenanceService
{
    /// <summary>
    /// Apply decay to all memory strengths and prune weak memories
    /// </summary>
    /// <param name="pruneThreshold">Memories with strength below this will be pruned</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the decay operation</returns>
    Task<DecayResult> ApplyDecayAsync(double pruneThreshold = 0.1, CancellationToken cancellationToken = default);

    /// <summary>
    /// Consolidate related memories to reduce redundancy
    /// </summary>
    /// <param name="similarityThreshold">Threshold for considering memories as similar (0.0-1.0)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the consolidation operation</returns>
    Task<ConsolidationResult> ConsolidateMemoriesAsync(double similarityThreshold = 0.8, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reindex all memories (regenerate trigrams and embeddings)
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the reindex operation</returns>
    Task<ReindexResult> ReindexAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Compact the database to reclaim space
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the compact operation</returns>
    Task<CompactResult> CompactDatabaseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the last time each maintenance operation was run
    /// </summary>
    MaintenanceStatus GetStatus();
}

/// <summary>
/// Result of a decay operation
/// </summary>
public record DecayResult
{
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public int MemoriesProcessed { get; init; }
    public int MemoriesPruned { get; init; }
    public double AverageStrengthBefore { get; init; }
    public double AverageStrengthAfter { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of a consolidation operation
/// </summary>
public record ConsolidationResult
{
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public int MemoriesAnalyzed { get; init; }
    public int MemoriesMerged { get; init; }
    public int MemoriesArchived { get; init; }
    public int ClustersFound { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of a reindex operation
/// </summary>
public record ReindexResult
{
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public int MemoriesReindexed { get; init; }
    public int EmbeddingsGenerated { get; init; }
    public int TrigramsGenerated { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of a database compact operation
/// </summary>
public record CompactResult
{
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public long SizeBeforeBytes { get; init; }
    public long SizeAfterBytes { get; init; }
    public double SpaceSavedPercent { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Status of maintenance operations
/// </summary>
public record MaintenanceStatus
{
    public DateTime? LastDecayRun { get; init; }
    public DateTime? LastConsolidationRun { get; init; }
    public DateTime? LastReindexRun { get; init; }
    public DateTime? LastCompactRun { get; init; }
    public bool IsRunning { get; init; }
    public string? CurrentOperation { get; init; }
}
