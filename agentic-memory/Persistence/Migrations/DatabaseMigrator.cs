using System.Globalization;
using AgenticMemory.Configuration;
using LiteDB;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Persistence.Migrations;

/// <summary>One step that ran during this open.</summary>
public sealed record AppliedMigration(int FromVersion, int ToVersion, string Name, int DocumentsTouched);

/// <summary>What happened when the database was opened. Reported at startup and over HTTP.</summary>
public sealed record DatabaseMigrationReport(
    int  FromVersion,
    int  ToVersion,
    bool WasFreshDatabase,
    bool AdoptedLegacyVersion,
    string? BackupPath,
    IReadOnlyList<AppliedMigration> Applied)
{
    public bool Ran => Applied.Count > 0;

    public static DatabaseMigrationReport UpToDate(int version) =>
        new(version, version, WasFreshDatabase: false, AdoptedLegacyVersion: false, BackupPath: null, Applied: []);
}

/// <summary>
/// Thrown when the database was written by a newer build than the one now opening it.
///
/// This aborts startup on purpose. Old code against a new schema is not a degraded experience, it is
/// silent destruction: it reads fields it does not know about, drops them on the next write, and
/// leaves a database that no version can make sense of. For a desktop app that auto-updates — where
/// a user can install an older build, or a stale sidecar can be launched beside a current one — this
/// is a routine accident, not a remote one.
/// </summary>
public sealed class DatabaseSchemaTooNewException(string message) : Exception(message);

/// <summary>Thrown when a step failed. The transaction is already rolled back by the time this is raised.</summary>
public sealed class DatabaseMigrationFailedException(string message, Exception inner)
    : Exception(message, inner);

/// <summary>
/// Brings a database up to <see cref="DatabaseSchema.Current"/> when it is opened, and records what
/// it did in the file itself.
///
/// The shape of the guarantee:
///
///   * <b>A newer database is refused, not opened.</b> See <see cref="DatabaseSchemaTooNewException"/>.
///   * <b>Each step commits with its own version stamp, in one transaction.</b> So a failure at step
///     N leaves the file complete at N-1 rather than half-way through, and the next launch resumes
///     from there instead of starting over.
///   * <b>A snapshot is taken before the first step.</b> Transactions cover a step that throws; they
///     do not cover a step whose logic is wrong and commits happily. The snapshot is the only thing
///     that does, which is why it is worth taking — and why it is not worth refusing to start over.
///   * <b>A fresh database is born current.</b> Historical steps describe how old data became new
///     data; running them against an empty file would at best waste time and at worst apply a
///     defaulting rule to documents that never needed it.
/// </summary>
public static class DatabaseMigrator
{
    public static DatabaseMigrationReport Run(
        LiteDatabase database,
        string?      databasePath     = null,
        string?      backupDirectory  = null,
        ILogger?     logger           = null) =>
        Run(database, DatabaseSchema.Steps, databasePath, backupDirectory, logger);

    /// <summary>
    /// Runs an arbitrary set of steps. Exists so the tests can drive a failing step through the
    /// runner: how it behaves when a migration throws is the part worth proving, and it cannot be
    /// proven with steps that all succeed.
    /// </summary>
    internal static DatabaseMigrationReport Run(
        LiteDatabase                 database,
        IReadOnlyList<IMigrationStep> steps,
        string?                      databasePath,
        string?                      backupDirectory,
        ILogger?                     logger)
    {
        var current        = steps.Count == 0 ? DatabaseSchema.Baseline : steps[^1].Version;
        var stamp          = DatabaseStamp.Read(database);
        var isFresh        = stamp is null && !HasUserData(database);
        var adoptedLegacy  = false;

        int from;
        if (stamp is not null)
        {
            from = stamp.SchemaVersion;
        }
        else if (isFresh)
        {
            from = current;
        }
        else
        {
            var legacy    = DatabaseStamp.ReadLegacyVersion(database);
            adoptedLegacy = legacy.HasValue;
            from          = legacy ?? DatabaseSchema.Baseline;

            if (adoptedLegacy)
                logger?.LogInformation(
                    "Adopted schema version {Version} from the previous versioning scheme", from);
        }

        if (from > current)
            throw TooNew(from, current, stamp, databasePath);

        stamp ??= NewStamp(from, isFresh);

        var pending = steps.Where(step => step.Version > from).OrderBy(step => step.Version).ToList();
        if (pending.Count == 0)
        {
            RecordOpen(database, stamp);
            MirrorToPragma(database, stamp.SchemaVersion, logger);
            return new DatabaseMigrationReport(
                from, from, isFresh, adoptedLegacy, BackupPath: null, Applied: []);
        }

        logger?.LogInformation(
            "Migrating database schema v{From} -> v{To} ({Count} step(s))",
            from, current, pending.Count);

        var backup  = TrySnapshot(database, databasePath, backupDirectory, from, current, logger);
        var context = new MigrationContext(database, logger);
        var applied = new List<AppliedMigration>();

        foreach (var step in pending)
        {
            var before = stamp.SchemaVersion;
            var owns   = database.BeginTrans();

            try
            {
                var touched = step.Apply(context);

                // Stamped inside the same transaction as the work it describes, so the recorded
                // version and the data it claims to describe can never disagree.
                stamp.SchemaVersion = step.Version;
                stamp.History.Add(new MigrationHistoryEntry
                {
                    FromVersion      = before,
                    ToVersion        = step.Version,
                    Name             = step.Name,
                    DocumentsTouched = touched,
                    AppliedAt        = DateTime.UtcNow,
                    AppVersion       = AppVersion.Current,
                });
                DatabaseStamp.Write(database, stamp);

                if (owns) database.Commit();

                applied.Add(new AppliedMigration(before, step.Version, step.Name, touched));
                logger?.LogInformation(
                    "Applied schema step v{Version} ({Name}): {Count} document(s)",
                    step.Version, step.Name, touched);
            }
            catch (Exception ex)
            {
                if (owns) database.Rollback();

                throw new DatabaseMigrationFailedException(
                    $"Database migration step v{step.Version} ({step.Name}) failed: {ex.Message}. " +
                    $"The database was left at schema v{before} and no data was changed by this step. " +
                    (backup is not null
                        ? $"A snapshot taken before the migration is at '{backup}'."
                        : "No snapshot could be taken beforehand."),
                    ex);
            }
        }

        RecordOpen(database, stamp);
        MirrorToPragma(database, stamp.SchemaVersion, logger);

        return new DatabaseMigrationReport(
            from, stamp.SchemaVersion, isFresh, adoptedLegacy, backup, applied);
    }

    /// <summary>
    /// True when the file holds something worth migrating. A database with nothing but its own stamp
    /// collection is indistinguishable from a new one, and is treated as new.
    /// </summary>
    private static bool HasUserData(LiteDatabase database) =>
        database.GetCollectionNames()
            .Any(name => !string.Equals(name, DatabaseStamp.CollectionName, StringComparison.Ordinal));

    private static DatabaseStamp NewStamp(int version, bool isFresh) => new()
    {
        SchemaVersion = version,
        CreatedAt     = DateTime.UtcNow,

        // An existing database was created by some earlier build we cannot name. Recording that as
        // unknown is more use than claiming it was created by whichever build added version stamping.
        CreatedByAppVersion = isFresh ? AppVersion.Current : null,
    };

    private static void RecordOpen(LiteDatabase database, DatabaseStamp stamp)
    {
        stamp.LastOpenedAt           = DateTime.UtcNow;
        stamp.LastOpenedByAppVersion = AppVersion.Current;
        DatabaseStamp.Write(database, stamp);
    }

    /// <summary>
    /// Keeps LiteDB's own USER_VERSION pragma in step, so the file reports its version to any tool
    /// that opens it. Written only; <see cref="DatabaseStamp"/> remains the authority, because a
    /// pragma cannot participate in the migration transaction.
    /// </summary>
    private static void MirrorToPragma(LiteDatabase database, int version, ILogger? logger)
    {
        try
        {
            database.UserVersion = version;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not mirror schema version to the USER_VERSION pragma");
        }
    }

    private static DatabaseSchemaTooNewException TooNew(
        int found, int supported, DatabaseStamp? stamp, string? databasePath)
    {
        var writtenBy = stamp?.LastMigratedByAppVersion ?? stamp?.LastOpenedByAppVersion;

        return new DatabaseSchemaTooNewException(
            $"This database is at schema v{found}, but this build only understands v{supported}. " +
            (string.IsNullOrWhiteSpace(writtenBy)
                ? $"It was written by a newer version of the application (this one is {AppVersion.Current}). "
                : $"It was written by version {writtenBy} of the application (this one is {AppVersion.Current}). ") +
            "Startup was stopped rather than risk writing to data this build cannot read correctly. " +
            "Update the application, or point this build at a different data directory with " +
            $"--data-dir <path>." +
            (databasePath is null ? "" : $" Database: '{databasePath}'."));
    }

    /// <summary>
    /// Copies the datafile before the first step. Checkpointed first so the copy is a complete image
    /// rather than a valid-looking database missing the most recent writes.
    /// </summary>
    private static string? TrySnapshot(
        LiteDatabase database, string? databasePath, string? backupDirectory,
        int fromVersion, int toVersion, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || string.IsNullOrWhiteSpace(backupDirectory))
            return null;

        try
        {
            var source = Path.GetFullPath(databasePath);
            if (!File.Exists(source)) return null;

            Directory.CreateDirectory(backupDirectory);
            database.Checkpoint();

            var stamp  = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var target = Path.Combine(
                backupDirectory, $"{stamp}-schema-v{fromVersion}-to-v{toVersion}.db");

            var suffix = 1;
            while (File.Exists(target))
                target = Path.Combine(
                    backupDirectory,
                    $"{stamp}-schema-v{fromVersion}-to-v{toVersion}-{suffix++}.db");

            // FileShare.ReadWrite: the connection we are about to migrate holds the source open.
            using (var input  = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
            }

            logger?.LogInformation("Snapshot taken before schema migration: {Path}", target);
            return target;
        }
        catch (Exception ex)
        {
            // Not fatal. Each step still commits or rolls back as a unit, so a missing snapshot costs
            // the ability to undo a *successful* migration, not the integrity of a failed one.
            logger?.LogWarning(ex, "Could not snapshot before schema migration; continuing");
            return null;
        }
    }
}
