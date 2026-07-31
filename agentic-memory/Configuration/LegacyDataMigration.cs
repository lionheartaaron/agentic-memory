namespace AgenticMemory.Configuration;

/// <summary>
/// Moves per-user state written by earlier builds — which kept the database beside the executable —
/// into the data directory resolved by <see cref="AppPaths"/>.
///
/// Without this, the move to a per-user data directory <em>is itself</em> the failure it was meant to
/// prevent: an existing install starts up, finds no database where it now looks, and presents an
/// empty memory. The old file is still on disk, but nothing says so, and to the user it is
/// indistinguishable from having lost everything.
///
/// Only the database and its snapshots move. Model weights stay beside the program, where they are
/// shipped with the build and shared by every user on the machine — copying gigabytes of identical
/// weights into a per-user folder would buy nothing and cost disk.
///
/// Two rules govern the moves that do happen.
///
/// <b>Never overwrite.</b> If both locations hold a database, that is two histories, and picking one
/// silently is the "conflicting info" failure. Both are left alone and both paths are reported.
///
/// <b>The database is not best-effort.</b> A failed snapshot move is logged and forgotten. A failed
/// database move is fatal: continuing would open an empty database at the new location while the
/// real one sits untouched at the old — the exact appearance of catastrophic data loss, produced by
/// a recoverable error.
/// </summary>
public static class LegacyDataMigration
{
    /// <summary>What the migration did, for the startup banner and the logs.</summary>
    public sealed record Result(List<string> Moved, List<string> Conflicts, List<string> Failed)
    {
        public bool DidAnything => Moved.Count > 0;
        public static Result Empty() => new([], [], []);
    }

    /// <summary>
    /// Relocates anything found in the legacy layout. Safe to call on every startup: once the legacy
    /// folder is gone it does nothing, and it never touches a destination that already exists.
    /// </summary>
    /// <param name="settings">Settings whose paths have already been resolved to absolute locations.</param>
    public static Result Run(AppPaths paths, AppSettings settings, ILogger? logger = null)
    {
        var result = Result.Empty();
        var legacyData = Path.Combine(paths.ProgramDirectory, "Data");

        // Portable mode resolves to the legacy location on purpose; there is nothing to move.
        if (PathsEqual(legacyData, paths.DataDirectory)) return result;

        MigrateDatabase(legacyData, settings.Storage.DatabasePath, result, logger);
        MigrateFolder(Path.Combine(legacyData, "backups"), settings.Maintenance.BackupPath, result, logger);

        RemoveIfEmpty(legacyData);

        return result;
    }

    /// <summary>
    /// The one move that must not fail quietly, and the one that has to be all-or-nothing.
    ///
    /// LiteDB writes a <c>-log.db</c> sibling: the write-ahead log holding transactions committed but
    /// not yet checkpointed into the main file. Move the database without it and those writes are
    /// gone — the most recent memories, silently, which is precisely the failure mode being designed
    /// out. So every <c>agentic-memory*</c> file travels as one unit, and a failure part-way through
    /// puts back what already moved before refusing to start.
    /// </summary>
    private static void MigrateDatabase(string legacyDirectory, string target, Result result, ILogger? logger)
    {
        var legacy = Path.Combine(legacyDirectory, StorageSettings.DefaultDatabaseFileName);
        if (!File.Exists(legacy)) return;

        if (File.Exists(target))
        {
            var message =
                $"Two databases exist: '{legacy}' from an earlier layout and '{target}' in the data " +
                "directory. The older file has been left untouched — delete or move it once you have " +
                "confirmed which one you want.";

            result.Conflicts.Add(message);
            logger?.LogWarning("{Message}", message);
            return;
        }

        var targetDirectory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(targetDirectory);

        var baseName = Path.GetFileNameWithoutExtension(StorageSettings.DefaultDatabaseFileName);

        // Ordered rather than left to the filesystem: the main file first, then siblings by name.
        // Directory enumeration order is not a contract, and a migration that behaves differently
        // depending on how a volume happens to return names is one that cannot be reasoned about —
        // or tested.
        var group = Directory.EnumerateFiles(legacyDirectory, baseName + "*")
            .Select(source => (
                Source: source,
                IsMain: string.Equals(source, legacy, StringComparison.OrdinalIgnoreCase)))
            .Select(file => (
                file.Source,
                Destination: file.IsMain
                    ? target                                                    // honours a renamed target
                    : Path.Combine(targetDirectory, Path.GetFileName(file.Source)),
                file.IsMain))
            .Where(file => !File.Exists(file.Destination))
            .OrderByDescending(file => file.IsMain)
            .ThenBy(file => file.Source, StringComparer.OrdinalIgnoreCase)
            .Select(file => (file.Source, file.Destination))
            .ToList();

        var done = new List<(string Source, string Destination)>();

        try
        {
            foreach (var (source, destination) in group)
            {
                File.Move(source, destination);
                done.Add((source, destination));
            }
        }
        catch (Exception ex)
        {
            var rolledBack = TryRollBack(done);

            throw new IOException(
                $"Could not move the existing database from '{legacy}' to '{target}': {ex.Message}. " +
                "Startup was stopped rather than continue with an empty database. " +
                (rolledBack
                    ? "Everything was put back, so your memories are still in the original location. " +
                      "Close any other copy of the server that may have the file open, then start again."
                    : $"Some files could not be put back — the database is split between " +
                      $"'{legacyDirectory}' and '{targetDirectory}'. Move the remaining " +
                      $"'{baseName}*' files by hand so that all of them sit together."), ex);
        }

        foreach (var (source, destination) in done)
            result.Moved.Add($"{source} -> {destination}");

        if (done.Count > 0)
            logger?.LogInformation(
                "Moved the database and {Count} associated file(s) to {Target}", done.Count - 1, targetDirectory);
    }

    /// <summary>Puts back everything that moved. Returns false if any file could not be restored.</summary>
    private static bool TryRollBack(List<(string Source, string Destination)> done)
    {
        var complete = true;

        foreach (var (source, destination) in done)
        {
            try { File.Move(destination, source); }
            catch { complete = false; }
        }

        return complete;
    }

    /// <summary>
    /// Best-effort move of a folder's contents. Anything already at the destination wins — a snapshot
    /// is a historical record, and the one already in place is the one the retention count knows about.
    /// </summary>
    private static void MigrateFolder(string legacy, string target, Result result, ILogger? logger)
    {
        if (!Directory.Exists(legacy) || PathsEqual(legacy, target)) return;

        try
        {
            Directory.CreateDirectory(target);
            var moved = 0;

            foreach (var file in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
            {
                var relative    = Path.GetRelativePath(legacy, file);
                var destination = Path.Combine(target, relative);

                if (File.Exists(destination)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(file, destination);
                moved++;
            }

            if (moved > 0)
            {
                result.Moved.Add($"{legacy} -> {target} ({moved} file{(moved == 1 ? "" : "s")})");
                logger?.LogInformation("Moved {Count} file(s) from {Legacy} to {Target}", moved, legacy, target);
            }

            RemoveIfEmpty(legacy);
        }
        catch (Exception ex)
        {
            // The database has already moved by this point, so the live store is intact and the old
            // snapshots are still readable where they are. Worth reporting, not worth refusing to start.
            result.Failed.Add($"{legacy}: {ex.Message}");
            logger?.LogWarning(ex, "Could not move {Legacy} to {Target}; the files were left in place", legacy, target);
        }
    }

    private static void RemoveIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch
        {
            // An empty folder left behind is cosmetic.
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
