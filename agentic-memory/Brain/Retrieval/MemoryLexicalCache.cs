using System.Collections.Concurrent;
using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Search;

namespace AgenticMemory.Brain.Retrieval;

/// <summary>
/// Caches the tokenized, field-weighted term map for each memory.
///
/// Tokenizing title, summary, content and tags on every query would re-analyse the entire scoped set
/// per keystroke of a live search. Keyed on <see cref="MemoryNodeEntity.Version"/> for the same
/// reason as <see cref="MemoryVectorCache"/>: the version is bumped by every content change and
/// deliberately left alone by reinforcement, so an entry cannot go stale while a memory is merely
/// being re-read.
/// </summary>
public sealed class MemoryLexicalCache
{
    private readonly ConcurrentDictionary<Guid, Entry> _cache = new();
    private readonly int _maxEntries;

    public MemoryLexicalCache(int maxEntries = 50_000) => _maxEntries = maxEntries;

    private readonly record struct Entry(long Version, Bm25Ranker.Document Document);

    public Bm25Ranker.Document Get(MemoryNodeEntity memory)
    {
        if (_cache.TryGetValue(memory.Id, out var entry) && entry.Version == memory.Version)
            return entry.Document;

        var document = Bm25Ranker.Analyze(memory);

        if (_cache.Count >= _maxEntries)
            _cache.Clear();

        _cache[memory.Id] = new Entry(memory.Version, document);
        return document;
    }

    public void Invalidate(Guid id) => _cache.TryRemove(id, out _);

    public void Clear() => _cache.Clear();

    public int Count => _cache.Count;
}
