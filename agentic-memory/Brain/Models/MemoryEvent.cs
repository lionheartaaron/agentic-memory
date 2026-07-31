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
/// What actually happened when a caller tried to settle a contradiction.
///
/// This used to be a <c>bool</c>, which could only say "found" or "not found" and so had to treat
/// a nonsense request as a success. Every failure below is a caller mistake that was previously
/// indistinguishable from a correct resolution.
/// </summary>
public enum ConflictResolution
{
    /// <summary>A winner was chosen. The other side is now superseded.</summary>
    Resolved = 0,

    /// <summary>Both sides stand; the contradiction is no longer flagged.</summary>
    Dismissed = 1,

    /// <summary>No such conflict for this user.</summary>
    NotFound = 2,

    /// <summary>
    /// The winner named is not one of the two memories in the conflict. Refused, because the code
    /// that picks the loser is "whichever side is not the winner": an id belonging to neither would
    /// have superseded the new memory in favour of something unrelated and reported success.
    /// </summary>
    WinnerNotInConflict = 3,

    /// <summary>Neither a winner nor a dismissal. There is nothing to record.</summary>
    NoChoice = 4,

    /// <summary>
    /// Already resolved or dismissed. Settling it twice would supersede the first winner in favour
    /// of the second, leaving both sides superseded and no memory current for the slot. Restore the
    /// side that should have won instead.
    /// </summary>
    AlreadySettled = 5,
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

/// <summary>
/// One of the two memories a contradiction is between, reduced to what a decision needs.
///
/// Not the whole memory: choosing a side does not require the embedding or the access counters,
/// and a conflict list that carried them would be mostly noise for a caller paying by the token.
/// </summary>
/// <param name="State">
/// Which side is still current. A side already superseded by something else usually settles the
/// question on its own.
/// </param>
/// <param name="Source">Where it came from. A user statement outranks a companion's inference.</param>
public sealed record ConflictSide(
    Guid Id,
    string Title,
    string Summary,
    string? ValueKey,
    MemoryState State,
    MemorySource Source,
    double Confidence,
    DateTime CreatedAt,
    DateTime ValidFrom,
    bool IsPinned);

/// <summary>
/// A contradiction together with both memories it is about.
///
/// The conflict record alone names its two sides by id, which is enough to resolve one and not
/// nearly enough to decide how. Anything asked to choose would have to fetch both separately, and
/// a forgotten side cannot be fetched at all, so this exists to make "show me the contradiction"
/// and "let me choose" the same call.
/// </summary>
/// <param name="Existing">The memory already held. Null when the scope may not see it.</param>
/// <param name="New">The memory that arrived and contradicted it.</param>
public sealed record ConflictDetail(
    MemoryConflict Conflict,
    ConflictSide? Existing,
    ConflictSide? New);
