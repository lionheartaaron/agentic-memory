using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Persistence;
using LiteDB;

namespace AgenticMemory.Brain.Storage;

public class LiteDbKeyValueStore : IKeyValueStore
{
    private const string CollectionName = "kv";

    private readonly ILiteCollection<KvEntry> _col;

    public LiteDbKeyValueStore(SharedLiteDatabase sharedDb)
    {
        _col = sharedDb.Database.GetCollection<KvEntry>(CollectionName);
    }

    public string? Get(string key) => _col.FindById(key)?.Value;

    public void Set(string key, string value) =>
        _col.Upsert(new KvEntry { Id = key, Value = value, UpdatedAt = DateTime.UtcNow });

    public void Delete(string key) => _col.Delete(key);
}

internal sealed class KvEntry
{
    public string   Id        { get; set; } = "";
    public string   Value     { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}
