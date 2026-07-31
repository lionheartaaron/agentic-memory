using AgenticMemory.Brain.Models;
using AgenticMemory.CodeIndex;
using AgenticMemory.Persistence.Migrations;
using LiteDB;
using Microsoft.Extensions.Logging;

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

    /// <summary>What the schema migration did on this open. See <see cref="DatabaseMigrator"/>.</summary>
    public DatabaseMigrationReport Migration { get; }

    private bool _disposed;

    /// <param name="backupDirectory">
    /// Where to put the snapshot taken before a schema migration. Null skips it — acceptable for a
    /// throwaway database, not for a user's.
    /// </param>
    public SharedLiteDatabase(string databasePath, string? backupDirectory = null, ILogger? logger = null)
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

        // WHY UTC_DATE: LiteDB stores dates as UTC ticks but, by default, converts them to *local*
        // time on read. Every timestamp in this codebase is written as DateTime.UtcNow, so on any
        // machine not running at UTC they came back skewed by the local bias — quietly breaking
        // every comparison against DateTime.UtcNow: expiry checks, decay and recency windows,
        // cold-storage cutoffs, and the retention purge for forgotten memories.
        //
        // The stored bytes were always correct, so enabling this fixes reads with no data migration.
        Database.Pragma("UTC_DATE", true);

        // Here rather than in a repository constructor, which is where this used to live. Repositories
        // are resolved lazily and in whatever order the container decides, so "migrate on first use"
        // only holds for the one type that remembered to ask — every other collection was reachable
        // before its schema had been brought up to date. Doing it at the point the file is opened is
        // the only place that is true for all of them, and it is after the UTC_DATE pragma, so a step
        // comparing timestamps sees the same values the rest of the application will.
        Migration = DatabaseMigrator.Run(Database, databasePath, backupDirectory, logger);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Database.Dispose();
    }
}
