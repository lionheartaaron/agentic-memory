using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Retrieval;
using AgenticMemory.Brain.Search;
using AgenticMemory.Brain.Storage;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// Embedding comparability.
///
/// Vectors from different models are not comparable. The previous code folded a dimension mismatch
/// into <c>CosineSimilarity</c>'s zero return, which the (cos+1)/2 mapping then turned into an
/// entirely ordinary-looking 0.5 — above the old "related memory" threshold. Changing the
/// configured model therefore degraded every similarity to a constant, silently.
/// </summary>
public class EmbeddingSafetyTests : MemoryServiceTestBase
{
    private const string User = "aaron";

    [Fact]
    public void DimensionMismatch_IsReportedNotSilentlyScoredAsHalf()
    {
        var a = new float[384];
        var b = new float[768];
        Array.Fill(a, 0.1f);
        Array.Fill(b, 0.1f);

        Assert.False(VectorMath.TryCosineSimilarity(a, b, out _));
        Assert.Null(VectorMath.NormalizedCosineSimilarity(a, b));
    }

    [Fact]
    public void ComparableVectors_StillScoreNormally()
    {
        var a = new float[8];
        var b = new float[8];
        Array.Fill(a, 1f);
        Array.Fill(b, 1f);

        Assert.True(VectorMath.TryCosineSimilarity(a, b, out var cosine));
        Assert.Equal(1.0f, cosine, 3);
        Assert.Equal(1.0f, VectorMath.NormalizedCosineSimilarity(a, b)!.Value, 3);
    }

    [Fact]
    public void EmbeddingsAreStampedWithModelAndDimensions()
    {
        var memory = CreateTestMemory("Note", "content", userId: User);
        memory.SetEmbedding(new float[384], "test-model-384d/text-v2");

        Assert.Equal(384, memory.EmbeddingDim);
        Assert.Equal("test-model-384d/text-v2", memory.EmbeddingModel);

        Assert.True(memory.HasComparableEmbedding("test-model-384d/text-v2", 384));
        Assert.False(memory.HasComparableEmbedding("other-model-384d/text-v2", 384));
        Assert.False(memory.HasComparableEmbedding("test-model-384d/text-v2", 768));
    }

    [Fact]
    public async Task VectorsFromAnotherModel_AreSkippedAndCounted()
    {
        Assert.SkipUnless(EmbeddingService?.IsAvailable == true, "Embedding model unavailable");

        var stamp = MemoryTextIndexer.BuildEmbeddingStamp(EmbeddingService!.ModelId);

        // One memory embedded correctly...
        var good = CreateTestMemory("Good", "The user's favourite meal is tonkotsu ramen", userId: User);
        good.SetEmbedding(await EmbeddingService.GetEmbeddingAsync(
            MemoryTextIndexer.BuildEmbeddingText(good), Ct), stamp);
        await Repository.SaveAsync(good, Ct);

        // ...and one carrying a vector from a different model, at a different dimension.
        var stale = CreateTestMemory("Stale", "The user's favourite meal is tonkotsu ramen", userId: User);
        stale.SetEmbedding(new float[768], "some-old-model-768d/text-v1");
        await Repository.SaveAsync(stale, Ct);

        var result = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "what does he like to eat", Scope = MemoryScope.AllFor(User), TopN = 10, Reinforce = false,
        }, Ct);

        // The mismatch is surfaced rather than absorbed as a middling similarity.
        Assert.Equal(1, result.IncomparableEmbeddings);

        var scored = result.Results.FirstOrDefault(r => r.Memory.Id == stale.Id);
        Assert.True(scored is null || scored.SemanticScore is null,
            "a vector from another model must never contribute a semantic score");
    }

    [Fact]
    public async Task Reindex_RebuildsVectorsWhoseStampIsStale()
    {
        Assert.SkipUnless(EmbeddingService?.IsAvailable == true, "Embedding model unavailable");

        var stale = CreateTestMemory("Stale", "needs re-embedding", userId: User);
        stale.SetEmbedding(new float[768], "some-old-model-768d/text-v1");
        await Repository.SaveAsync(stale, Ct);

        var result = await Maintenance.ReindexAsync(force: false, Ct);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.StaleEmbeddingsReplaced >= 1);

        var refreshed = await Repository.GetAsync(stale.Id, MemoryScope.AllFor(User), Ct);
        Assert.Equal(EmbeddingService!.Dimensions, refreshed!.EmbeddingDim);
        Assert.True(refreshed.HasComparableEmbedding(
            MemoryTextIndexer.BuildEmbeddingStamp(EmbeddingService.ModelId), EmbeddingService.Dimensions));
    }

    /// <summary>
    /// Reindex used to recompute the derived text fields and then hand the entity to a save path
    /// that recomputed them differently, discarding the work on the way to disk.
    /// </summary>
    [Fact]
    public async Task Reindex_LeavesSearchFieldsConsistentWithTheSavePath()
    {
        var memory = CreateTestMemory(
            "Trip", "Notes from the trip", "we found an izakaya in nakameguro", userId: User);
        await Repository.SaveAsync(memory, Ct);

        await Maintenance.ReindexAsync(force: false, Ct);

        var reloaded = await Repository.GetAsync(memory.Id, MemoryScope.AllFor(User), Ct);

        Assert.Contains("nakameguro", reloaded!.SearchText);
        Assert.NotEmpty(reloaded.Trigrams);

        // Still findable by a word that only appears in the content.
        var found = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "nakameguro", Scope = MemoryScope.AllFor(User), TopN = 5, Reinforce = false,
        }, Ct);

        Assert.Contains(found.Results, r => r.Memory.Id == memory.Id);
    }

    [Fact]
    public async Task StoreSucceedsEvenWhenEmbeddingIsUnavailable()
    {
        // Durability first: a memory must be stored even if it cannot be vectorised.
        var memory = CreateTestMemory("Durable", "must survive an embedding failure", userId: User);
        var result = await ConflictStorage.StoreAsync(memory, MemoryScope.AllFor(User), "test", Ct);

        Assert.NotNull(await Repository.GetAsync(result.Memory.Id, MemoryScope.AllFor(User), Ct));
    }

    [Fact]
    public void EmbeddingTextIsIdenticalAcrossStoreAndReindexPaths()
    {
        var memory = CreateTestMemory(
            "Employer", "The user works at Acme", "Since 2019", userId: User,
            subject: SubjectRefs.Companion("aria"), predicate: "employer");

        var first  = MemoryTextIndexer.BuildEmbeddingText(memory);
        var second = MemoryTextIndexer.BuildEmbeddingText(memory);

        Assert.Equal(first, second);

        // The subject is part of the text, so "the user's employer" and "Aria's employer" separate
        // in vector space instead of colliding at a similarity high enough to trip conflict checks.
        Assert.Contains("companion:aria", first);
        Assert.Contains("employer", first);
    }
}
