using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Retrieval;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// The user-bounded row cache must never be observable.
///
/// It exists because materialising every one of a user's documents per query — each carrying a
/// 1.5 KB embedding blob — was the dominant cost of a search. But a cache in front of a memory store
/// is exactly the mechanism by which a system starts "losing memories randomly": a write lands, the
/// next read serves the previous snapshot, and the fact appears to have vanished. Every test here
/// asserts that a change is visible to the very next read.
/// </summary>
public class RowCacheCoherenceTests : MemoryServiceTestBase
{
    private const string User = "aaron";

    private static MemoryScope UserScope => MemoryScope.AllFor(User);

    private async Task<int> CountAsync() => (await Repository.QueryAsync(UserScope, null, Ct)).Count;

    [Fact]
    public async Task ANewMemoryIsVisibleToTheVeryNextQuery()
    {
        Assert.Equal(0, await CountAsync());

        await Repository.SaveAsync(CreateTestMemory("First", "one", userId: User), Ct);
        Assert.Equal(1, await CountAsync());

        await Repository.SaveAsync(CreateTestMemory("Second", "two", userId: User), Ct);
        Assert.Equal(2, await CountAsync());
    }

    [Fact]
    public async Task AnEditIsVisibleImmediately()
    {
        var memory = CreateTestMemory("Employer", "The user works at Acme", userId: User);
        await Repository.SaveAsync(memory, Ct);

        // Read once so the row set is cached.
        Assert.Contains(await Repository.QueryAsync(UserScope, null, Ct), m => m.Summary.Contains("Acme"));

        var loaded = await Repository.GetAsync(memory.Id, UserScope, Ct);
        loaded!.Summary = "The user works at Globex";
        await Repository.SaveAsync(loaded, Ct);

        var after = await Repository.QueryAsync(UserScope, null, Ct);
        Assert.Contains(after, m => m.Summary.Contains("Globex"));
        Assert.DoesNotContain(after, m => m.Summary.Contains("Acme"));
    }

    [Fact]
    public async Task ForgettingRemovesAMemoryFromTheNextQuery()
    {
        var memory = CreateTestMemory("Forgettable", "something", userId: User);
        await Repository.SaveAsync(memory, Ct);
        Assert.Equal(1, await CountAsync());

        await Repository.ForgetAsync(memory.Id, UserScope, "test", Ct);
        Assert.Equal(0, await CountAsync());

        await Repository.RestoreAsync(memory.Id, UserScope, "test", Ct);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task ASupersedeIsReflectedInTheNextSearch()
    {
        var first = CreateTestMemory(
            "Employer", "The user works at Acme", content: "", userId: User, predicate: "employer", value: "acme");
        await ConflictStorage.StoreAsync(first, UserScope, "test", Ct);

        await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "employer", Scope = UserScope, Predicate = "employer", TopN = 5, Reinforce = false,
        }, Ct);

        var second = CreateTestMemory(
            "Employer", "The user works at Globex", content: "", userId: User, predicate: "employer", value: "globex");
        await ConflictStorage.StoreAsync(second, UserScope, "test", Ct);

        var after = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "employer", Scope = UserScope, Predicate = "employer", TopN = 5, Reinforce = false,
        }, Ct);

        Assert.Contains(after.Results, r => r.Memory.Summary.Contains("Globex"));
        Assert.DoesNotContain(after.Results, r => r.Memory.Summary.Contains("Acme"));
    }

    /// <summary>
    /// Reinforcement deliberately does not invalidate the cache — every search reinforces, so it
    /// would defeat the whole thing. The counts must still accumulate rather than resetting to
    /// whatever the cached snapshot held.
    /// </summary>
    [Fact]
    public async Task ReinforcementAccumulatesAcrossRepeatedSearches()
    {
        var memory = CreateTestMemory("Bicycle", "The user cycles to work", userId: User);
        await Repository.SaveAsync(memory, Ct);

        for (var i = 0; i < 4; i++)
            await SearchService.RetrieveAsync(new RetrievalRequest
            {
                Query = "cycles to work", Scope = UserScope, TopN = 5,
            }, Ct);

        var stored = await Repository.GetAsync(memory.Id, UserScope, Ct);
        Assert.Equal(4, stored!.AccessCount);

        // And the value the query path sees agrees with what is on disk.
        var viaQuery = (await Repository.QueryAsync(UserScope, null, Ct)).Single();
        Assert.Equal(4, viaQuery.AccessCount);
    }

    [Fact]
    public async Task OneUsersWritesNeverAppearInAnothersReads()
    {
        await Repository.SaveAsync(CreateTestMemory("Mine", "belongs to aaron", userId: User), Ct);
        await Repository.SaveAsync(CreateTestMemory("Theirs", "belongs to someone else", userId: "other"), Ct);

        // Prime both row sets.
        Assert.Single(await Repository.QueryAsync(UserScope, null, Ct));
        Assert.Single(await Repository.QueryAsync(MemoryScope.AllFor("other"), null, Ct));

        await Repository.SaveAsync(CreateTestMemory("Mine 2", "also aaron", userId: User), Ct);

        Assert.Equal(2, (await Repository.QueryAsync(UserScope, null, Ct)).Count);
        Assert.Single(await Repository.QueryAsync(MemoryScope.AllFor("other"), null, Ct));
    }

    [Fact]
    public async Task AReindexIsVisibleToTheNextSearch()
    {
        Assert.SkipUnless(EmbeddingService?.IsAvailable == true, "Embedding model unavailable");

        // Stored with no vector at all, so a search cannot reach it semantically.
        await Repository.SaveAsync(CreateTestMemory(
            "Allergy", "The user is allergic to shellfish", content: "", userId: User), Ct);

        await Repository.QueryAsync(UserScope, null, Ct);   // prime the cache

        var reindex = await Maintenance.ReindexAsync(force: true, Ct);
        Assert.True(reindex.Success);

        var stored = (await Repository.QueryAsync(UserScope, null, Ct)).Single();
        Assert.True(stored.EmbeddingDim > 0, "reindexed vector should be visible through the cached read path");
    }

    /// <summary>
    /// Interleaved writes and reads from several threads.
    ///
    /// The invariant asserted is per-reader monotonicity: within one thread, whose reads are ordered,
    /// a later read must never see fewer memories than an earlier one. That is the property a cache
    /// can break — by serving a snapshot older than one it has already served — and the property that
    /// would show up in production as a memory that "disappeared and came back".
    ///
    /// It is deliberately not asserted across threads. Two concurrent queries have overlapping
    /// intervals, so the one that returns second may well have started first; no store makes that
    /// ordering meaningful, and asserting it produces a flaky test rather than a stronger one.
    /// </summary>
    [Fact]
    public async Task ConcurrentWritesAndReadsNeverGoBackwardsWithinAReader()
    {
        const int total = 60;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < total; i++)
                await Repository.SaveAsync(CreateTestMemory($"Memory {i}", $"summary {i}", userId: User), Ct);
        }, Ct);

        var readers = Enumerable.Range(0, 3).Select(reader => Task.Run(async () =>
        {
            var high = 0;

            for (var i = 0; i < total; i++)
            {
                var count = (await Repository.QueryAsync(UserScope, null, Ct)).Count;

                if (count < high) failures.Add($"reader {reader} read {count} after its own earlier {high}");
                if (count > high) high = count;
            }
        }, Ct)).ToArray();

        await Task.WhenAll(readers.Append(writer));

        Assert.Empty(failures);
        Assert.Equal(total, (await Repository.QueryAsync(UserScope, null, Ct)).Count);
    }
}
