using LiteDB;

namespace AgenticMemory.Brain.Models;

public enum MemoryEventType
{
    Created = 0,
    Updated = 1,
    Superseded = 2,
    Archived = 3,
    Restored = 4,
    Forgotten = 5,
    Merged = 6,
    Expired = 7,
    ConflictRecorded = 8,

    /// <summary>Physical deletion. The event survives the row.</summary>
    Purged = 9,
}

/// <summary>
/// An append-only record of everything that has happened to a memory.
///
/// Current state is a projection of this log. It exists so that "why did she forget that?" has an
/// answer, so that destructive operations are reviewable, and so that an accidental supersede can
/// be undone. Reinforcement is deliberately <em>not</em> logged — every search reinforces, and the
/// resulting write volume would swamp the log without adding information.
/// </summary>
public sealed class MemoryEvent
{
    [BsonId]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Monotonic per-store ordering, independent of clock skew.</summary>
    public long Sequence { get; set; }

    public string UserId { get; set; } = MemoryScope.DefaultUserId;
    public Guid MemoryId { get; set; }
    public MemoryEventType Type { get; set; }

    /// <summary>What caused this: "mcp:store_memory", "api:PUT /api/memory", "maintenance:consolidation".</summary>
    public string Actor { get; set; } = "unknown";

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>The other memory involved, for supersede and merge events.</summary>
    public Guid? RelatedMemoryId { get; set; }

    /// <summary>Denormalised so the log stays readable after the memory itself is purged.</summary>
    public string? MemoryTitle { get; set; }

    public string? Detail { get; set; }
}

public enum ConflictKind
{
    /// <summary>Same singular slot, different values — the newer one won.</summary>
    ValueReplaced = 0,

    /// <summary>A soft-singular preference changed. Both kept; worth asking about.</summary>
    SoftPreferenceChange = 1,

    /// <summary>Something declared immutable (birthday, legal name) was contradicted.</summary>
    ImmutableViolation = 2,

    /// <summary>A companion-scoped memory contradicts a memory all companions share. Never
    /// resolved silently: letting it would erase knowledge from every other companion.</summary>
    CrossScopeContradiction = 3,

    /// <summary>An inferred memory contradicts something the user stated outright.</summary>
    ProvenanceDowngrade = 4,

    /// <summary>One memory denies what another asserts, with no structured slot on either side.
    /// Detected from wording and polarity rather than from the slot registry.</summary>
    PolarityContradiction = 5,
}

public enum ConflictStatus
{
    Open = 0,
    Resolved = 1,
    Dismissed = 2,
}

/// <summary>
/// A detected contradiction that was deliberately <em>not</em> resolved automatically.
///
/// Surfacing these is a feature rather than a failure: "Wait — I thought you were still at Acme?"
/// is better companion behaviour than silently picking a winner, and it converts a data-integrity
/// problem into a moment that reads as attentiveness.
/// </summary>
public sealed class MemoryConflict
{
    [BsonId]
    public Guid Id { get; set; } = Guid.NewGuid();

    public string UserId { get; set; } = MemoryScope.DefaultUserId;

    /// <summary>The incoming memory.</summary>
    public Guid NewMemoryId { get; set; }

    /// <summary>The memory it contradicts.</summary>
    public Guid ExistingMemoryId { get; set; }

    public string SubjectRef { get; set; } = SubjectRefs.User;
    public string? Predicate { get; set; }
    public ConflictKind Kind { get; set; }
    public ConflictStatus Status { get; set; } = ConflictStatus.Open;

    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    public Guid? WinnerId { get; set; }

    /// <summary>Human-readable, suitable for a companion to paraphrase back to the user.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Companion that observed the conflict, when it arose in a scoped context.</summary>
    public string? CompanionId { get; set; }
}
