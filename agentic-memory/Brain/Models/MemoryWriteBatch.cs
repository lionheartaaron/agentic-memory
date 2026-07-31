namespace AgenticMemory.Brain.Models;

/// <summary>An insert or update, optionally guarded by the version the caller last observed.</summary>
public sealed record MemoryUpsert(MemoryNodeEntity Entity, long? ExpectedVersion = null);

/// <summary>A lifecycle transition applied to an existing memory.</summary>
public sealed record MemoryStateChange(
    Guid Id,
    MemoryState NewState,
    Guid? SupersededBy = null,
    DateTime? ValidUntil = null,
    string? Detail = null);

/// <summary>
/// A set of mutations applied as one atomic unit.
///
/// The supersede path previously archived N existing memories with N separate saves and only then
/// wrote the replacement. A crash in between left the old fact archived and the new one absent —
/// the fact disappeared entirely. Batching makes that sequence all-or-nothing.
/// </summary>
public sealed class MemoryWriteBatch
{
    public List<MemoryUpsert> Upserts { get; } = [];
    public List<MemoryStateChange> StateChanges { get; } = [];
    public List<MemoryConflict> Conflicts { get; } = [];

    public MemoryWriteBatch Upsert(MemoryNodeEntity entity, long? expectedVersion = null)
    {
        Upserts.Add(new MemoryUpsert(entity, expectedVersion));
        return this;
    }

    public MemoryWriteBatch ChangeState(
        Guid id, MemoryState state, Guid? supersededBy = null, DateTime? validUntil = null, string? detail = null)
    {
        StateChanges.Add(new MemoryStateChange(id, state, supersededBy, validUntil, detail));
        return this;
    }

    public MemoryWriteBatch RecordConflict(MemoryConflict conflict)
    {
        Conflicts.Add(conflict);
        return this;
    }

    public bool IsEmpty => Upserts.Count == 0 && StateChanges.Count == 0 && Conflicts.Count == 0;
}

/// <summary>
/// Raised when a guarded upsert loses a race. Callers should re-read and retry rather than
/// overwrite: every search reinforces, so concurrent read/write on the same memory is routine.
/// </summary>
public sealed class MemoryConcurrencyException(Guid id, long expected, long actual)
    : Exception($"Memory {id} was modified concurrently (expected version {expected}, found {actual}).")
{
    public Guid MemoryId { get; } = id;
    public long ExpectedVersion { get; } = expected;
    public long ActualVersion { get; } = actual;
}
