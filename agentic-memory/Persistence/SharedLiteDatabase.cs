using AgenticMemory.Brain.Models;
using AgenticMemory.CodeIndex;
using LiteDB;

namespace AgenticMemory.Persistence;

/// <summary>
/// Single LiteDatabase instance shared across all repositories.
///
/// WHY DIRECT MODE: ConnectionType.Shared re-opens the file and acquires a named OS mutex on
/// every operation, serializing all DB access across threads. With three separate LiteDatabase
/// instances (memory, kv, code-index) all on the same file, every HTTP request and every
/// background worker contended for that one mutex. DirectMode holds the file open and uses a
/// lightweight in-process ReaderWriterLockSlim — reads run concurrently, writes are exclusive.
///
/// WHY ONE INSTANCE: LiteDB Direct mode requires exactly one LiteDatabase per file per process.
/// Multiple Direct-mode instances on the same file corrupt the journal. This singleton enforces
/// that invariant through DI lifetime management.
/// </summary>
public sealed class SharedLiteDatabase : IDisposable
{
    public LiteDatabase Database { get; }
    public string DatabasePath  { get; }

    private bool _disposed;

    public SharedLiteDatabase(string databasePath)
    {
        DatabasePath = databasePath;

        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var mapper = new BsonMapper();
        mapper.Entity<MemoryNodeEntity>().Id(x => x.Id);
        mapper.Entity<CodeIndexRecord>().Id(x => x.Id);

        Database = new LiteDatabase(new ConnectionString
        {
            Filename   = databasePath,
            Connection = ConnectionType.Direct,
        }, mapper);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Database.Dispose();
    }
}
