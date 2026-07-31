using AgenticMemory.Brain.Models;

namespace AgenticMemory.Brain.Interfaces;

public interface IConflictAwareStorage
{
    /// <summary>Store within an explicit scope, attributing the write to <paramref name="actor"/>.</summary>
    Task<StoreResult> StoreAsync(
        MemoryNodeEntity entity,
        MemoryScope scope,
        string actor,
        CancellationToken cancellationToken = default);

    /// <summary>Convenience overload against <see cref="MemoryScope.Default"/>.</summary>
    Task<StoreResult> StoreAsync(MemoryNodeEntity entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Full history for a structured slot, newest first, including superseded values.
    /// Exact (subject, predicate) lookup — no similarity involved.
    /// </summary>
    Task<IReadOnlyList<MemoryNodeEntity>> GetSlotHistoryAsync(
        MemoryScope scope,
        string subjectRef,
        string predicate,
        CancellationToken cancellationToken = default);

    /// <summary>Tag-based history. Retained for compatibility; prefer the slot form.</summary>
    Task<IReadOnlyList<MemoryNodeEntity>> GetTagHistoryAsync(
        string tag,
        bool includeArchived = true,
        CancellationToken cancellationToken = default);
}
