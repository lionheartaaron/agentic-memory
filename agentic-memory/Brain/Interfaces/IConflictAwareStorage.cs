using AgenticMemory.Brain.Models;

namespace AgenticMemory.Brain.Interfaces;

/// <summary>
/// Interface for conflict-aware memory storage that handles duplicates and superseding states
/// </summary>
public interface IConflictAwareStorage
{
    /// <summary>
    /// Store a memory with conflict detection and resolution
    /// </summary>
    /// <param name="entity">The memory entity to store</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing the stored/reinforced memory and action taken</returns>
    Task<StoreResult> StoreAsync(MemoryNodeEntity entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get memory history for a specific tag, including superseded memories
    /// </summary>
    /// <param name="tag">The tag to get history for</param>
    /// <param name="includeArchived">Whether to include archived memories</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of memories with the specified tag, ordered by ValidFrom descending</returns>
    Task<IReadOnlyList<MemoryNodeEntity>> GetTagHistoryAsync(
        string tag,
        bool includeArchived = true,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a conflict-aware store operation
/// </summary>
public record StoreResult
{
    /// <summary>
    /// The memory that was stored or reinforced
    /// </summary>
    public required MemoryNodeEntity Memory { get; init; }

    /// <summary>
    /// The action that was taken
    /// </summary>
    public required StoreAction Action { get; init; }

    /// <summary>
    /// Memories that were superseded by this store operation
    /// </summary>
    public IReadOnlyList<MemoryNodeEntity> SupersededMemories { get; init; } = [];

    /// <summary>
    /// Human-readable message describing what happened
    /// </summary>
    public required string Message { get; init; }
}

/// <summary>
/// Actions that can be taken when storing a memory
/// </summary>
public enum StoreAction
{
    /// <summary>
    /// New memory was stored (no conflicts or duplicates found)
    /// </summary>
    StoredNew,

    /// <summary>
    /// New memory was stored and older memories with conflicting singular-state tags were superseded
    /// </summary>
    StoredWithSupersede,

    /// <summary>
    /// A very similar memory already existed and was reinforced instead
    /// </summary>
    ReinforcedExisting,

    /// <summary>
    /// Memory was stored and coexists with similar memories (non-singular tags)
    /// </summary>
    StoredCoexist
}
