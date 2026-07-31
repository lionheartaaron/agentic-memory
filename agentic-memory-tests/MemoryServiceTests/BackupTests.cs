using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Storage;
using AgenticMemory.Persistence;
using LiteDB;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// Snapshots before irreversible operations.
///
/// The point of these tests is not that a file appears — it is that the file is a genuinely
/// restorable database holding the memories that were there beforehand. A backup that opens but is
/// missing the most recent writes is worse than none, because it restores cleanly and silently
/// discards exactly the data that was in flight.
/// </summary>
public class BackupTests : MemoryServiceTestBase
{
    private const string User = "aaron";

    /// <summary>Opens a snapshot as its own read-only database and counts what survived.</summary>
    private static List<MemoryNodeEntity> ReadSnapshot(string path)
    {
        var copy = Path.Combine(Path.GetTempPath(), $"snapshot-read-{Guid.NewGuid():N}.db");
        File.Copy(path, copy, overwrite: true);

        try
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = copy, ReadOnly = true });
            return db.GetCollection<MemoryNodeEntity>(LiteDbMemoryRepository.CollectionName).FindAll().ToList();
        }
        finally
        {
            try { File.Delete(copy); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task SnapshotContainsEveryMemoryWrittenBeforeIt()
    {
        for (var i = 0; i < 25; i++)
            await Repository.SaveAsync(CreateTestMemory($"Memory {i}", $"Summary {i}", userId: User), Ct);

        var snapshot = await Backups.CreateSnapshotAsync("test", Ct);

        Assert.NotNull(snapshot);
        Assert.True(File.Exists(snapshot!.Path));

        var restored = ReadSnapshot(snapshot.Path);
        Assert.Equal(25, restored.Count);
        Assert.Contains(restored, m => m.Title == "Memory 24");
    }

    /// <summary>
    /// The write-ahead log must be folded in first. Without the checkpoint the copy is a valid
    /// database that is simply missing the newest transactions.
    /// </summary>
    [Fact]
    public async Task SnapshotIncludesWritesMadeImmediatelyBeforeIt()
    {
        await Repository.SaveAsync(CreateTestMemory("Early", "written well before", userId: User), Ct);
        await Repository.SaveAsync(CreateTestMemory("Latest", "written the instant before the snapshot", userId: User), Ct);

        var snapshot = await Backups.CreateSnapshotAsync("checkpoint", Ct);
        var restored = ReadSnapshot(snapshot!.Path);

        Assert.Contains(restored, m => m.Title == "Latest");
    }

    [Fact]
    public async Task PurgeTakesASnapshotBeforeDestroyingAnything()
    {
        var doomed = new List<Guid>();

        for (var i = 0; i < 5; i++)
        {
            var memory = CreateTestMemory($"Forgettable {i}", $"Summary {i}", userId: User);
            await Repository.SaveAsync(memory, Ct);
            await Repository.ForgetAsync(memory.Id, MemoryScope.AllFor(User), "test", Ct);
            doomed.Add(memory.Id);
        }

        var result = await Maintenance.PurgeForgottenAsync(TimeSpan.Zero, Ct);

        Assert.True(result.Success);
        Assert.Equal(5, result.MemoriesPurged);
        Assert.NotNull(result.SnapshotPath);

        // Gone from the live store...
        var live = await AdminStore.GetAllAsync(includeNonCurrent: true, Ct);
        Assert.DoesNotContain(live, m => doomed.Contains(m.Id));

        // ...and every one of them recoverable from the snapshot.
        var restored = ReadSnapshot(result.SnapshotPath!);
        Assert.All(doomed, id => Assert.Contains(restored, m => m.Id == id));
    }

    [Fact]
    public async Task CompactAndConsolidationAlsoSnapshot()
    {
        await Repository.SaveAsync(CreateTestMemory("Something", "worth keeping", userId: User), Ct);

        var consolidation = await Maintenance.ConsolidateMemoriesAsync(0.9, Ct);
        var compact       = await Maintenance.CompactDatabaseAsync(Ct);

        Assert.NotNull(consolidation.SnapshotPath);
        Assert.NotNull(compact.SnapshotPath);
    }

    [Fact]
    public async Task RetentionKeepsTheNewestSnapshotsAndPrunesTheRest()
    {
        await Repository.SaveAsync(CreateTestMemory("Anchor", "so the file is non-empty", userId: User), Ct);

        for (var i = 0; i < 6; i++)
            await Backups.CreateSnapshotAsync($"run{i}", Ct);

        Assert.Equal(6, Backups.ListSnapshots().Count);

        var pruned = Backups.PruneSnapshots(keep: 2);

        Assert.Equal(4, pruned);
        Assert.Equal(2, Backups.ListSnapshots().Count);
    }

    /// <summary>Two snapshots in the same second must not overwrite one another.</summary>
    [Fact]
    public async Task ConsecutiveSnapshotsDoNotCollide()
    {
        await Repository.SaveAsync(CreateTestMemory("Anchor", "content", userId: User), Ct);

        var first  = await Backups.CreateSnapshotAsync("same-reason", Ct);
        var second = await Backups.CreateSnapshotAsync("same-reason", Ct);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Path, second!.Path);
        Assert.True(File.Exists(first.Path));
        Assert.True(File.Exists(second.Path));
    }

    [Fact]
    public async Task BackupsCanBeTurnedOff()
    {
        var disabled = new LiteDbBackupService(
            Fixture.SharedDb!,
            new AgenticMemory.Configuration.MaintenanceSettings
            {
                BackupPath = Fixture.BackupPath,
                BackupBeforeDestructiveOperations = false,
            });

        Assert.Null(await disabled.CreateSnapshotAsync("test", Ct));
        Assert.Empty(disabled.ListSnapshots());
    }

    [Fact]
    public async Task SnapshotFailureDoesNotStopMaintenanceFromRunning()
    {
        var memory = CreateTestMemory("Forgettable", "summary", userId: User);
        await Repository.SaveAsync(memory, Ct);
        await Repository.ForgetAsync(memory.Id, MemoryScope.AllFor(User), "test", Ct);

        // Point the backup service at a path that cannot be created, so the snapshot fails.
        var broken = new AgenticMemory.Brain.Maintenance.MaintenanceService(
            Repository, AdminStore, EmbeddingService,
            new AgenticMemory.Configuration.MaintenanceSettings { BackupPath = "\0invalid" },
            new LiteDbBackupService(
                Fixture.SharedDb!,
                new AgenticMemory.Configuration.MaintenanceSettings { BackupPath = "\0invalid" }),
            null);

        var result = await broken.PurgeForgottenAsync(TimeSpan.Zero, Ct);

        // Refusing to run maintenance because a disk is unavailable would trade a recoverable risk
        // for a store that is never tidied at all.
        Assert.True(result.Success);
        Assert.Null(result.SnapshotPath);
        Assert.Equal(1, result.MemoriesPurged);
    }
}
