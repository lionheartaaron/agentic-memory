namespace AgenticMemory.Brain.Models;

/// <summary>
/// Which companions may recall a memory.
/// </summary>
public enum MemoryVisibility
{
    /// <summary>Every companion belonging to the owning user knows this.</summary>
    Global = 0,

    /// <summary>Only the companions listed in <see cref="MemoryNodeEntity.CompanionIds"/> know this.
    /// A "private" memory is simply a scoped memory with a single companion.</summary>
    Scoped = 1,
}

/// <summary>
/// Lifecycle state. Replaces the old boolean <c>IsArchived</c> flag so that "archived because a
/// newer fact replaced it" is distinguishable from "the user asked us to forget it".
/// </summary>
public enum MemoryState
{
    Active = 0,
    Superseded = 1,
    Archived = 2,

    /// <summary>User explicitly asked to forget this. Tombstoned; purged only by an explicit
    /// retention job, and the purge is itself recorded as an event.</summary>
    Forgotten = 3,

    /// <summary>Folded into a consolidated summary memory. The original is retained.</summary>
    Merged = 4,
}

/// <summary>
/// Determines which lifecycle rules apply. The default (<see cref="Semantic"/>) is deliberately
/// the safest value: durable, never automatically removed.
/// </summary>
public enum MemoryType
{
    /// <summary>A general durable fact. Never auto-removed.</summary>
    Semantic = 0,

    /// <summary>Core identity: name, birthday, family. Never auto-removed, always in the core set.</summary>
    Identity = 1,

    /// <summary>Likes and dislikes. Never auto-removed.</summary>
    Preference = 2,

    /// <summary>A companion's own traits. Immutable — conversation can never supersede it.</summary>
    Persona = 3,

    /// <summary>An event or conversation. Decays in ranking and may be consolidated into a
    /// summary, but the original is archived rather than deleted.</summary>
    Episodic = 4,

    /// <summary>Emotional state or relationship warmth. Slow decay, never auto-removed.</summary>
    Affective = 5,

    /// <summary>Short-lived context ("the user is on a train right now"). The only type for
    /// which <see cref="MemoryNodeEntity.ExpiresAt"/> is meaningful.</summary>
    Ephemeral = 6,
}

/// <summary>
/// Who asserted a memory. Used to arbitrate conflicts: a companion's inference must never
/// silently overwrite something the user stated outright.
/// </summary>
public enum MemorySource
{
    UserStated = 0,
    Imported = 1,
    SystemDerived = 2,
    CompanionInferred = 3,
}

/// <summary>
/// Controls redaction, export and retrieval of intimate disclosures.
/// </summary>
public enum Sensitivity
{
    Normal = 0,
    Sensitive = 1,
    Restricted = 2,
}

public static class MemoryEnumExtensions
{
    /// <summary>
    /// Trust ranking used by the supersede gate. Declared explicitly rather than relying on
    /// enum ordinal values so that adding a source cannot silently reorder trust.
    /// </summary>
    public static int TrustRank(this MemorySource source) => source switch
    {
        MemorySource.UserStated        => 100,
        MemorySource.Imported          => 70,
        MemorySource.SystemDerived     => 50,
        MemorySource.CompanionInferred => 30,
        _                              => 0,
    };

    /// <summary>
    /// Whether background maintenance may ever archive a memory of this type. Only episodic and
    /// ephemeral memories age out; facts, preferences, persona and affect are kept indefinitely.
    /// </summary>
    public static bool IsAgeable(this MemoryType type) =>
        type is MemoryType.Episodic or MemoryType.Ephemeral;

    /// <summary>
    /// Whether memories of this type belong in the always-loaded core context for a turn.
    /// </summary>
    public static bool IsCoreContext(this MemoryType type) =>
        type is MemoryType.Identity or MemoryType.Persona;
}
