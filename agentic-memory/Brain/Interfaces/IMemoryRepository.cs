using AgenticMemory.Brain.Models;

namespace AgenticMemory.Brain.Interfaces;

/// <summary>
/// Scoped storage for memories.
///
/// Every read requires a <see cref="MemoryScope"/>. That is structural, not stylistic: it makes an
/// accidentally-unscoped query impossible to write, because there is no overload that omits it.
/// Cross-user and administrative access lives on <see cref="IMemoryAdminStore"/>, where it has to
/// be asked for by name.
/// </summary>
public interface IMemoryRepository : IDisposable
{
    /// <summary>Fetch by id, returning null if the memory exists but is outside the scope.</summary>
    Task<MemoryNodeEntity?> GetAsync(Guid id, MemoryScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every memory visible in this scope, subject to <paramref name="options"/>.
    ///
    /// The user predicate is pushed into the storage query; visibility and expiry are evaluated
    /// over that already user-bounded set and, crucially, before any limit is applied.
    /// </summary>
    Task<IReadOnlyList<MemoryNodeEntity>> QueryAsync(
        MemoryScope scope,
        MemoryQueryOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>The active, non-expired candidate set for a scope. The retrieval pipeline's input.</summary>
    Task<IReadOnlyList<MemoryNodeEntity>> GetActiveAsync(MemoryScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// All memories asserting a given (subject, predicate) pair — the exact-match channel used by
    /// both retrieval and the supersede gate.
    /// </summary>
    Task<IReadOnlyList<MemoryNodeEntity>> GetBySlotAsync(
        MemoryScope scope,
        string subjectRef,
        string predicate,
        bool includeHistory = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The slot's value as it stood at <paramref name="asOf"/>, rather than as it stands now.
    /// Superseded assertions are included when their validity window covers that instant.
    /// </summary>
    Task<IReadOnlyList<MemoryNodeEntity>> GetBySlotAsync(
        MemoryScope scope,
        string subjectRef,
        string predicate,
        bool includeHistory,
        DateTime? asOf,
        CancellationToken cancellationToken = default);

    Task<RepositoryStats> GetStatsAsync(MemoryScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply a set of mutations atomically. Supersede-then-insert must go through here: applied
    /// separately, a crash between the two loses the fact entirely.
    /// </summary>
    Task<MemoryWriteResult> ExecuteAsync(
        MemoryWriteBatch batch,
        string actor,
        CancellationToken cancellationToken = default);

    /// <summary>Convenience wrapper over <see cref="ExecuteAsync"/> for a single write.</summary>
    Task SaveAsync(MemoryNodeEntity node, CancellationToken cancellationToken = default);

    /// <summary>Guarded single write. Throws <see cref="MemoryConcurrencyException"/> if the
    /// memory changed since <paramref name="expectedVersion"/> was observed.</summary>
    Task SaveAsync(MemoryNodeEntity node, long expectedVersion, string actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Record a retrieval. Deliberately not version-guarded and not event-logged: this fires on
    /// every search hit, and it only touches ranking signals.
    /// </summary>
    Task ReinforceAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Batch form, so a search costs one write rather than one per hit.</summary>
    Task ReinforceManyAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-delete: marks the memory <see cref="MemoryState.Forgotten"/> and records an event.
    /// Physical removal happens only via <see cref="IMemoryAdminStore.PurgeForgottenAsync"/>.
    /// </summary>
    Task<bool> ForgetAsync(Guid id, MemoryScope scope, string actor, CancellationToken cancellationToken = default);

    /// <summary>Undo an archive, supersede or forget.</summary>
    Task<bool> RestoreAsync(Guid id, MemoryScope scope, string actor, CancellationToken cancellationToken = default);

    // ── Per-companion awareness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Record that this scope's companion has drawn on these memories in a turn. A no-op for a
    /// scope with no companion. Never bumps <see cref="MemoryNodeEntity.Version"/>: surfacing is not
    /// a change to the memory.
    /// </summary>
    Task RecordSurfacedAsync(MemoryScope scope, IEnumerable<Guid> memoryIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// What this scope's companion has already said, for the given memories. Empty for a scope with
    /// no companion.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, MemoryAwareness>> GetAwarenessAsync(
        MemoryScope scope, IEnumerable<Guid> memoryIds, CancellationToken cancellationToken = default);

    /// <summary>Open contradictions awaiting a companion or user decision.</summary>
    Task<IReadOnlyList<MemoryConflict>> GetConflictsAsync(
        MemoryScope scope,
        bool openOnly = true,
        CancellationToken cancellationToken = default);

    Task<bool> ResolveConflictAsync(
        Guid conflictId, MemoryScope scope, Guid? winnerId, bool dismissed, string actor,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of an atomic batch.</summary>
public sealed record MemoryWriteResult
{
    public int Inserted { get; init; }
    public int Updated { get; init; }
    public int StateChanged { get; init; }
    public int ConflictsRecorded { get; init; }
}
