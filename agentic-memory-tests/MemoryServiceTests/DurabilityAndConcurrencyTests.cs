using AgenticMemory.Brain.Models;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// Atomicity, concurrency and the audit trail.
///
/// The invariant behind most of these: a supersede archives the old memory and writes the
/// replacement as one unit. Applied separately — as they previously were, across N independent
/// saves followed by the insert — a crash in the middle left the old fact archived and the new one
/// absent, so the fact disappeared entirely.
/// </summary>
public class DurabilityAndConcurrencyTests : MemoryServiceTestBase
{
    private const string User = "aaron";

    [Fact]
    public async Task FailedBatch_LeavesNothingPartiallyApplied()
    {
        var original = CreateTestMemory("Employer", "The user works at Acme", userId: User,
            predicate: "employer", value: "acme");
        await Repository.SaveAsync(original, Ct);

        var replacement = CreateTestMemory("Employer", "The user works at Globex", userId: User,
            predicate: "employer", value: "globex");

        // A batch that archives the old memory and writes a replacement whose version guard cannot
        // be satisfied — the same shape as a supersede interrupted midway.
        var doomed = new MemoryWriteBatch()
            .ChangeState(original.Id, MemoryState.Superseded, replacement.Id)
            .Upsert(replacement, expectedVersion: 99);

        await Assert.ThrowsAsync<MemoryConcurrencyException>(
            () => Repository.ExecuteAsync(doomed, "test", Ct));

        // The old fact must not have been archived without its successor being present.
        var stored = await AdminStore.GetByIdUnscopedAsync(original.Id, Ct);
        Assert.Equal(MemoryState.Active, stored!.State);
        Assert.Null(await AdminStore.GetByIdUnscopedAsync(replacement.Id, Ct));
    }

    [Fact]
    public async Task SupersedeAndInsert_CommitTogether()
    {
        var original = CreateTestMemory("Employer", "Acme", userId: User, predicate: "employer", value: "acme");
        await Repository.SaveAsync(original, Ct);

        var replacement = CreateTestMemory("Employer", "Globex", userId: User, predicate: "employer", value: "globex");

        await Repository.ExecuteAsync(new MemoryWriteBatch()
            .Upsert(replacement)
            .ChangeState(original.Id, MemoryState.Superseded, replacement.Id), "test", Ct);

        Assert.Equal(MemoryState.Superseded, (await AdminStore.GetByIdUnscopedAsync(original.Id, Ct))!.State);
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(replacement.Id, Ct))!.State);
    }

    [Fact]
    public async Task StaleWrite_IsRejectedRatherThanClobberingTheNewerValue()
    {
        var memory = CreateTestMemory("Note", "original", userId: User);
        await Repository.SaveAsync(memory, Ct);

        var first  = await Repository.GetAsync(memory.Id, MemoryScope.AllFor(User), Ct);
        var second = await Repository.GetAsync(memory.Id, MemoryScope.AllFor(User), Ct);

        first!.Summary = "first writer";
        await Repository.SaveAsync(first, first.Version, "writer-1", Ct);

        second!.Summary = "second writer";
        await Assert.ThrowsAsync<MemoryConcurrencyException>(
            () => Repository.SaveAsync(second, second.Version, "writer-2", Ct));

        var final = await Repository.GetAsync(memory.Id, MemoryScope.AllFor(User), Ct);
        Assert.Equal("first writer", final!.Summary);
    }

    [Fact]
    public async Task Reinforcement_DoesNotBumpVersionAndCannotLoseAnEdit()
    {
        var memory = CreateTestMemory("Note", "original", userId: User);
        await Repository.SaveAsync(memory, Ct);

        var loaded = await Repository.GetAsync(memory.Id, MemoryScope.AllFor(User), Ct);
        var version = loaded!.Version;

        // Reinforcement is a ranking signal only; it must not invalidate a concurrent editor's
        // version, or every search would break every in-flight edit.
        await Repository.ReinforceAsync(memory.Id, Ct);

        loaded.Summary = "edited";
        await Repository.SaveAsync(loaded, version, "editor", Ct);

        var final = await Repository.GetAsync(memory.Id, MemoryScope.AllFor(User), Ct);
        Assert.Equal("edited", final!.Summary);
        Assert.True(final.AccessCount >= 1);
    }

    [Fact]
    public async Task ConcurrentStores_AllSurvive()
    {
        var tasks = Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            var memory = CreateTestMemory($"Concurrent {i}", $"Written by task {i}", userId: User);
            await Repository.SaveAsync(memory, CancellationToken.None);
        })).ToArray();

        await Task.WhenAll(tasks);

        var stats = await Repository.GetStatsAsync(MemoryScope.AllFor(User), Ct);
        Assert.Equal(40, stats.ActiveNodes);
    }

    [Fact]
    public async Task LifecycleEvents_AreRecorded()
    {
        var memory = CreateTestMemory("Tracked", "watch what happens", userId: User);
        await Repository.SaveAsync(memory, Ct);
        await Repository.ForgetAsync(memory.Id, MemoryScope.AllFor(User), "user-request", Ct);
        await Repository.RestoreAsync(memory.Id, MemoryScope.AllFor(User), "oops", Ct);

        var events = await EventLog.GetForMemoryAsync(memory.Id, Ct);

        Assert.Contains(events, e => e.Type == MemoryEventType.Created);
        Assert.Contains(events, e => e.Type == MemoryEventType.Forgotten && e.Actor == "user-request");
        Assert.Contains(events, e => e.Type == MemoryEventType.Restored);

        // Ordering is by an explicit sequence, not a wall clock.
        Assert.Equal(events.OrderBy(e => e.Sequence).Select(e => e.Id), events.Select(e => e.Id));
    }

    [Fact]
    public async Task SupersedeEvent_LinksToTheReplacement()
    {
        var old = CreateTestMemory("Employer", "Acme", userId: User, predicate: "employer", value: "acme");
        await ConflictStorage.StoreAsync(old, MemoryScope.AllFor(User), "test", Ct);

        var replacement = CreateTestMemory("Employer", "Globex", userId: User, predicate: "employer", value: "globex");
        await ConflictStorage.StoreAsync(replacement, MemoryScope.AllFor(User), "test", Ct);

        var events = await EventLog.GetForMemoryAsync(old.Id, Ct);
        var superseded = Assert.Single(events, e => e.Type == MemoryEventType.Superseded);

        Assert.Equal(replacement.Id, superseded.RelatedMemoryId);
    }

    [Fact]
    public async Task Reinforcement_IsNotWrittenToTheAuditLog()
    {
        var memory = CreateTestMemory("Popular", "recalled often", userId: User);
        await Repository.SaveAsync(memory, Ct);

        for (var i = 0; i < 25; i++)
            await Repository.ReinforceAsync(memory.Id, Ct);

        var events = await EventLog.GetForMemoryAsync(memory.Id, Ct);

        // Every search reinforces; logging that would swamp the log without adding information.
        Assert.Single(events);
        Assert.Equal(MemoryEventType.Created, events[0].Type);
    }

    [Fact]
    public async Task DatesRoundTripAsUtc()
    {
        // LiteDB converts stored dates to local time on read unless the UTC_DATE pragma is set,
        // which silently skewed every comparison against DateTime.UtcNow on non-UTC machines.
        var written = DateTime.UtcNow.AddDays(-10);

        var memory = CreateTestMemory("Timestamped", "check the clock", userId: User);
        memory.LastAccessedAt = written;
        memory.CreatedAt = written;
        await Repository.SaveAsync(memory, Ct);

        var loaded = await Repository.GetAsync(memory.Id, MemoryScope.AllFor(User), Ct);

        Assert.True(Math.Abs((loaded!.LastAccessedAt - written).TotalMinutes) < 1,
            $"expected ~{written:O} but read back {loaded.LastAccessedAt:O}");

        var ageInDays = (DateTime.UtcNow - loaded.LastAccessedAt).TotalDays;
        Assert.InRange(ageInDays, 9.9, 10.1);
    }
}
