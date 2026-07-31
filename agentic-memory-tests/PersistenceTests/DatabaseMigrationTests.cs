using AgenticMemory.Configuration;
using AgenticMemory.Persistence.Migrations;
using AgenticMemoryTests.PlatformTests;
using LiteDB;

namespace AgenticMemoryTests.PersistenceTests;

/// <summary>
/// A database outlives every build that touches it. These cover the four things that have to hold
/// for that to be safe:
///
///   * an old file is brought up to date automatically, with no user action;
///   * a file from a <i>newer</i> build is refused rather than quietly damaged;
///   * a migration that fails leaves the file where it started, not part-way;
///   * a migration that has already run never runs again.
///
/// The last one is not housekeeping. The v2 step reads a field it then removes, so a second pass
/// sees defaults instead of data and silently reactivates every archived memory.
/// </summary>
public class DatabaseMigrationTests
{
    // ── Test doubles ──────────────────────────────────────────────────────────────────────────

    /// <summary>A step that records that it ran and, optionally, writes a marker.</summary>
    private sealed class RecordingStep(int version, string name = "recording") : IMigrationStep
    {
        public int    Version => version;
        public string Name    => name;
        public int    Runs    { get; private set; }

        public int Apply(MigrationContext context)
        {
            Runs++;
            context.Database.GetCollection("markers").Upsert(new BsonDocument
            {
                ["_id"]  = $"step-{version}",
                ["Runs"] = Runs,
            });
            return 1;
        }
    }

    private sealed class FailingStep(int version) : IMigrationStep
    {
        public int    Version => version;
        public string Name    => "deliberately-broken";

        public int Apply(MigrationContext context)
        {
            // Write first, so the test proves the transaction is rolled back rather than that
            // nothing was attempted.
            context.Database.GetCollection("markers").Upsert(new BsonDocument
            {
                ["_id"] = $"step-{version}",
            });
            throw new InvalidOperationException("step failed on purpose");
        }
    }

    /// <summary>A LiteDB file that cleans itself up, opened the way the application opens one.</summary>
    private sealed class TempDatabase : IDisposable
    {
        private readonly TempDirectory _directory = new();

        public string       Path     { get; }
        public LiteDatabase Database { get; private set; }

        public TempDatabase()
        {
            Path     = System.IO.Path.Combine(_directory.Path, "test.db");
            Database = Open();
        }

        private LiteDatabase Open() =>
            new(new ConnectionString { Filename = Path, Connection = ConnectionType.Direct });

        /// <summary>Closes and re-opens, to prove something survived rather than sat in a cache.</summary>
        public void Reopen()
        {
            Database.Dispose();
            Database = Open();
        }

        public string BackupDirectory => System.IO.Path.Combine(_directory.Path, "backups");

        public void Dispose()
        {
            Database.Dispose();
            _directory.Dispose();
        }
    }

    /// <summary>
    /// Makes the file look like one that has been in use, so it is not mistaken for a new one.
    ///
    /// Deliberately a collection no real step touches: these tests are about the runner, and the
    /// steps themselves are exercised against realistic documents in <see cref="SchemaStepTests"/>.
    /// </summary>
    private static void MakeItLookUsed(LiteDatabase database) =>
        database.GetCollection("probe").Insert(new BsonDocument
        {
            ["_id"]  = "written by an older build",
        });

    private static DatabaseStamp ReadStamp(LiteDatabase database) =>
        DatabaseStamp.Read(database) ?? throw new InvalidOperationException("no stamp written");

    // ── A new database ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFreshDatabaseIsBornAtTheCurrentSchemaVersion()
    {
        using var db = new TempDatabase();

        var report = DatabaseMigrator.Run(db.Database, db.Path);

        // Historical steps describe how old data became new data. There is no old data.
        Assert.False(report.Ran);
        Assert.True(report.WasFreshDatabase);
        Assert.Equal(DatabaseSchema.Current, report.ToVersion);
        Assert.Equal(DatabaseSchema.Current, ReadStamp(db.Database).SchemaVersion);
    }

    [Fact]
    public void AFreshDatabaseRecordsTheBuildThatCreatedIt()
    {
        using var db = new TempDatabase();

        DatabaseMigrator.Run(db.Database, db.Path);

        var stamp = ReadStamp(db.Database);
        Assert.Equal(AppVersion.Current, stamp.CreatedByAppVersion);
        Assert.NotEqual(default, stamp.CreatedAt);
    }

    [Fact]
    public void TheStampSurvivesClosingAndReopeningTheFile()
    {
        using var db = new TempDatabase();
        DatabaseMigrator.Run(db.Database, db.Path);

        db.Reopen();

        Assert.Equal(DatabaseSchema.Current, ReadStamp(db.Database).SchemaVersion);
    }

    [Fact]
    public void TheSchemaVersionIsMirroredToTheLiteDbPragma()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);

        DatabaseMigrator.Run(db.Database, db.Path);
        db.Reopen();

        // Written so anything that opens the file — LiteDB Studio, a support script — can read the
        // version without knowing about this project's stamp document.
        Assert.Equal(DatabaseSchema.Current, db.Database.UserVersion);
    }

    // ── An old database ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnUnstampedDatabaseHoldingDataIsMigratedFromTheBaseline()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);

        var report = DatabaseMigrator.Run(db.Database, db.Path);

        Assert.True(report.Ran);
        Assert.Equal(DatabaseSchema.Baseline, report.FromVersion);
        Assert.Equal(DatabaseSchema.Current, report.ToVersion);
        Assert.False(report.WasFreshDatabase);
    }

    [Fact]
    public void EveryStepRunsInOrderAndOnlyOnce()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);

        var first  = new RecordingStep(2, "first");
        var second = new RecordingStep(3, "second");

        var report = DatabaseMigrator.Run(
            db.Database, [second, first], db.Path, backupDirectory: null, logger: null);

        Assert.Equal(["first", "second"], report.Applied.Select(step => step.Name));
        Assert.Equal(1, first.Runs);
        Assert.Equal(1, second.Runs);
    }

    [Fact]
    public void AnAlreadyCurrentDatabaseIsLeftAloneOnTheNextOpen()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);

        DatabaseMigrator.Run(db.Database, db.Path);
        var second = DatabaseMigrator.Run(db.Database, db.Path);

        Assert.False(second.Ran);
        Assert.Empty(second.Applied);
    }

    [Fact]
    public void OnlyTheStepsAboveTheStoredVersionRun()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);

        var second = new RecordingStep(2, "second");
        var third  = new RecordingStep(3, "third");

        DatabaseMigrator.Run(db.Database, [second], db.Path, null, null);
        var report = DatabaseMigrator.Run(db.Database, [second, third], db.Path, null, null);

        // The database is at v2, so shipping a v3 step must not re-run v2 along with it.
        Assert.Equal(1, second.Runs);
        Assert.Equal(1, third.Runs);
        Assert.Equal(["third"], report.Applied.Select(step => step.Name));
    }

    // ── The version recorded by the scheme this replaced ───────────────────────────────────────

    [Fact]
    public void TheVersionRecordedByThePreviousSchemeIsAdopted()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);

        // How the old scheme stamped itself, before there was a stamp document.
        db.Database.GetCollection("memory_schema").Upsert(new BsonDocument
        {
            ["_id"]   = "schema_version",
            ["value"] = 2,
        });

        var scoped = new RecordingStep(2, "scoped-memory-schema");
        var report = DatabaseMigrator.Run(db.Database, [scoped], db.Path, null, null);

        // Not re-run: this install already went through v2 under the old scheme. Treating it as
        // unversioned is what would silently reactivate every archived memory.
        Assert.Equal(0, scoped.Runs);
        Assert.True(report.AdoptedLegacyVersion);
        Assert.Equal(2, report.FromVersion);
    }

    [Fact]
    public void AFreshDatabaseIsNotMistakenForOneWithALegacyStamp()
    {
        using var db = new TempDatabase();

        var report = DatabaseMigrator.Run(db.Database, db.Path);

        Assert.False(report.AdoptedLegacyVersion);
    }

    // ── A database from a newer build ─────────────────────────────────────────────────────────

    [Fact]
    public void ADatabaseWrittenByANewerBuildIsRefused()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);
        DatabaseMigrator.Run(db.Database, db.Path);

        // A build one schema version ahead of this one — an auto-update that was later rolled back,
        // or a stale sidecar launched against a current profile.
        var ahead = new RecordingStep(DatabaseSchema.Current + 1, "from-the-future");
        DatabaseMigrator.Run(db.Database, [.. DatabaseSchema.Steps, ahead], db.Path, null, null);

        Assert.Throws<DatabaseSchemaTooNewException>(() => DatabaseMigrator.Run(db.Database, db.Path));
    }

    [Fact]
    public void TheRefusalSaysWhichVersionsAreInvolvedAndWhatToDo()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);
        DatabaseMigrator.Run(db.Database, db.Path);

        var ahead = new RecordingStep(DatabaseSchema.Current + 1, "from-the-future");
        DatabaseMigrator.Run(db.Database, [.. DatabaseSchema.Steps, ahead], db.Path, null, null);

        var error = Assert.Throws<DatabaseSchemaTooNewException>(
            () => DatabaseMigrator.Run(db.Database, db.Path));

        Assert.Contains($"v{DatabaseSchema.Current + 1}", error.Message);
        Assert.Contains($"v{DatabaseSchema.Current}", error.Message);
        Assert.Contains(AppVersion.Current, error.Message);
        Assert.Contains("--data-dir", error.Message);
    }

    [Fact]
    public void ARefusedDatabaseIsNotWrittenTo()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);
        DatabaseMigrator.Run(db.Database, db.Path);

        var ahead = new RecordingStep(DatabaseSchema.Current + 1, "from-the-future");
        DatabaseMigrator.Run(db.Database, [.. DatabaseSchema.Steps, ahead], db.Path, null, null);

        var before = ReadStamp(db.Database);
        Assert.Throws<DatabaseSchemaTooNewException>(() => DatabaseMigrator.Run(db.Database, db.Path));
        var after = ReadStamp(db.Database);

        // Not even the "last opened by" field: this build has established it does not understand
        // the file, so it has no business leaving a mark on it.
        Assert.Equal(before.SchemaVersion, after.SchemaVersion);
        Assert.Equal(before.LastOpenedAt, after.LastOpenedAt);
    }

    // ── A migration that fails ────────────────────────────────────────────────────────────────

    [Fact]
    public void AFailingStepLeavesTheDatabaseAtThePreviousVersion()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);

        var good = new RecordingStep(2, "good");
        var bad  = new FailingStep(3);

        Assert.Throws<DatabaseMigrationFailedException>(
            () => DatabaseMigrator.Run(db.Database, [good, bad], db.Path, null, null));

        // v2 committed, v3 did not. The file is complete at v2 rather than stranded between the two.
        Assert.Equal(2, ReadStamp(db.Database).SchemaVersion);
    }

    [Fact]
    public void AFailingStepRollsBackWhatItHadAlreadyWritten()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);

        var bad = new FailingStep(2);

        Assert.Throws<DatabaseMigrationFailedException>(
            () => DatabaseMigrator.Run(db.Database, [bad], db.Path, null, null));

        db.Reopen();
        Assert.Null(db.Database.GetCollection("markers").FindById("step-2"));
    }

    [Fact]
    public void TheNextLaunchResumesFromWhereTheFailureLeftIt()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);

        var good = new RecordingStep(2, "good");
        Assert.Throws<DatabaseMigrationFailedException>(
            () => DatabaseMigrator.Run(db.Database, [good, new FailingStep(3)], db.Path, null, null));

        // Ship the fix; relaunch. The repaired step runs, the one that already succeeded does not.
        var repaired = new RecordingStep(3, "repaired");
        var report   = DatabaseMigrator.Run(db.Database, [good, repaired], db.Path, null, null);

        Assert.Equal(1, good.Runs);
        Assert.Equal(1, repaired.Runs);
        Assert.Equal(3, report.ToVersion);
    }

    [Fact]
    public void TheFailureSaysWhichStepBrokeAndWhereTheDatabaseIsNow()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);

        var error = Assert.Throws<DatabaseMigrationFailedException>(
            () => DatabaseMigrator.Run(
                db.Database, [new RecordingStep(2, "good"), new FailingStep(3)], db.Path, null, null));

        Assert.Contains("v3", error.Message);
        Assert.Contains("deliberately-broken", error.Message);
        Assert.Contains("schema v2", error.Message);
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    // ── The snapshot taken beforehand ─────────────────────────────────────────────────────────

    [Fact]
    public void ASnapshotIsTakenBeforeTheFirstStep()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);

        var report = DatabaseMigrator.Run(
            db.Database, [new RecordingStep(2)], db.Path, db.BackupDirectory, null);

        Assert.NotNull(report.BackupPath);
        Assert.True(File.Exists(report.BackupPath));
    }

    [Fact]
    public void TheSnapshotIsTakenBeforeTheStepsRunNotAfter()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);

        var report = DatabaseMigrator.Run(
            db.Database, [new RecordingStep(2)], db.Path, db.BackupDirectory, null);

        // A snapshot that already contains the migration's output is worthless as a way back.
        using var snapshot = new LiteDatabase(new ConnectionString
        {
            Filename   = report.BackupPath!,
            Connection = ConnectionType.Direct,
            ReadOnly   = true,
        });

        Assert.Null(snapshot.GetCollection("markers").FindById("step-2"));
    }

    [Fact]
    public void NoSnapshotIsTakenWhenThereIsNothingToMigrate()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);
        DatabaseMigrator.Run(db.Database, db.Path, db.BackupDirectory);

        var second = DatabaseMigrator.Run(db.Database, db.Path, db.BackupDirectory);

        Assert.Null(second.BackupPath);
        Assert.Single(Directory.GetFiles(db.BackupDirectory));
    }

    [Fact]
    public void AnUnwritableSnapshotDirectoryDoesNotStopTheMigration()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);

        // Each step still commits or rolls back as a unit, so a missing snapshot costs the ability
        // to undo a successful migration — not the integrity of a failed one.
        var report = DatabaseMigrator.Run(
            db.Database, [new RecordingStep(2)], db.Path,
            backupDirectory: Path.Combine(db.Path, "not-a-directory"), logger: null);

        Assert.Null(report.BackupPath);
        Assert.True(report.Ran);
        Assert.Equal(2, ReadStamp(db.Database).SchemaVersion);
    }

    // ── What the file records about itself ────────────────────────────────────────────────────

    [Fact]
    public void EveryMigrationIsRecordedWithTheAppVersionThatRanIt()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);

        DatabaseMigrator.Run(
            db.Database, [new RecordingStep(2, "first"), new RecordingStep(3, "second")],
            db.Path, null, null);

        var history = ReadStamp(db.Database).History;

        Assert.Equal(["first", "second"], history.Select(entry => entry.Name));
        Assert.All(history, entry => Assert.Equal(AppVersion.Current, entry.AppVersion));
        Assert.Equal([1, 2], history.Select(entry => entry.FromVersion));
        Assert.Equal([2, 3], history.Select(entry => entry.ToVersion));
    }

    [Fact]
    public void EachOpenRecordsWhichBuildOpenedIt()
    {
        using var db = new TempDatabase();

        DatabaseMigrator.Run(db.Database, db.Path);

        var stamp = ReadStamp(db.Database);
        Assert.Equal(AppVersion.Current, stamp.LastOpenedByAppVersion);
        Assert.NotEqual(default, stamp.LastOpenedAt);
    }

    [Fact]
    public void AnOlderDatabaseIsNotClaimedToHaveBeenCreatedByThisBuild()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);

        DatabaseMigrator.Run(db.Database, db.Path);

        // It was created by some build that predates version stamping. Naming this one would be a
        // lie, and the field is only there to answer support questions.
        Assert.Null(ReadStamp(db.Database).CreatedByAppVersion);
    }

    [Fact]
    public void AStampThatWillNotDeserializeStillYieldsItsSchemaVersion()
    {
        using var db = new TempDatabase();
        MakeItLookUsed(db.Database);
        DatabaseMigrator.Run(db.Database, db.Path);

        // A future build changes the shape of a field this one cannot map. The schema version is
        // the one thing that must still be readable, because it decides whether this build may write.
        var raw = db.Database.GetCollection(DatabaseStamp.CollectionName);
        var document = raw.FindById(DatabaseStamp.DocumentId);
        document["History"] = "no longer an array";
        raw.Update(document);

        Assert.Equal(DatabaseSchema.Current, DatabaseStamp.Read(db.Database)!.SchemaVersion);
    }

    // ── The registry itself ───────────────────────────────────────────────────────────────────

    [Fact]
    public void StepVersionsAreUniqueAndAscendingAboveTheBaseline()
    {
        var versions = DatabaseSchema.Steps.Select(step => step.Version).ToList();

        // A duplicate or out-of-order version means two databases can claim the same version with
        // different contents, which nothing downstream can detect.
        Assert.Equal(versions.Distinct().Count(), versions.Count);
        Assert.Equal(versions.OrderBy(version => version), versions);
        Assert.All(versions, version => Assert.True(version > DatabaseSchema.Baseline));
    }

    [Fact]
    public void TheCurrentVersionIsTheLastStepSoTheyCannotDisagree()
    {
        Assert.Equal(DatabaseSchema.Steps[^1].Version, DatabaseSchema.Current);
    }

    [Fact]
    public void EveryStepHasAName()
    {
        Assert.All(DatabaseSchema.Steps, step => Assert.False(string.IsNullOrWhiteSpace(step.Name)));
    }

    [Fact]
    public void TheAppVersionIsSeparateFromTheSchemaVersionAndActuallySet()
    {
        // The two are deliberately unrelated: a bug-fix release must not imply a data migration,
        // and a schema change must not wait for a release boundary.
        Assert.NotEqual("0.0.0", AppVersion.Current);
        Assert.Matches(@"^\d+\.\d+", AppVersion.Current);
    }
}
