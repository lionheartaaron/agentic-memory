using System.Globalization;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Configuration;
using AgenticMemory.Persistence;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Brain.Storage;

/// <summary>
/// File-level snapshots of the LiteDB datafile.
///
/// A checkpoint is issued first so the write-ahead log is folded into the datafile; without it the
/// copy would be a valid database missing the most recent transactions — the worst possible outcome,
/// because it restores cleanly and silently omits exactly the writes that were in flight when
/// something went wrong.
/// </summary>
public sealed class LiteDbBackupService : IMemoryBackupService
{
    private readonly SharedLiteDatabase _database;
    private readonly MaintenanceSettings _settings;
    private readonly ILogger<LiteDbBackupService>? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LiteDbBackupService(
        SharedLiteDatabase database,
        MaintenanceSettings? settings = null,
        ILogger<LiteDbBackupService>? logger = null)
    {
        _database = database;
        _settings = settings ?? new MaintenanceSettings();
        _logger   = logger;
    }

    public string BackupDirectory =>
        string.IsNullOrWhiteSpace(_settings.BackupPath)
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_database.DatabasePath)) ?? ".", "backups")
            : Path.GetFullPath(_settings.BackupPath);

    public async Task<BackupSnapshot?> CreateSnapshotAsync(
        string reason, CancellationToken cancellationToken = default)
    {
        if (!_settings.BackupBeforeDestructiveOperations) return null;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var source = Path.GetFullPath(_database.DatabasePath);
            if (!File.Exists(source))
            {
                _logger?.LogWarning("Cannot snapshot: no database file at {Path}", source);
                return null;
            }

            Directory.CreateDirectory(BackupDirectory);

            // Fold the write-ahead log into the datafile so the copy is a complete, consistent image.
            _database.Database.Checkpoint();

            var stamp  = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var safe   = Sanitize(reason);
            var target = Path.Combine(BackupDirectory, $"{stamp}-{safe}.db");

            // Unique-ify rather than overwrite: two operations in the same second must not collide,
            // and a backup that silently replaces an earlier one is not a backup.
            var suffix = 1;
            while (File.Exists(target))
                target = Path.Combine(BackupDirectory, $"{stamp}-{safe}-{suffix++}.db");

            // FileShare.ReadWrite: the source is held open by the live connection.
            await using (var input = new FileStream(
                             source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true))
            await using (var output = new FileStream(
                             target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            var info = new FileInfo(target);
            _logger?.LogInformation(
                "Snapshot taken before {Reason}: {Path} ({Size:N0} bytes)", reason, target, info.Length);

            PruneSnapshots(_settings.BackupRetentionCount);

            return new BackupSnapshot(target, reason, DateTime.UtcNow, info.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to snapshot before {Reason}", reason);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public IReadOnlyList<BackupSnapshot> ListSnapshots()
    {
        if (!Directory.Exists(BackupDirectory)) return [];

        return Directory.EnumerateFiles(BackupDirectory, "*.db")
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.CreationTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new BackupSnapshot(f.FullName, ReasonFromName(f.Name), f.CreationTimeUtc, f.Length))
            .ToList();
    }

    public int PruneSnapshots(int keep)
    {
        if (keep <= 0) return 0;

        var deleted = 0;
        foreach (var snapshot in ListSnapshots().Skip(keep))
        {
            try
            {
                File.Delete(snapshot.Path);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Could not prune snapshot {Path}", snapshot.Path);
            }
        }

        return deleted;
    }

    /// <summary>"20260730-114500-maintenance_purge.db" → "maintenance_purge".</summary>
    private static string ReasonFromName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var parts = stem.Split('-', 3);
        return parts.Length == 3 ? parts[2] : stem;
    }

    private static string Sanitize(string reason)
    {
        var cleaned = new string(reason
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_')
            .ToArray())
            .Trim('_');

        return cleaned.Length == 0 ? "manual" : cleaned[..Math.Min(cleaned.Length, 40)];
    }
}
