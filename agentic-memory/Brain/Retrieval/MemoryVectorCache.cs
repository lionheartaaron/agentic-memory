using System.Collections.Concurrent;
using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Search;

namespace AgenticMemory.Brain.Retrieval;

/// <summary>
/// Caches decoded, unit-length embedding vectors.
///
/// <see cref="MemoryNodeEntity.GetEmbedding"/> allocates a fresh float array from the stored bytes
/// on every call; scoring a few thousand candidates per search made that the dominant cost.
/// Vectors are scaled to unit length once on the way in, so every subsequent comparison is a single
/// SIMD dot product rather than a cosine with two square roots.
///
/// Correctness rests on <see cref="MemoryNodeEntity.Version"/>: it is incremented for every
/// persisted content change but deliberately left alone by reinforcement, so (id, version) is a
/// stable identity for a vector and cannot go stale while a memory is merely being re-read.
/// </summary>
public sealed class MemoryVectorCache
{
    private readonly ConcurrentDictionary<Guid, Entry> _cache = new();
    private readonly int _maxEntries;

    public MemoryVectorCache(int maxEntries = 50_000) => _maxEntries = maxEntries;

    private readonly record struct Entry(long Version, float[]? Vector);

    /// <summary>
    /// Returns the memory's vector scaled to unit length, or null when it has none or its vector was
    /// produced by a different model or text recipe and is therefore not comparable. Compare results
    /// with <see cref="VectorMath.TryUnitSimilarity"/>, never with a plain cosine — the magnitudes
    /// have already been divided out.
    /// </summary>
    public float[]? Get(MemoryNodeEntity memory, string? modelStamp, int dimensions)
    {
        if (!memory.HasComparableEmbedding(modelStamp, dimensions))
            return null;

        if (_cache.TryGetValue(memory.Id, out var entry) && entry.Version == memory.Version)
            return entry.Vector;

        var raw = memory.GetEmbedding();
        var vector = raw is null ? null : VectorMath.Normalize(raw);

        if (_cache.Count >= _maxEntries)
            _cache.Clear();

        _cache[memory.Id] = new Entry(memory.Version, vector);
        return vector;
    }

    public void Invalidate(Guid id) => _cache.TryRemove(id, out _);

    public void Clear() => _cache.Clear();

    public int Count => _cache.Count;
}
