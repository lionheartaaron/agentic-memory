using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Retrieval;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// Isolation between users and between companions.
///
/// These are the tests that have to hold for a multi-companion app: a memory scoped to one
/// companion must be invisible to the others by every route — search, direct fetch, tag query,
/// slot history — and a user must never see another user's memories at all.
/// </summary>
public class CompanionScopingTests : MemoryServiceTestBase
{
    private const string User = "aaron";

    private async Task<MemoryNodeEntity> StoreAsync(
        string title, string summary, string? companionId, MemoryVisibility visibility,
        string? userId = null, List<string>? tags = null)
    {
        var memory = CreateTestMemory(
            title, summary, userId: userId ?? User, companionId: companionId,
            visibility: visibility, tags: tags);

        await Repository.SaveAsync(memory, Ct);
        return memory;
    }

    [Fact]
    public async Task PrivateMemory_IsInvisibleToOtherCompanions()
    {
        var secret = await StoreAsync(
            "Penguin joke", "Aria and the user have a running joke about penguins",
            "aria", MemoryVisibility.Scoped);

        // Searching as Mika with the memory's own text must find nothing.
        var asMika = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "penguin joke running", Scope = MemoryScope.For(User, "mika"), TopN = 20,
        }, Ct);

        Assert.Empty(asMika.Results);

        // ...and a direct fetch by id must not leak it either.
        Assert.Null(await Repository.GetAsync(secret.Id, MemoryScope.For(User, "mika"), Ct));

        // The owning companion still sees it.
        var asAria = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "penguin joke running", Scope = MemoryScope.For(User, "aria"), TopN = 20,
        }, Ct);

        Assert.Contains(asAria.Results, r => r.Memory.Id == secret.Id);
    }

    [Fact]
    public async Task GlobalMemory_IsVisibleToEveryCompanion()
    {
        var shared = await StoreAsync(
            "Shellfish allergy", "The user is allergic to shellfish", null, MemoryVisibility.Global);

        foreach (var companion in new[] { "aria", "mika", "nova" })
        {
            var result = await SearchService.RetrieveAsync(new RetrievalRequest
            {
                Query = "shellfish allergy", Scope = MemoryScope.For(User, companion), TopN = 10,
            }, Ct);

            Assert.Contains(result.Results, r => r.Memory.Id == shared.Id);
        }
    }

    /// <summary>
    /// The specific failure mode of building scoping on the old fuzzy tag matcher, which compared
    /// with bidirectional Contains: "ari" would have matched "aria" and leaked the memory.
    /// </summary>
    [Theory]
    [InlineData("aria", "ari")]
    [InlineData("ari", "aria")]
    [InlineData("mika", "mik")]
    [InlineData("nova", "novaa")]
    [InlineData("Aria", "aria-2")]
    public async Task SubstringCollidingCompanionNames_DoNotLeak(string owner, string other)
    {
        var secret = await StoreAsync($"Secret for {owner}", "confidential detail", owner, MemoryVisibility.Scoped);

        var leaked = await Repository.QueryAsync(
            MemoryScope.For(User, other), new MemoryQueryOptions { IncludeNonCurrent = true }, Ct);

        Assert.DoesNotContain(leaked, m => m.Id == secret.Id);
        Assert.Null(await Repository.GetAsync(secret.Id, MemoryScope.For(User, other), Ct));
    }

    [Fact]
    public async Task CompanionIds_AreMatchedCaseInsensitivelyButExactly()
    {
        var secret = await StoreAsync("Aria's note", "detail", "Aria", MemoryVisibility.Scoped);

        // Same identifier, different casing — should match.
        Assert.NotNull(await Repository.GetAsync(secret.Id, MemoryScope.For(User, "ARIA"), Ct));

        // Different identifier — must not.
        Assert.Null(await Repository.GetAsync(secret.Id, MemoryScope.For(User, "aria2"), Ct));
    }

    [Fact]
    public async Task DifferentUsers_AreCompletelyIsolated()
    {
        var aaronMemory = await StoreAsync("Favourite food", "Ramen", null, MemoryVisibility.Global, userId: "aaron");
        var jamieMemory = await StoreAsync("Favourite food", "Ramen", null, MemoryVisibility.Global, userId: "jamie");

        var aaronView = await Repository.QueryAsync(MemoryScope.AllFor("aaron"), null, Ct);
        var jamieView = await Repository.QueryAsync(MemoryScope.AllFor("jamie"), null, Ct);

        Assert.Single(aaronView);
        Assert.Single(jamieView);
        Assert.Equal(aaronMemory.Id, aaronView[0].Id);
        Assert.Equal(jamieMemory.Id, jamieView[0].Id);

        // Identical text, so a leak would surface here if the boundary were only a ranking filter.
        var search = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "ramen favourite food", Scope = MemoryScope.AllFor("aaron"), TopN = 50,
        }, Ct);

        Assert.All(search.Results, r => Assert.Equal("aaron", r.Memory.UserId));
    }

    [Fact]
    public async Task NoCompanionContext_SeesOnlySharedMemories()
    {
        await StoreAsync("Shared fact", "everyone knows this", null, MemoryVisibility.Global);
        var priv = await StoreAsync("Private fact", "only aria knows", "aria", MemoryVisibility.Scoped);

        var visible = await Repository.QueryAsync(MemoryScope.For(User, companionId: null), null, Ct);

        Assert.Single(visible);
        Assert.DoesNotContain(visible, m => m.Id == priv.Id);
    }

    /// <summary>
    /// A tag whose text matches a companion name must not grant access. Tags are soft
    /// categorisation and are never load-bearing for scope.
    /// </summary>
    [Fact]
    public async Task TagsNamedAfterACompanion_GrantNoAccess()
    {
        var secret = await StoreAsync("Mika's secret", "hidden", "mika", MemoryVisibility.Scoped, tags: ["aria"]);

        var asAria = await Repository.QueryAsync(
            MemoryScope.For(User, "aria"), new MemoryQueryOptions { Tags = ["aria"] }, Ct);

        Assert.DoesNotContain(asAria, m => m.Id == secret.Id);
    }

    [Fact]
    public async Task ScopedMemoryWithNoCompanions_FallsBackToGlobalRatherThanBecomingOrphaned()
    {
        var orphan = CreateTestMemory("No companions", "should not vanish", userId: User);
        orphan.Visibility = MemoryVisibility.Scoped;
        orphan.CompanionIds = [];

        await Repository.SaveAsync(orphan, Ct);

        var stored = await Repository.GetAsync(orphan.Id, MemoryScope.AllFor(User), Ct);
        Assert.NotNull(stored);
        Assert.Equal(MemoryVisibility.Global, stored.Visibility);
    }

    [Fact]
    public async Task TagFilter_DoesNotReturnFalseEmptyWhenMatchesAreOutsideTheTopLexicalHits()
    {
        // 60 distractors that all match the query strongly, plus one tagged match that does not.
        for (var i = 0; i < 60; i++)
            await StoreAsync($"Project update {i}", $"Status report number {i}", null, MemoryVisibility.Global);

        var needle = await StoreAsync(
            "Project update final", "Status report closing out the work", null, MemoryVisibility.Global,
            tags: ["archive-2019"]);

        // Filtering after truncating a candidate list would return nothing here.
        var result = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "project update status report", Scope = MemoryScope.AllFor(User),
            TopN = 5, Tags = ["archive-2019"],
        }, Ct);

        Assert.Single(result.Results);
        Assert.Equal(needle.Id, result.Results[0].Memory.Id);
    }
}
