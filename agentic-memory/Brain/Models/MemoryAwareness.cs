using LiteDB;

namespace AgenticMemory.Brain.Models;

/// <summary>
/// What a particular companion has already brought up, per memory.
///
/// A shared fact is not shared knowledge. Two companions can both see "the user is allergic to
/// shellfish", but only one of them has actually mentioned it — and a companion that recounts the
/// same fact every few turns stops feeling like someone who remembers and starts feeling like a
/// database with a voice. That is the characteristic failure of companion apps, and no amount of
/// retrieval quality fixes it, because the memory <em>is</em> relevant every time.
///
/// Kept in its own collection rather than on the memory document on purpose. Surfacing is a
/// per-companion, high-frequency event; recording it on the memory itself would bump
/// <see cref="MemoryNodeEntity.Version"/> on every read, invalidating the vector and lexical caches
/// and turning each search into a write storm against rows other companions are also reading.
/// </summary>
public class MemoryAwareness
{
    /// <summary>"{memoryId}|{companionId}" — makes recording idempotent without a lookup index.</summary>
    [BsonId]
    public string Id { get; set; } = "";

    public Guid MemoryId { get; set; }
    public string UserId { get; set; } = MemoryScope.DefaultUserId;
    public string CompanionId { get; set; } = "";

    /// <summary>When this companion first used the memory in a turn.</summary>
    public DateTime FirstSurfacedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastSurfacedAt { get; set; } = DateTime.UtcNow;

    /// <summary>How many turns this companion has drawn on the memory.</summary>
    public int SurfaceCount { get; set; }

    public static string BuildId(Guid memoryId, string companionId) =>
        $"{memoryId:N}|{MemoryScope.NormalizeId(companionId) ?? ""}";
}
