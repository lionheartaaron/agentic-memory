using AgenticMemory.Brain.Interfaces;
using LiteDB;

namespace AgenticMemory.Brain.Storage;

public class LiteDbKeyValueStore : IKeyValueStore, IDisposable
{
    private const string CollectionName = "kv";

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<KvEntry> _col;
    private bool _disposed;

    public LiteDbKeyValueStore(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase(new ConnectionString
        {
            Filename   = databasePath,
            Connection = ConnectionType.Shared,
        }, new BsonMapper());

        _col = _db.GetCollection<KvEntry>(CollectionName);
    }

    public string? Get(string key) => _col.FindById(key)?.Value;

    public void Set(string key, string value) =>
        _col.Upsert(new KvEntry { Id = key, Value = value, UpdatedAt = DateTime.UtcNow });

    public void Delete(string key) => _col.Delete(key);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _db.Dispose();
    }
}

internal sealed class KvEntry
{
    public string   Id        { get; set; } = "";
    public string   Value     { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}
