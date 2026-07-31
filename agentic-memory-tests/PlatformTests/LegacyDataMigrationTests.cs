using AgenticMemory.Configuration;

namespace AgenticMemoryTests.PlatformTests;

/// <summary>
/// Moving the database out of the program directory is only safe if existing installs come with it.
///
/// The failure this guards against is specific and severe: a user updates, the server looks in the
/// new per-user location, finds nothing, and reports an empty memory. Nothing has actually been
/// deleted — but from the outside it is indistinguishable from having lost everything, which is the
/// one outcome this subsystem exists to make impossible.
/// </summary>
public class LegacyDataMigrationTests
{
    /// <summary>A program directory laid out the way older builds left it.</summary>
    private sealed class Install : IDisposable
    {
        public TempDirectory Root { get; } = new();

        public string Program => Path.Combine(Root.Path, "program");
        public string LegacyData => Path.Combine(Program, "Data");
        public string LegacyDb => Path.Combine(LegacyData, "agentic-memory.db");
        public string LegacyBackups => Path.Combine(LegacyData, "backups");
        public string UserData => Path.Combine(Root.Path, "userdata");

        public Install()
        {
            Directory.CreateDirectory(Program);
        }

        public Install WithLegacyDatabase(string contents = "old memories")
        {
            Directory.CreateDirectory(LegacyData);
            File.WriteAllText(LegacyDb, contents);
            return this;
        }

        /// <summary>The LiteDB write-ahead log that sits beside the database file.</summary>
        public string LegacyLog => Path.Combine(LegacyData, "agentic-memory-log.db");

        public Install WithLegacyLog(string contents = "uncheckpointed writes")
        {
            Directory.CreateDirectory(LegacyData);
            File.WriteAllText(LegacyLog, contents);
            return this;
        }

        public Install WithLegacyBackup(string name, string contents = "snapshot")
        {
            Directory.CreateDirectory(LegacyBackups);
            File.WriteAllText(Path.Combine(LegacyBackups, name), contents);
            return this;
        }

        public Install WithLegacyModels()
        {
            var models = Path.Combine(Program, "Models", "Embedding");
            Directory.CreateDirectory(models);
            File.WriteAllText(Path.Combine(models, "all-MiniLM-L6-v2.onnx"), "weights");
            return this;
        }

        public (AppPaths Paths, AppSettings Settings) Resolve(string[]? args = null)
        {
            var paths = AppPaths.Resolve(
                Program, args ?? ["--data-dir", UserData], environment: _ => null);

            var settings = new AppSettings();
            settings.Storage.DatabasePath =
                paths.InData("", StorageSettings.DefaultDatabaseFileName);
            settings.Maintenance.BackupPath =
                paths.InData("", MaintenanceSettings.DefaultRelativeBackupPath);
            settings.Embeddings.ModelsPath =
                paths.InModels("", EmbeddingsSettings.DefaultRelativeModelsPath);

            return (paths, settings);
        }

        public void Dispose() => Root.Dispose();
    }

    [Fact]
    public void AnExistingDatabaseFollowsTheUserToTheNewLocation()
    {
        using var install = new Install().WithLegacyDatabase("aaron's memories");
        var (paths, settings) = install.Resolve();

        var result = LegacyDataMigration.Run(paths, settings);

        Assert.True(result.DidAnything);
        Assert.Equal("aaron's memories", File.ReadAllText(settings.Storage.DatabasePath));
        Assert.False(File.Exists(install.LegacyDb));
        Assert.Empty(result.Failed);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void SnapshotsTravelWithTheDatabase()
    {
        using var install = new Install()
            .WithLegacyDatabase()
            .WithLegacyBackup("20260101-120000-manual.db", "first")
            .WithLegacyBackup("20260202-120000-purge.db", "second");

        var (paths, settings) = install.Resolve();

        LegacyDataMigration.Run(paths, settings);

        // A snapshot is the recovery path for a purge. Leaving them behind would quietly make every
        // pre-migration backup unreachable through the API that lists them.
        Assert.Equal("first",  File.ReadAllText(Path.Combine(settings.Maintenance.BackupPath, "20260101-120000-manual.db")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(settings.Maintenance.BackupPath, "20260202-120000-purge.db")));
    }

    /// <summary>
    /// Two databases is two histories. Choosing one silently is the "conflicting info" failure, so
    /// neither is touched and the user is told.
    /// </summary>
    [Fact]
    public void TwoDatabasesAreReportedRatherThanMerged()
    {
        using var install = new Install().WithLegacyDatabase("old");
        var (paths, settings) = install.Resolve();

        Directory.CreateDirectory(Path.GetDirectoryName(settings.Storage.DatabasePath)!);
        File.WriteAllText(settings.Storage.DatabasePath, "current");

        var result = LegacyDataMigration.Run(paths, settings);

        Assert.Single(result.Conflicts);
        Assert.Contains(install.LegacyDb, result.Conflicts[0]);
        Assert.Equal("old",     File.ReadAllText(install.LegacyDb));
        Assert.Equal("current", File.ReadAllText(settings.Storage.DatabasePath));
        Assert.Empty(result.Moved);
    }

    /// <summary>
    /// LiteDB's <c>-log.db</c> holds transactions committed but not yet checkpointed into the main
    /// file — that is, the most recent memories. Moving the database without it loses them silently.
    /// </summary>
    [Fact]
    public void TheWriteAheadLogTravelsWithTheDatabase()
    {
        using var install = new Install().WithLegacyDatabase("committed").WithLegacyLog("pending writes");
        var (paths, settings) = install.Resolve();

        var result = LegacyDataMigration.Run(paths, settings);

        var movedLog = Path.Combine(
            Path.GetDirectoryName(settings.Storage.DatabasePath)!, "agentic-memory-log.db");

        Assert.Equal("pending writes", File.ReadAllText(movedLog));
        Assert.False(File.Exists(install.LegacyLog));

        // The main file goes first, by construction rather than by whatever order the volume
        // returns names in. Without a fixed order there is no way to reason about — or test — what
        // state a part-way failure leaves behind; see the rollback case below.
        Assert.Equal(2, result.Moved.Count);
        Assert.StartsWith(install.LegacyDb + " ->", result.Moved[0]);
    }

    /// <summary>
    /// Makes the move of <paramref name="destination"/> fail, on every operating system.
    ///
    /// The real-world trigger is a second copy of the server holding the database open, and on
    /// Windows a test can reproduce that directly: sharing modes are enforced by the kernel, so a
    /// <c>FileShare.None</c> handle is enough to make <c>File.Move</c> throw. Unix has no equivalent.
    /// Locks there are advisory and <c>rename(2)</c> does not consult them, so the same test moved
    /// the file happily and the assertion failed on Linux and macOS while passing on Windows.
    ///
    /// A directory sitting where the file has to land fails the move everywhere, for the same
    /// reason it would in production: the destination cannot be created. Which error arrives is not
    /// what is under test — <c>MigrateDatabase</c> catches anything, rolls back and rethrows as one
    /// <see cref="IOException"/> — so this exercises exactly the path a held file would.
    /// </summary>
    private static void Block(string destination) => Directory.CreateDirectory(destination);

    /// <summary>
    /// The database is the one move that must not fail quietly: starting with an empty store while
    /// the real one sits untouched next door looks exactly like total data loss.
    /// </summary>
    [Fact]
    public void AFailedDatabaseMoveStopsStartupInsteadOfStartingEmpty()
    {
        using var install = new Install().WithLegacyDatabase("aaron's memories");
        var (paths, settings) = install.Resolve();

        Block(settings.Storage.DatabasePath);

        var ex = Assert.Throws<IOException>(() => LegacyDataMigration.Run(paths, settings));

        Assert.Contains(install.LegacyDb, ex.Message);
        Assert.Contains(settings.Storage.DatabasePath, ex.Message);
        Assert.True(File.Exists(install.LegacyDb), "the original must be left exactly where it was");
        Assert.Equal("aaron's memories", File.ReadAllText(install.LegacyDb));

        // Nothing reached the new location, so there is no half-migrated store for the next startup
        // to open. Asserting on the directory rather than on the database path alone, because the
        // path itself is the blocker here and would report "not a file" whatever happened.
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(settings.Storage.DatabasePath)!));
    }

    /// <summary>
    /// A half-moved database is worse than an unmoved one: the main file in the new location and its
    /// log left in the old means LiteDB opens a store missing its most recent writes. Whatever
    /// already moved goes back before the failure is reported.
    /// </summary>
    [Fact]
    public void AFailurePartWayThroughPutsBackWhatAlreadyMoved()
    {
        using var install = new Install().WithLegacyDatabase("committed").WithLegacyLog("pending writes");
        var (paths, settings) = install.Resolve();

        // The main database is moved first by construction, so blocking the log fails the group
        // *after* something has already moved — which is the only state the rollback exists for.
        var dataDirectory = Path.GetDirectoryName(settings.Storage.DatabasePath)!;
        Block(Path.Combine(dataDirectory, "agentic-memory-log.db"));

        var ex = Assert.Throws<IOException>(() => LegacyDataMigration.Run(paths, settings));

        Assert.Contains("still in the original location", ex.Message);
        Assert.True(File.Exists(install.LegacyDb), "the database must be put back beside its log");
        Assert.Equal("committed", File.ReadAllText(install.LegacyDb));
        Assert.False(File.Exists(settings.Storage.DatabasePath));
    }

    /// <summary>
    /// Weights are part of the build, shared by every user of the machine, and re-supplied by the
    /// next version. Copying them into a per-user folder would duplicate gigabytes for nothing.
    /// </summary>
    [Fact]
    public void ModelsAreLeftBesideTheProgram()
    {
        using var install = new Install().WithLegacyDatabase().WithLegacyModels();
        var (paths, settings) = install.Resolve();

        LegacyDataMigration.Run(paths, settings);

        var weights = Path.Combine(install.Program, "Models", "Embedding", "all-MiniLM-L6-v2.onnx");
        Assert.True(File.Exists(weights), "model weights must stay with the binary");
        Assert.Equal(weights, Path.Combine(settings.Embeddings.ModelsPath, "all-MiniLM-L6-v2.onnx"));
    }

    [Fact]
    public void RunningTwiceIsHarmless()
    {
        using var install = new Install().WithLegacyDatabase("aaron's memories").WithLegacyBackup("snap.db");
        var (paths, settings) = install.Resolve();

        var first  = LegacyDataMigration.Run(paths, settings);
        var second = LegacyDataMigration.Run(paths, settings);

        Assert.True(first.DidAnything);
        Assert.False(second.DidAnything);
        Assert.Empty(second.Conflicts);
        Assert.Equal("aaron's memories", File.ReadAllText(settings.Storage.DatabasePath));
    }

    [Fact]
    public void AFreshInstallHasNothingToDo()
    {
        using var install = new Install();
        var (paths, settings) = install.Resolve();

        var result = LegacyDataMigration.Run(paths, settings);

        Assert.False(result.DidAnything);
        Assert.Empty(result.Conflicts);
        Assert.Empty(result.Failed);
    }

    /// <summary>Portable mode resolves to the legacy location deliberately — moving it onto itself
    /// would be at best a no-op and at worst a delete.</summary>
    [Fact]
    public void PortableModeLeavesTheDatabaseWhereItIs()
    {
        using var install = new Install().WithLegacyDatabase("portable memories");
        File.WriteAllText(Path.Combine(install.Program, AppPaths.PortableMarkerFile), "");

        var (paths, settings) = install.Resolve(args: []);
        Assert.Equal(PathOrigin.Portable, paths.Origin);

        var result = LegacyDataMigration.Run(paths, settings);

        Assert.False(result.DidAnything);
        Assert.Equal("portable memories", File.ReadAllText(install.LegacyDb));
        Assert.Equal(install.LegacyDb, settings.Storage.DatabasePath);
    }

    /// <summary>An empty legacy folder left behind reads as "there is still something there".</summary>
    [Fact]
    public void TheEmptiedLegacyFolderIsRemoved()
    {
        using var install = new Install().WithLegacyDatabase().WithLegacyBackup("snap.db");
        var (paths, settings) = install.Resolve();

        LegacyDataMigration.Run(paths, settings);

        Assert.False(Directory.Exists(install.LegacyData));
    }
}
