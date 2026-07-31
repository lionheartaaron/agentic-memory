using System.Diagnostics;
using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Retrieval;
using AgenticMemory.Brain.Search;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// Behaviour of the retrieval pipeline at a size a long-lived companion actually reaches.
///
/// The scale that matters here is per user: the vector channel scores the scope-filtered set, so
/// what bounds a query is how much one person has told this system, not how many people use it.
/// Ten thousand memories is several years of daily conversation.
/// </summary>
public class RetrievalScaleTests(ITestOutputHelper output) : MemoryServiceTestBase
{
    private const string User = "aaron";

    [Fact]
    public void SimdCosineAgreesWithTheScalarDefinition()
    {
        var random = new Random(20260730);

        for (var trial = 0; trial < 50; trial++)
        {
            var a = new float[384];
            var b = new float[384];
            for (var i = 0; i < a.Length; i++)
            {
                a[i] = (float)(random.NextDouble() * 2 - 1);
                b[i] = (float)(random.NextDouble() * 2 - 1);
            }

            var expected = CosineSimilarity(a, b);
            Assert.Equal(expected, VectorMath.CosineSimilarity(a, b), 4);

            // The cached path: normalise once, then compare with a bare dot product.
            Assert.True(VectorMath.TryUnitSimilarity(
                VectorMath.Normalize(a), VectorMath.Normalize(b), out var viaUnit));
            Assert.Equal(expected, viaUnit, 4);
        }
    }

    [Fact]
    public void SimdHandlesVectorLengthsThatAreNotAWholeNumberOfBlocks()
    {
        // 385 is prime to every SIMD width, so the scalar remainder loop has to run.
        var random = new Random(7);
        var a = new float[385];
        var b = new float[385];
        for (var i = 0; i < a.Length; i++)
        {
            a[i] = (float)random.NextDouble();
            b[i] = (float)random.NextDouble();
        }

        Assert.Equal(CosineSimilarity(a, b), VectorMath.CosineSimilarity(a, b), 4);
    }

    [Fact]
    public void TheVectorCacheDecodesEachMemoryOnceUntilItChanges()
    {
        var cache = new MemoryVectorCache();
        var memory = CreateTestMemory("Note", "something", userId: User);
        memory.SetEmbedding(Enumerable.Range(0, 8).Select(i => (float)i).ToArray(), "stamp");

        var first  = cache.Get(memory, "stamp", 8);
        var second = cache.Get(memory, "stamp", 8);

        Assert.NotNull(first);
        Assert.Same(first, second);

        // Unit length, so comparisons are a single dot product.
        Assert.Equal(1.0, Math.Sqrt(first!.Sum(v => (double)v * v)), 4);

        // A content change bumps the version, and the entry must not survive it.
        memory.Version++;
        Assert.NotSame(first, cache.Get(memory, "stamp", 8));
    }

    /// <summary>Reinforcement is not a content change; invalidating on it would defeat the cache.</summary>
    [Fact]
    public void ReinforcementDoesNotInvalidateTheCaches()
    {
        var vectors = new MemoryVectorCache();
        var lexical = new MemoryLexicalCache();

        var memory = CreateTestMemory("Note", "something", userId: User);
        memory.SetEmbedding([1f, 0f, 0f, 0f], "stamp");

        var vectorBefore  = vectors.Get(memory, "stamp", 4);
        var lexicalBefore = lexical.Get(memory);

        memory.Reinforce();

        Assert.Same(vectorBefore,  vectors.Get(memory, "stamp", 4));
        Assert.Same(lexicalBefore, lexical.Get(memory));
    }

    [Fact]
    public async Task SearchStaysResponsiveAcrossTenThousandMemories()
    {
        Assert.SkipUnless(EmbeddingService?.IsAvailable == true, "Embedding model unavailable");

        const int corpusSize = 10_000;

        // One embedding reused across the filler, so the test measures retrieval rather than the
        // ONNX model. The target memory gets a genuine vector.
        var filler = await EmbeddingService!.GetEmbeddingAsync("an unremarkable note about nothing much", Ct);
        var stamp  = AgenticMemory.Brain.Storage.MemoryTextIndexer.BuildEmbeddingStamp(EmbeddingService.ModelId);

        var target = CreateTestMemory("Allergy", "The user is allergic to shellfish", content: "", userId: User);
        target.SetEmbedding(
            await EmbeddingService.GetEmbeddingAsync(
                AgenticMemory.Brain.Storage.MemoryTextIndexer.BuildEmbeddingText(target), Ct),
            stamp);
        await Repository.SaveAsync(target, Ct);

        var batch = new MemoryWriteBatch();
        for (var i = 0; i < corpusSize; i++)
        {
            var memory = CreateTestMemory($"Note {i}", $"An unremarkable note, number {i}", content: "", userId: User);
            memory.SetEmbedding(filler, stamp);
            batch.Upsert(memory);

            if (batch.Upserts.Count >= 1000)
            {
                await Repository.ExecuteAsync(batch, "test:seed", Ct);
                batch = new MemoryWriteBatch();
            }
        }
        if (!batch.IsEmpty) await Repository.ExecuteAsync(batch, "test:seed", Ct);

        var request = new RetrievalRequest
        {
            Query = "what can the user not eat", Scope = MemoryScope.AllFor(User), TopN = 10, Reinforce = false,
        };

        // First query populates the caches; the steady state is what a live session experiences.
        var cold = Stopwatch.StartNew();
        var first = await SearchService.RetrieveAsync(request, Ct);
        cold.Stop();

        var warm = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) await SearchService.RetrieveAsync(request, Ct);
        warm.Stop();

        var perQuery = warm.Elapsed.TotalMilliseconds / 5;
        output.WriteLine($"corpus {corpusSize + 1:N0} | cold {cold.ElapsedMilliseconds} ms | warm {perQuery:F0} ms/query");

        Assert.Contains(first.Results, r => r.Memory.Title == "Allergy");

        // Generous, because CI hardware varies wildly. It is set to catch an accidental return to
        // per-comparison vector decoding or re-tokenizing the corpus on every query, either of which
        // costs an order of magnitude, not a few percent.
        Assert.True(perQuery < 2000,
            $"retrieval over {corpusSize:N0} memories took {perQuery:F0} ms/query");
    }

    /// <summary>
    /// Scope is an indexed predicate, so one user's corpus must not slow another's query — this is
    /// what makes a single shared database viable for many users.
    /// </summary>
    [Fact]
    public async Task OneUsersCorpusDoesNotBecomeAnotherUsersCandidateSet()
    {
        var batch = new MemoryWriteBatch();
        for (var i = 0; i < 2000; i++)
            batch.Upsert(CreateTestMemory($"Note {i}", $"noise {i}", content: "", userId: "somebody-else"));
        await Repository.ExecuteAsync(batch, "test:seed", Ct);

        await Repository.SaveAsync(CreateTestMemory("Mine", "the user cycles to work", userId: User), Ct);

        var result = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "cycles to work", Scope = MemoryScope.AllFor(User), TopN = 5, Reinforce = false,
        }, Ct);

        Assert.Equal(1, result.CandidatesConsidered);
        Assert.Equal("Mine", Assert.Single(result.Results).Memory.Title);
    }
}
