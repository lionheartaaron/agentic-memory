namespace AgenticMemory.Brain.Models;

public record StoreResult
{
    public required MemoryNodeEntity Memory { get; init; }
    public required StoreAction Action { get; init; }
    public IReadOnlyList<MemoryNodeEntity> SupersededMemories { get; init; } = [];
    public required string Message { get; init; }
}

public enum StoreAction
{
    StoredNew,
    StoredWithSupersede,
    ReinforcedExisting,
    StoredCoexist,
}
