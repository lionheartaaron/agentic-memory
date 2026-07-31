namespace AgenticMemory.Brain.Interfaces;

/// <summary>
/// Point-in-time snapshots of the whole store, taken immediately before anything that removes or
/// rewrites data.
///
/// Every destructive path in this system is now conservative — ageing archives rather than deletes,
/// forgetting is a tombstone, consolidation merges rather than drops. But "conservative" is a
/// property of the code as written, and the operations that survive are precisely the ones with no
/// undo: a purge past its retention window, a rebuild, a wipe. A snapshot costs a file copy and
/// converts every one of those from irreversible to recoverable.
/// </summary>
public interface IMemoryBackupService
{
    /// <summary>Absolute path to the folder snapshots are written to.</summary>
    string BackupDirectory { get; }

    /// <summary>
    /// Copies the database to the backup directory. Returns null when backups are disabled or the
    /// snapshot could not be taken — the caller decides whether that blocks the operation.
    /// </summary>
    Task<BackupSnapshot?> CreateSnapshotAsync(string reason, CancellationToken cancellationToken = default);

    /// <summary>Existing snapshots, newest first.</summary>
    IReadOnlyList<BackupSnapshot> ListSnapshots();

    /// <summary>Deletes all but the newest <paramref name="keep"/> snapshots. Returns how many went.</summary>
    int PruneSnapshots(int keep);
}

/// <param name="Path">Absolute path to the snapshot file. Restoring is a file copy with the server stopped.</param>
/// <param name="Reason">Which operation was about to run.</param>
public sealed record BackupSnapshot(string Path, string Reason, DateTime CreatedAt, long SizeBytes);
