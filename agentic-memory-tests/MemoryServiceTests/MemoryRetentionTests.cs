using AgenticMemory.Brain.Models;
using AgenticMemory.Configuration;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// Retention guarantees.
///
/// The headline defect these cover: decay used to hard-delete any memory whose strength fell below
/// a threshold, which with default settings meant permanent deletion roughly 23–46 days after the
/// last retrieval. Ageing must now only ever archive, and only for memory types that are supposed
/// to age at all.
/// </summary>
public class MemoryRetentionTests : MemoryServiceTestBase
{
    private const string User = "aaron";

    [Fact]
    public async Task AYearOfNeglect_LosesNothing()
    {
        const int count = 500;
        var ids = new List<Guid>();

        for (var i = 0; i < count; i++)
        {
            var memory = CreateTestMemory($"Fact {i}", $"Something the user told us, number {i}", userId: User);

            // Simulate a year without recall: strength decays to effectively zero.
            memory.CreatedAt = DateTime.UtcNow.AddDays(-365);
            memory.LastAccessedAt = DateTime.UtcNow.AddDays(-365);
            memory.ValidFrom = memory.CreatedAt;
            memory.IngestedAt = memory.CreatedAt;

            await Repository.SaveAsync(memory, Ct);
            ids.Add(memory.Id);
        }

        var upkeep = await Maintenance.RunUpkeepAsync(new MaintenanceSettings(), Ct);
        Assert.True(upkeep.Success, upkeep.ErrorMessage);
        Assert.Equal(0, upkeep.MemoriesDeleted);
        Assert.Equal(0, upkeep.ArchivedToCold);   // Semantic memories never age out.

        var stats = await Repository.GetStatsAsync(MemoryScope.AllFor(User), Ct);
        Assert.Equal(count, stats.ActiveNodes);

        foreach (var id in ids)
            Assert.NotNull(await Repository.GetAsync(id, MemoryScope.AllFor(User), Ct));
    }

    [Fact]
    public async Task VeryWeakMemory_IsStillRetrievable()
    {
        var memory = CreateTestMemory("Mother's name", "The user's mother is called Elena", userId: User);
        memory.LastAccessedAt = DateTime.UtcNow.AddDays(-400);
        memory.BaseStrength = 1.0;
        await Repository.SaveAsync(memory, Ct);

        // Strength has decayed essentially to nothing...
        Assert.True(memory.GetCurrentStrength() < 0.01);

        await Maintenance.RunUpkeepAsync(new MaintenanceSettings(), Ct);

        // ...but the memory is intact and still findable.
        var found = await SearchService.RetrieveAsync(new AgenticMemory.Brain.Retrieval.RetrievalRequest
        {
            Query = "what is the user's mother called", Scope = MemoryScope.AllFor(User), TopN = 5,
        }, Ct);

        Assert.Contains(found.Results, r => r.Memory.Id == memory.Id);
    }

    [Fact]
    public async Task OldEpisodicMemories_AreArchivedNotDeleted()
    {
        var episodic = CreateTestMemory(
            "Tuesday chat", "We talked about the user's garden", userId: User, type: MemoryType.Episodic);
        episodic.LastAccessedAt = DateTime.UtcNow.AddDays(-400);
        await Repository.SaveAsync(episodic, Ct);

        var result = await Maintenance.RunUpkeepAsync(new MaintenanceSettings { ArchiveEpisodicAfterDays = 180 }, Ct);

        Assert.Equal(1, result.ArchivedToCold);

        // Archived, not gone: still fetchable and restorable.
        var stored = await AdminStore.GetByIdUnscopedAsync(episodic.Id, Ct);
        Assert.NotNull(stored);
        Assert.Equal(MemoryState.Archived, stored.State);

        Assert.True(await Repository.RestoreAsync(episodic.Id, MemoryScope.AllFor(User), "test", Ct));
    }

    [Theory]
    [InlineData(MemoryType.Semantic)]
    [InlineData(MemoryType.Identity)]
    [InlineData(MemoryType.Preference)]
    [InlineData(MemoryType.Persona)]
    [InlineData(MemoryType.Affective)]
    public async Task DurableMemoryTypes_NeverAgeOut(MemoryType type)
    {
        var memory = CreateTestMemory($"{type} fact", "should survive indefinitely", userId: User, type: type);
        memory.LastAccessedAt = DateTime.UtcNow.AddDays(-3650);
        await Repository.SaveAsync(memory, Ct);

        await Maintenance.RunUpkeepAsync(new MaintenanceSettings { ArchiveEpisodicAfterDays = 1 }, Ct);

        var stored = await Repository.GetAsync(memory.Id, MemoryScope.AllFor(User), Ct);
        Assert.NotNull(stored);
        Assert.Equal(MemoryState.Active, stored.State);
    }

    [Fact]
    public async Task PinnedEpisodicMemory_IsExemptFromAgeing()
    {
        var memory = CreateTestMemory("First conversation", "The day we met", userId: User, type: MemoryType.Episodic);
        memory.IsPinned = true;
        memory.LastAccessedAt = DateTime.UtcNow.AddDays(-3650);
        await Repository.SaveAsync(memory, Ct);

        await Maintenance.RunUpkeepAsync(new MaintenanceSettings { ArchiveEpisodicAfterDays = 1 }, Ct);

        var stored = await Repository.GetAsync(memory.Id, MemoryScope.AllFor(User), Ct);
        Assert.Equal(MemoryState.Active, stored!.State);
    }

    [Fact]
    public async Task ExpiredEphemeralMemory_StopsBeingRecalledImmediately()
    {
        var memory = CreateTestMemory(
            "On a train", "The user is on a train right now", userId: User, type: MemoryType.Ephemeral);
        memory.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await Repository.SaveAsync(memory, Ct);

        // Expiry is enforced by the scope predicate, not only by the background sweep.
        var active = await Repository.GetActiveAsync(MemoryScope.AllFor(User), Ct);
        Assert.DoesNotContain(active, m => m.Id == memory.Id);

        var upkeep = await Maintenance.RunUpkeepAsync(new MaintenanceSettings(), Ct);
        Assert.Equal(1, upkeep.Expired);
    }

    [Fact]
    public async Task UnexpiredEphemeralMemory_IsStillRecalled()
    {
        var memory = CreateTestMemory(
            "On a train", "The user is on a train right now", userId: User, type: MemoryType.Ephemeral);
        memory.ExpiresAt = DateTime.UtcNow.AddHours(2);
        await Repository.SaveAsync(memory, Ct);

        var active = await Repository.GetActiveAsync(MemoryScope.AllFor(User), Ct);
        Assert.Contains(active, m => m.Id == memory.Id);
    }

    [Fact]
    public async Task ForgottenMemory_IsNotPurgedBeforeItsRetentionWindow()
    {
        var memory = CreateTestMemory("Regrettable", "asked to be forgotten", userId: User);
        await Repository.SaveAsync(memory, Ct);
        await Repository.ForgetAsync(memory.Id, MemoryScope.AllFor(User), "test", Ct);

        var purged = await AdminStore.PurgeForgottenAsync(TimeSpan.FromDays(30), "test", Ct);

        Assert.Equal(0, purged);
        Assert.NotNull(await AdminStore.GetByIdUnscopedAsync(memory.Id, Ct));
    }

    [Fact]
    public async Task Consolidation_KeepsTheMostCurrentVersionNotTheMostAccessed()
    {
        // An old memory that has been recalled many times, and so has high accumulated strength.
        var old = CreateTestMemory("Job", "The user works at Acme", userId: User,
            predicate: "employer", value: "acme");
        old.BaseStrength = 5.0;
        old.AccessCount = 500;
        old.ValidFrom = DateTime.UtcNow.AddYears(-2);
        await Repository.SaveAsync(old, Ct);

        // The newer, correct one.
        var recent = CreateTestMemory("Job", "The user works at Acme", userId: User,
            predicate: "employer", value: "acme");
        recent.BaseStrength = 1.0;
        recent.ValidFrom = DateTime.UtcNow;
        await Repository.SaveAsync(recent, Ct);

        await Maintenance.ConsolidateMemoriesAsync(0.9, Ct);

        var survivor = await Repository.GetAsync(recent.Id, MemoryScope.AllFor(User), Ct);
        Assert.Equal(MemoryState.Active, survivor!.State);

        // The older duplicate is merged, never destroyed.
        var merged = await AdminStore.GetByIdUnscopedAsync(old.Id, Ct);
        Assert.NotNull(merged);
        Assert.Equal(MemoryState.Merged, merged.State);
    }

    [Fact]
    public async Task Consolidation_NeverMergesAcrossCompanionScopes()
    {
        var shared = CreateTestMemory("Coffee", "The user likes coffee", userId: User);
        await Repository.SaveAsync(shared, Ct);

        var privateCopy = CreateTestMemory("Coffee", "The user likes coffee", userId: User,
            companionId: "aria", visibility: MemoryVisibility.Scoped);
        await Repository.SaveAsync(privateCopy, Ct);

        await Maintenance.ConsolidateMemoriesAsync(0.5, Ct);

        // Merging these would silently change who can recall the fact.
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(shared.Id, Ct))!.State);
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(privateCopy.Id, Ct))!.State);
    }

    [Fact]
    public async Task Consolidation_ScalesWithoutComparingEveryPair()
    {
        for (var i = 0; i < 300; i++)
            await Repository.SaveAsync(
                CreateTestMemory($"Note {i}", $"An unrelated observation about topic {i}", userId: User), Ct);

        var result = await Maintenance.ConsolidateMemoriesAsync(0.9, Ct);

        Assert.True(result.Success, result.ErrorMessage);

        // Blocking must keep this far below the 300*299/2 = 44,850 of an all-pairs sweep.
        Assert.True(result.ComparisonsPerformed < 5000,
            $"Expected blocking to bound comparisons, performed {result.ComparisonsPerformed}");
    }
}
