namespace AgenticMemory.Brain.Models;

public record StoreResult
{
    public required MemoryNodeEntity Memory { get; init; }
    public required StoreAction Action { get; init; }

    /// <summary>Memories this one legally replaced. They are retained as history, not deleted.</summary>
    public IReadOnlyList<MemoryNodeEntity> SupersededMemories { get; init; } = [];

    /// <summary>Contradictions recorded rather than resolved. Both sides stay active.</summary>
    public IReadOnlyList<MemoryConflict> Conflicts { get; init; } = [];

    public required string Message { get; init; }
}

public enum StoreAction
{
    StoredNew,
    StoredWithSupersede,
    ReinforcedExisting,
    StoredCoexist,

    /// <summary>Stored, and a contradiction was recorded for a companion or the user to settle.</summary>
    StoredWithConflict,
}
