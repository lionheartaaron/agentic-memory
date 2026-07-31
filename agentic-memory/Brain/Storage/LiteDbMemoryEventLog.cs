using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Persistence;
using LiteDB;

namespace AgenticMemory.Brain.Storage;

/// <summary>
/// LiteDB-backed append-only event log.
///
/// Appends deliberately participate in whatever transaction the caller has open, so an event and
/// the state change it describes commit together or not at all.
/// </summary>
public sealed class LiteDbMemoryEventLog : IMemoryEventLog
{
    public const string CollectionName = "memory_events";

    private readonly ILiteCollection<MemoryEvent> _collection;
    private long _sequence;

    public LiteDbMemoryEventLog(SharedLiteDatabase sharedDb)
    {
        _collection = sharedDb.Database.GetCollection<MemoryEvent>(CollectionName);
        _collection.EnsureIndex(x => x.MemoryId);
        _collection.EnsureIndex(x => x.UserId);
        _collection.EnsureIndex(x => x.Sequence);
        _collection.EnsureIndex(x => x.Timestamp);

        // Resume the counter where the last run left off.
        var last = _collection.Query().OrderByDescending(x => x.Sequence).FirstOrDefault();
        _sequence = last?.Sequence ?? 0;
    }

    public long NextSequence() => Interlocked.Increment(ref _sequence);

    public void Append(MemoryEvent memoryEvent)
    {
        if (memoryEvent.Sequence == 0)
            memoryEvent.Sequence = NextSequence();

        memoryEvent.MemoryTitle = MemoryTextIndexer.SanitizeForLiteDb(memoryEvent.MemoryTitle);
        memoryEvent.Detail      = memoryEvent.Detail is null
            ? null
            : MemoryTextIndexer.SanitizeForLiteDb(memoryEvent.Detail);

        _collection.Insert(memoryEvent);
    }

    public void AppendMany(IEnumerable<MemoryEvent> events)
    {
        foreach (var e in events)
            Append(e);
    }

    public Task<IReadOnlyList<MemoryEvent>> GetForMemoryAsync(Guid memoryId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = _collection.Find(x => x.MemoryId == memoryId)
            .OrderBy(x => x.Sequence)
            .ToList();
        return Task.FromResult<IReadOnlyList<MemoryEvent>>(results);
    }

    public Task<IReadOnlyList<MemoryEvent>> GetRecentAsync(
        MemoryScope scope, int limit = 100, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = scope.UserId;
        var results = _collection.Find(x => x.UserId == userId)
            .OrderByDescending(x => x.Sequence)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<MemoryEvent>>(results);
    }
}
