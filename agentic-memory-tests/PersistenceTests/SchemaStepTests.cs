using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Storage;
using AgenticMemory.CodeIndex;
using AgenticMemory.Persistence.Migrations;
using AgenticMemory.Persistence.Migrations.Steps;
using AgenticMemoryTests.PlatformTests;
using LiteDB;

namespace AgenticMemoryTests.PersistenceTests;

/// <summary>
/// The individual steps, against documents shaped the way the build that wrote them left them.
///
/// A step gets exactly one chance on a given user's data, so what matters is not that it runs but
/// that it carries the meaning of the old document across. The v2 tests are all variations on one
/// question: after the upgrade, does this memory still say what it said before?
/// </summary>
public class SchemaStepTests
{
    private sealed class TempDatabase : IDisposable
    {
        private readonly TempDirectory _directory = new();

        public string       Path     { get; }
        public LiteDatabase Database { get; }

        public TempDatabase()
        {
            Path     = System.IO.Path.Combine(_directory.Path, "test.db");
            Database = new LiteDatabase(new ConnectionString
            {
                Filename   = Path,
                Connection = ConnectionType.Direct,
            });
        }

        public void Dispose()
        {
            Database.Dispose();
            _directory.Dispose();
        }
    }

    private static MigrationContext Context(TempDatabase db) => new(db.Database, logger: null);

    // ── v2: memories written before scoping ───────────────────────────────────────────────────

    /// <summary>A memory as an unscoped build wrote it: no user, no state, an IsArchived boolean.</summary>
    private static Guid InsertLegacyMemory(
        TempDatabase db, bool isArchived = false, DateTime? validUntil = null, byte[]? embedding = null)
    {
        var id = Guid.NewGuid();
        var document = new BsonDocument
        {
            ["_id"]        = id,
            ["Title"]      = "Prefers tea",
            ["Content"]    = "The user drinks tea, never coffee",
            ["IsArchived"] = isArchived,
            ["CreatedAt"]  = DateTime.UtcNow.AddDays(-30),
        };

        if (validUntil.HasValue) document["ValidUntil"]     = validUntil.Value;
        if (embedding is not null) document["EmbeddingBytes"] = embedding;

        db.Database.GetCollection(LiteDbMemoryRepository.CollectionName).Insert(document);
        return id;
    }

    private static MemoryNodeEntity Read(TempDatabase db, Guid id) =>
        db.Database
            .GetCollection<MemoryNodeEntity>(LiteDbMemoryRepository.CollectionName)
            .FindById(id);

    [Fact]
    public void AnArchivedMemoryDoesNotComeBackAsActive()
    {
        using var db = new TempDatabase();
        var id = InsertLegacyMemory(db, isArchived: true);

        new ScopedMemorySchemaStep().Apply(Context(db));

        // The archived flag is read from raw BSON before the typed mapper — which no longer knows
        // the field — gets a chance to drop it. Miss this and every memory the user chose to put
        // away reappears.
        Assert.Equal(MemoryState.Archived, Read(db, id).State);
    }

    [Fact]
    public void AnUnarchivedMemoryStaysActive()
    {
        using var db = new TempDatabase();
        var id = InsertLegacyMemory(db, isArchived: false);

        new ScopedMemorySchemaStep().Apply(Context(db));

        Assert.Equal(MemoryState.Active, Read(db, id).State);
    }

    [Fact]
    public void AReplacedMemoryIsSupersededRatherThanMerelyArchived()
    {
        using var db = new TempDatabase();
        var id = InsertLegacyMemory(db, isArchived: true, validUntil: DateTime.UtcNow.AddDays(-1));

        new ScopedMemorySchemaStep().Apply(Context(db));

        // ValidUntil only ever came from a supersede, and that is the more specific fact: this was
        // not put away, it was replaced by something newer.
        Assert.Equal(MemoryState.Superseded, Read(db, id).State);
    }

    [Fact]
    public void MemoriesWithNoOwnerLandOnTheDefaultUser()
    {
        using var db = new TempDatabase();
        var id = InsertLegacyMemory(db);

        new ScopedMemorySchemaStep().Apply(Context(db));

        var memory = Read(db, id);
        Assert.Equal(MemoryScope.DefaultUserId, memory.UserId);
        Assert.Equal(SubjectRefs.User, memory.SubjectRef);

        // Global, not Scoped: an unscoped memory was visible to everything, and narrowing it during
        // a migration would hide memories the user never asked to hide.
        Assert.Equal(MemoryVisibility.Global, memory.Visibility);
    }

    [Fact]
    public void EmbeddingDimensionsAreRecoveredFromTheStoredBytes()
    {
        using var db = new TempDatabase();
        var id = InsertLegacyMemory(db, embedding: new byte[384 * sizeof(float)]);

        new ScopedMemorySchemaStep().Apply(Context(db));

        // Never recorded before, but derivable — and without it the comparison guard treats every
        // existing vector as incomparable and silently loses semantic search over old memories.
        Assert.Equal(384, Read(db, id).EmbeddingDim);
    }

    [Fact]
    public void ContentBecomesSearchableUnderTheNewTextIndex()
    {
        using var db = new TempDatabase();
        var id = InsertLegacyMemory(db);

        new ScopedMemorySchemaStep().Apply(Context(db));

        // SearchText did not exist before and now carries full content. Left empty, these memories
        // are in the store but unfindable by the lexical channel.
        Assert.Contains("coffee", Read(db, id).SearchText);
    }

    [Fact]
    public void AMemoryThatNeverRecordedWhenItBecameTrueBackdatesToItsCreation()
    {
        using var db = new TempDatabase();
        var id = InsertLegacyMemory(db);

        new ScopedMemorySchemaStep().Apply(Context(db));

        var memory = Read(db, id);

        // Not the moment of the upgrade. ValidFrom is what as-of queries filter on, so stamping it
        // "now" would drop every pre-upgrade memory out of any question asked about an earlier date.
        Assert.Equal(memory.CreatedAt, memory.IngestedAt);
        Assert.Equal(memory.CreatedAt, memory.ValidFrom);
        Assert.Equal(1, memory.Version);
        Assert.True(memory.Confidence > 0);
    }

    [Fact]
    public void AMemoryThatDidRecordThoseTimesKeepsThem()
    {
        using var db = new TempDatabase();

        var id       = Guid.NewGuid();
        var ingested = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        db.Database.GetCollection(LiteDbMemoryRepository.CollectionName).Insert(new BsonDocument
        {
            ["_id"]        = id,
            ["Content"]    = "recorded properly",
            ["CreatedAt"]  = DateTime.UtcNow,
            ["IngestedAt"] = ingested,
            ["ValidFrom"]  = ingested,
        });

        new ScopedMemorySchemaStep().Apply(Context(db));

        // The backfill only applies to fields the document never had. Overwriting a stored value
        // with CreatedAt would be its own kind of history rewrite.
        var memory = Read(db, id);
        Assert.Equal(ingested, memory.IngestedAt.ToUniversalTime());
        Assert.Equal(ingested, memory.ValidFrom.ToUniversalTime());
    }

    [Fact]
    public void AnEmptyStoreIsNothingToDo()
    {
        using var db = new TempDatabase();

        Assert.Equal(0, new ScopedMemorySchemaStep().Apply(Context(db)));
    }

    [Fact]
    public void AnArchivedMemorySurvivesUpgradingFromTheOldVersioningScheme()
    {
        using var db = new TempDatabase();

        // An install that already ran the v2 migration under the previous scheme: the memory is in
        // its new shape, the boolean is long gone, and the version lives in the old stamp.
        var id = Guid.NewGuid();
        db.Database.GetCollection<MemoryNodeEntity>(LiteDbMemoryRepository.CollectionName).Insert(
            new MemoryNodeEntity
            {
                Id      = id,
                Content = "Something the user archived",
                State   = MemoryState.Archived,
            });
        db.Database.GetCollection("memory_schema").Upsert(new BsonDocument
        {
            ["_id"]   = "schema_version",
            ["value"] = 2,
        });

        DatabaseMigrator.Run(db.Database, db.Path);

        // This is the whole reason the runner adopts the old stamp instead of treating these
        // databases as unversioned. Re-running v2 here finds no boolean to read, concludes the
        // memory was never archived, and quietly reactivates it.
        Assert.Equal(MemoryState.Archived, Read(db, id).State);
    }

    // ── v3: projects becoming workspaces ──────────────────────────────────────────────────────

    private static void SetKeyValue(TempDatabase db, string key, string value) =>
        db.Database.GetCollection("kv").Upsert(new BsonDocument
        {
            ["_id"]       = key,
            ["Value"]     = value,
            ["UpdatedAt"] = DateTime.UtcNow,
        });

    private static string? GetKeyValue(TempDatabase db, string key) =>
        db.Database.GetCollection("kv").FindById(key) is { } document
            ? document["Value"].AsString
            : null;

    [Fact]
    public void StoredProjectsAreReshapedIntoWorkspaces()
    {
        using var db = new TempDatabase();
        SetKeyValue(db, "projects",
            """[{"Id":"a1","Name":"agentic-memory","RootPath":"H:/DEV/agentic-memory","CreatedAt":"2026-01-01T00:00:00Z"}]""");

        var touched = new ProjectsToWorkspacesStep().Apply(Context(db));

        Assert.Equal(1, touched);

        var workspaces = System.Text.Json.JsonSerializer
            .Deserialize<List<WorkspaceRecord>>(GetKeyValue(db, "workspaces")!)!;

        Assert.Single(workspaces);
        Assert.Equal("a1", workspaces[0].Id);
        Assert.Equal("agentic-memory", workspaces[0].Name);
        Assert.Equal("H:/DEV/agentic-memory", workspaces[0].RootPath);
        Assert.Equal("2026-01-01T00:00:00Z", workspaces[0].CreatedAt);
        Assert.Empty(workspaces[0].SubProjects);
    }

    [Fact]
    public void AProjectWithNoCreationDateGetsOne()
    {
        using var db = new TempDatabase();
        SetKeyValue(db, "projects", """[{"Id":"a1","Name":"n","RootPath":"/tmp/n"}]""");

        new ProjectsToWorkspacesStep().Apply(Context(db));

        var workspaces = System.Text.Json.JsonSerializer
            .Deserialize<List<WorkspaceRecord>>(GetKeyValue(db, "workspaces")!)!;

        Assert.False(string.IsNullOrWhiteSpace(workspaces[0].CreatedAt));
    }

    [Fact]
    public void AProjectWithNowhereToPointIsDropped()
    {
        using var db = new TempDatabase();
        SetKeyValue(db, "projects",
            """[{"Id":"a1","Name":"good","RootPath":"/tmp/good"},{"Id":"a2","Name":"no path"}]""");

        new ProjectsToWorkspacesStep().Apply(Context(db));

        var workspaces = System.Text.Json.JsonSerializer
            .Deserialize<List<WorkspaceRecord>>(GetKeyValue(db, "workspaces")!)!;

        // Carrying it forward would only surface as an entry in the dashboard that cannot open.
        Assert.Single(workspaces);
        Assert.Equal("good", workspaces[0].Name);
    }

    [Fact]
    public void AlreadyReshapedWorkspacesAreLeftUntouched()
    {
        using var db = new TempDatabase();
        SetKeyValue(db, "projects", """[{"Id":"a1","Name":"old","RootPath":"/tmp/old"}]""");
        SetKeyValue(db, "workspaces", """[{"Id":"b1","Name":"current","RootPath":"/tmp/current","CreatedAt":"x","SubProjects":[]}]""");

        var touched = new ProjectsToWorkspacesStep().Apply(Context(db));

        // Installs that went through the unversioned startup fixup already have this key. Overwriting
        // it would discard whatever they have done since.
        Assert.Equal(0, touched);
        Assert.Contains("current", GetKeyValue(db, "workspaces"));
    }

    [Fact]
    public void NothingToReshapeIsNotAFailure()
    {
        using var db = new TempDatabase();

        Assert.Equal(0, new ProjectsToWorkspacesStep().Apply(Context(db)));
        Assert.Null(GetKeyValue(db, "workspaces"));
    }

    [Fact]
    public void AnUnreadableProjectListDoesNotStopStartup()
    {
        using var db = new TempDatabase();
        SetKeyValue(db, "projects", "{ this is not json");

        // The workspace list is a convenience the user can rebuild in a few clicks. Refusing to
        // start over it would be a far worse outcome than losing it.
        var touched = new ProjectsToWorkspacesStep().Apply(Context(db));

        Assert.Equal(0, touched);
        Assert.Null(GetKeyValue(db, "workspaces"));
    }
}
