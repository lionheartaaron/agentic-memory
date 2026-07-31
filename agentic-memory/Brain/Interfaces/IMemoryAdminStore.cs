using AgenticMemory.Brain.Models;

namespace AgenticMemory.Brain.Interfaces;

/// <summary>
/// Unscoped access, for maintenance, the dashboard and export.
///
/// Kept separate from <see cref="IMemoryRepository"/> so that crossing the tenancy boundary is an
/// explicit act — a service that only takes IMemoryRepository cannot read another user's memories
/// even by mistake.
/// </summary>
public interface IMemoryAdminStore
{
    /// <summary>Streams every memory without materialising the collection. Maintenance passes use
    /// this instead of loading the entire store into memory.</summary>
    IEnumerable<MemoryNodeEntity> StreamAll(bool includeNonCurrent = true);

    Task<IReadOnlyList<MemoryNodeEntity>> GetAllAsync(bool includeNonCurrent = true, CancellationToken cancellationToken = default);

    Task<MemoryNodeEntity?> GetByIdUnscopedAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetUserIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>Aggregate statistics across all users. Counts are computed with LiteDB count
    /// queries rather than by deserialising every document.</summary>
    Task<RepositoryStats> GetGlobalStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Physically removes memories tombstoned as <see cref="MemoryState.Forgotten"/> longer ago
    /// than <paramref name="retention"/>. The only path in the system that deletes a memory, and
    /// each deletion is recorded as a <see cref="MemoryEventType.Purged"/> event that outlives it.
    /// </summary>
    Task<int> PurgeForgottenAsync(TimeSpan retention, string actor, CancellationToken cancellationToken = default);

    /// <summary>Administrative wipe, used by the dashboard reset endpoints.</summary>
    Task<int> DeleteAllAsync(CancellationToken cancellationToken = default);

    Task CompactAsync(CancellationToken cancellationToken = default);

    long GetDatabaseSizeBytes();
}

/// <summary>Append-only audit log. Writes go through the repository; this is the read surface.</summary>
public interface IMemoryEventLog
{
    /// <summary>Append within the caller's transaction, if one is open.</summary>
    void Append(MemoryEvent memoryEvent);

    void AppendMany(IEnumerable<MemoryEvent> events);

    Task<IReadOnlyList<MemoryEvent>> GetForMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryEvent>> GetRecentAsync(
        MemoryScope scope, int limit = 100, CancellationToken cancellationToken = default);

    long NextSequence();
}
