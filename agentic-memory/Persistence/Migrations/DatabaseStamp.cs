using LiteDB;

namespace AgenticMemory.Persistence.Migrations;

/// <summary>One completed migration, kept so a support question can be answered from the file alone.</summary>
public sealed class MigrationHistoryEntry
{
    public int      FromVersion      { get; set; }
    public int      ToVersion        { get; set; }
    public string   Name             { get; set; } = "";
    public int      DocumentsTouched { get; set; }
    public DateTime AppliedAt        { get; set; }

    /// <summary>The app version that ran it — the answer to "which release did this to my data".</summary>
    public string   AppVersion       { get; set; } = "";
}

/// <summary>
/// The database's record of itself: what schema version it is at, and which builds have opened it.
///
/// This document is authoritative for the schema version. LiteDB's own <c>UserVersion</c> pragma is
/// also kept in step, but only as a mirror for external tooling — it is written, never read, because
/// a pragma cannot be set inside the transaction that does the migration work and so could disagree
/// with what was actually committed.
///
/// Every field is optional on read. This is the one record that must stay readable by builds written
/// years apart, so fields may be added but never repurposed, and a missing field always means
/// "unknown", never an error.
/// </summary>
public sealed class DatabaseStamp
{
    public const string CollectionName = "_database";
    public const string DocumentId     = "stamp";

    /// <summary>Where the pre-<see cref="DatabaseStamp"/> scheme kept its version, adopted on first open.</summary>
    private const string LegacyCollectionName = "memory_schema";
    private const string LegacyDocumentId     = "schema_version";

    /// <summary>Trimmed on write. Long enough to cover an install's whole update history in practice.</summary>
    private const int MaxHistoryEntries = 50;

    public string Id { get; set; } = DocumentId;

    /// <summary>The only field anything branches on.</summary>
    public int SchemaVersion { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Null for a database that predates version stamping — genuinely unknown, not blank.</summary>
    public string? CreatedByAppVersion { get; set; }

    public DateTime LastOpenedAt          { get; set; }
    public string?  LastOpenedByAppVersion { get; set; }

    public List<MigrationHistoryEntry> History { get; set; } = [];

    /// <summary>The app version that last changed the schema — who to name when a database is too new.</summary>
    public string? LastMigratedByAppVersion =>
        History.Count > 0 ? History[^1].AppVersion : null;

    /// <summary>Returns null when the database has never been stamped.</summary>
    public static DatabaseStamp? Read(LiteDatabase database)
    {
        try
        {
            return database.GetCollection<DatabaseStamp>(CollectionName).FindById(DocumentId);
        }
        catch (Exception)
        {
            // A stamp that will not deserialize is still worth something: the schema version is the
            // one field that decides whether this build may touch the data, so recover it from the
            // raw document rather than treat the database as unversioned and re-run every migration.
            return ReadRaw(database);
        }
    }

    private static DatabaseStamp? ReadRaw(LiteDatabase database)
    {
        var document = database.GetCollection(CollectionName).FindById(DocumentId);
        if (document is null) return null;

        return new DatabaseStamp
        {
            SchemaVersion = document.TryGetValue(nameof(SchemaVersion), out var version) && version.IsNumber
                ? version.AsInt32
                : DatabaseSchema.Baseline,
        };
    }

    /// <summary>
    /// The version recorded by the scheme that predates this one, or null if there isn't one.
    ///
    /// Adopting it matters: without this, an install that already ran the old scoped-memory migration
    /// looks unversioned and would run it a second time — and that particular step is not safe to
    /// repeat, because the archived flag it reads has by then already been removed from the
    /// documents, so every archived memory would come back as active.
    /// </summary>
    public static int? ReadLegacyVersion(LiteDatabase database)
    {
        try
        {
            var document = database.GetCollection(LegacyCollectionName).FindById(LegacyDocumentId);
            if (document is not null && document.TryGetValue("value", out var value) && value.IsNumber)
                return value.AsInt32;
        }
        catch (Exception)
        {
            // Unreadable legacy stamp: fall through to the baseline and let the steps run.
        }

        return null;
    }

    public static void Write(LiteDatabase database, DatabaseStamp stamp)
    {
        if (stamp.History.Count > MaxHistoryEntries)
            stamp.History = stamp.History.TakeLast(MaxHistoryEntries).ToList();

        database.GetCollection<DatabaseStamp>(CollectionName).Upsert(DocumentId, stamp);
    }
}
