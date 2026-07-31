using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Retrieval;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// What each companion has already brought up.
///
/// A shared fact is not shared knowledge: Aria and Mika can both see that the user is allergic to
/// shellfish, but only one of them has actually said so. Without this distinction every companion
/// re-announces every relevant memory on every turn, which is the characteristic way a companion app
/// stops feeling like someone who remembers.
/// </summary>
public class CompanionAwarenessTests : MemoryServiceTestBase
{
    private const string User = "aaron";

    private async Task<MemoryNodeEntity> SeedAsync(string title, string summary)
    {
        var memory = CreateTestMemory(title, summary, userId: User);
        await Repository.SaveAsync(memory, Ct);
        return memory;
    }

    [Fact]
    public async Task SurfacingIsRecordedPerCompanionAndCountsUp()
    {
        var memory = await SeedAsync("Allergy", "The user is allergic to shellfish");
        var aria = MemoryScope.For(User, "aria");

        await Repository.RecordSurfacedAsync(aria, [memory.Id], Ct);
        await Repository.RecordSurfacedAsync(aria, [memory.Id], Ct);

        var seen = await Repository.GetAwarenessAsync(aria, [memory.Id], Ct);

        Assert.Equal(2, seen[memory.Id].SurfaceCount);
        Assert.True(seen[memory.Id].LastSurfacedAt >= seen[memory.Id].FirstSurfacedAt);
    }

    [Fact]
    public async Task OneCompanionMentioningAFactTellsUsNothingAboutAnother()
    {
        var memory = await SeedAsync("Allergy", "The user is allergic to shellfish");

        await Repository.RecordSurfacedAsync(MemoryScope.For(User, "aria"), [memory.Id], Ct);

        var mika = await Repository.GetAwarenessAsync(MemoryScope.For(User, "mika"), [memory.Id], Ct);
        Assert.Empty(mika);

        var aria = await Repository.GetAwarenessAsync(MemoryScope.For(User, "aria"), [memory.Id], Ct);
        Assert.Equal(1, aria[memory.Id].SurfaceCount);
    }

    [Fact]
    public async Task SearchRecordsWhatTheCompanionDrewOnAndReportsItBack()
    {
        await SeedAsync("Bicycle", "The user cycles to work most mornings");
        var aria = MemoryScope.For(User, "aria");

        var first = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "cycles to work", Scope = aria, TopN = 5,
        }, Ct);

        Assert.NotEmpty(first.Results);
        Assert.All(first.Results, r => Assert.Equal(0, r.TimesSurfacedToCompanion));

        var second = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "cycles to work", Scope = aria, TopN = 5,
        }, Ct);

        Assert.Contains(second.Results, r => r.TimesSurfacedToCompanion == 1);
        Assert.Contains(second.Results, r => r.LastSurfacedToCompanionAt is not null);
    }

    [Fact]
    public async Task AnotherCompanionSeesTheSameFactAsUnmentioned()
    {
        await SeedAsync("Bicycle", "The user cycles to work most mornings");

        await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "cycles to work", Scope = MemoryScope.For(User, "aria"), TopN = 5,
        }, Ct);

        var mika = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "cycles to work", Scope = MemoryScope.For(User, "mika"), TopN = 5,
        }, Ct);

        Assert.NotEmpty(mika.Results);
        Assert.All(mika.Results, r => Assert.Equal(0, r.TimesSurfacedToCompanion));
    }

    /// <summary>
    /// Surfacing must not count as a change to the memory. If it bumped the version, every read
    /// would invalidate the vector and lexical caches for every other companion.
    /// </summary>
    [Fact]
    public async Task RecordingAwarenessDoesNotModifyTheMemory()
    {
        var memory = await SeedAsync("Bicycle", "The user cycles to work most mornings");
        var before = await Repository.GetAsync(memory.Id, MemoryScope.AllFor(User), Ct);

        var aria = MemoryScope.For(User, "aria");
        for (var i = 0; i < 5; i++)
            await Repository.RecordSurfacedAsync(aria, [memory.Id], Ct);

        var after = await Repository.GetAsync(memory.Id, MemoryScope.AllFor(User), Ct);

        Assert.Equal(before!.Version, after!.Version);
        Assert.Equal(before.AccessCount, after.AccessCount);
    }

    [Fact]
    public async Task AwarenessTrackingCanBeDisabledForBackgroundReads()
    {
        var memory = await SeedAsync("Bicycle", "The user cycles to work most mornings");
        var aria = MemoryScope.For(User, "aria");

        await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "cycles to work", Scope = aria, TopN = 5, TrackAwareness = false, Reinforce = false,
        }, Ct);

        Assert.Empty(await Repository.GetAwarenessAsync(aria, [memory.Id], Ct));
    }

    [Fact]
    public async Task AScopeWithoutACompanionRecordsNothing()
    {
        var memory = await SeedAsync("Bicycle", "The user cycles to work most mornings");
        var everyone = MemoryScope.AllFor(User);

        await Repository.RecordSurfacedAsync(everyone, [memory.Id], Ct);

        Assert.Empty(await Repository.GetAwarenessAsync(everyone, [memory.Id], Ct));
    }

    [Fact]
    public async Task NoveltyBiasPrefersWhatThisCompanionHasNotSaidYet()
    {
        var stale = await SeedAsync("Bouldering", "The user goes bouldering on Saturdays");
        await SeedAsync("Bouldering partner", "The user goes bouldering with Sam");

        var aria = MemoryScope.For(User, "aria");

        // Aria has leaned on the first fact repeatedly.
        for (var i = 0; i < 8; i++)
            await Repository.RecordSurfacedAsync(aria, [stale.Id], Ct);

        var biased = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "bouldering", Scope = aria, TopN = 2,
            NoveltyBias = 1.0, TrackAwareness = false, Reinforce = false,
        }, Ct);

        var neutral = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "bouldering", Scope = aria, TopN = 2,
            NoveltyBias = 0, TrackAwareness = false, Reinforce = false,
        }, Ct);

        var biasedRank  = biased.Results.ToList().FindIndex(r => r.Memory.Id == stale.Id);
        var neutralRank = neutral.Results.ToList().FindIndex(r => r.Memory.Id == stale.Id);

        // Still present — novelty reorders, it never hides an answer.
        Assert.Contains(biased.Results, r => r.Memory.Id == stale.Id);
        Assert.True(biasedRank >= neutralRank,
            $"well-worn memory should not rank higher under novelty bias (was {neutralRank}, now {biasedRank})");
    }

    [Fact]
    public async Task ANeverMentionedFactIsUnaffectedByNoveltyBias()
    {
        await SeedAsync("Allergy", "The user is allergic to shellfish");

        var result = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "allergic to shellfish", Scope = MemoryScope.For(User, "aria"), TopN = 5,
            NoveltyBias = 1.0, TrackAwareness = false, Reinforce = false,
        }, Ct);

        var top = Assert.Single(result.Results, r => r.Memory.Title == "Allergy");
        Assert.Equal(1.0, top.Score, 3);
    }

    [Fact]
    public async Task PurgingAMemoryRemovesItsAwarenessRows()
    {
        var memory = await SeedAsync("Forgettable", "something the user retracted");
        var aria = MemoryScope.For(User, "aria");

        await Repository.RecordSurfacedAsync(aria, [memory.Id], Ct);
        await Repository.ForgetAsync(memory.Id, MemoryScope.AllFor(User), "test", Ct);
        await AdminStore.PurgeForgottenAsync(TimeSpan.Zero, "test", Ct);

        Assert.Empty(await Repository.GetAwarenessAsync(aria, [memory.Id], Ct));
    }
}
