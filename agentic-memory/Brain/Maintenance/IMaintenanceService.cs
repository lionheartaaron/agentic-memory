using AgenticMemory.Configuration;

namespace AgenticMemory.Brain.Maintenance;

/// <summary>
/// Background upkeep. Nothing here deletes a memory: the only physical removal in the system is
/// <c>PurgeForgotten</c>, which acts solely on memories the user explicitly asked to forget.
/// </summary>
public interface IMaintenanceService
{
    /// <summary>
    /// Periodic upkeep: expire ephemeral memories whose <c>ExpiresAt</c> has passed, and move
    /// long-untouched episodic memories to cold storage. Both transitions are archival and
    /// reversible.
    /// </summary>
    Task<UpkeepResult> RunUpkeepAsync(MaintenanceSettings? settings = null, CancellationToken cancellationToken = default);

    /// <summary>Consolidate near-duplicate memories, keeping the most current and best-attested.</summary>
    Task<ConsolidationResult> ConsolidateMemoriesAsync(double similarityThreshold = 0.9, CancellationToken cancellationToken = default);

    /// <summary>Rebuild derived search fields and regenerate embeddings whose model stamp is stale.</summary>
    Task<ReindexResult> ReindexAsync(bool force = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Physically remove memories tombstoned as forgotten longer ago than the retention window.
    /// The audit event outlives the row.
    /// </summary>
    Task<PurgeResult> PurgeForgottenAsync(TimeSpan retention, CancellationToken cancellationToken = default);

    Task<CompactResult> CompactDatabaseAsync(CancellationToken cancellationToken = default);

    MaintenanceStatus GetStatus();
}

public record UpkeepResult
{
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public int MemoriesProcessed { get; init; }

    /// <summary>Ephemeral memories past their expiry, archived.</summary>
    public int Expired { get; init; }

    /// <summary>Episodic memories moved to cold storage. Still queryable; never deleted.</summary>
    public int ArchivedToCold { get; init; }

    /// <summary>Always zero. Retained so existing dashboards keep binding; upkeep cannot delete.</summary>
    public int MemoriesDeleted => 0;

    public double AverageStrength { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public record ConsolidationResult
{
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public int MemoriesAnalyzed { get; init; }
    public int MemoriesMerged { get; init; }
    public int MemoriesArchived { get; init; }
    public int ClustersFound { get; init; }
    public int ComparisonsPerformed { get; init; }

    /// <summary>Snapshot taken immediately before the merge, when backups are enabled.</summary>
    public string? SnapshotPath { get; init; }

    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public record ReindexResult
{
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public int MemoriesReindexed { get; init; }
    public int EmbeddingsGenerated { get; init; }

    /// <summary>Vectors found to be from a different model or text recipe and rebuilt.</summary>
    public int StaleEmbeddingsReplaced { get; init; }

    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public record PurgeResult
{
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public int MemoriesPurged { get; init; }

    /// <summary>Snapshot taken immediately before the purge, when backups are enabled. This is the
    /// only operation in the system that destroys data, so the path is surfaced to the caller.</summary>
    public string? SnapshotPath { get; init; }

    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public record CompactResult
{
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public long SizeBeforeBytes { get; init; }
    public long SizeAfterBytes { get; init; }
    public double SpaceSavedPercent { get; init; }

    /// <summary>Snapshot taken immediately before the rebuild, when backups are enabled.</summary>
    public string? SnapshotPath { get; init; }

    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public record MaintenanceStatus
{
    public DateTime? LastUpkeepRun { get; init; }
    public DateTime? LastConsolidationRun { get; init; }
    public DateTime? LastReindexRun { get; init; }
    public DateTime? LastCompactRun { get; init; }
    public DateTime? LastPurgeRun { get; init; }
    public bool IsRunning { get; init; }
    public string? CurrentOperation { get; init; }
}
