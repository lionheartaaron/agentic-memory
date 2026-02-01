namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// Tests for IEmbeddingService functionality including similarity calculations and edge cases.
/// </summary>
public class EmbeddingTests : MemoryServiceTestBase
{
    #region Availability Tests

    [Fact]
    public void EmbeddingService_IsAvailable_ReturnsExpectedState()
    {
        if (EmbeddingService != null)
        {
            Assert.True(EmbeddingService.IsAvailable);
        }
        else
        {
            Assert.Null(EmbeddingService);
        }
    }

    [Fact]
    public void EmbeddingService_Dimensions_ReturnsPositiveValue()
    {
        if (EmbeddingService == null)
        {
            return;
        }

        Assert.True(EmbeddingService.Dimensions > 0);
    }

    #endregion

    #region Embedding Generation Tests

    [Fact]
    public async Task EmbeddingService_GetEmbedding_ReturnsValidVector()
    {
        if (EmbeddingService == null)
        {
            return;
        }

        var embedding = await EmbeddingService.GetEmbeddingAsync("Test text for embedding", TestContext.Current.CancellationToken);

        Assert.NotNull(embedding);
        Assert.NotEmpty(embedding);
        Assert.All(embedding, v => Assert.False(float.IsNaN(v)));
    }

    [Fact]
    public async Task EmbeddingService_SimilarTexts_HaveHighSimilarity()
    {
        if (EmbeddingService == null)
        {
            return;
        }

        var embedding1 = await EmbeddingService.GetEmbeddingAsync("The cat sat on the mat", TestContext.Current.CancellationToken);
        var embedding2 = await EmbeddingService.GetEmbeddingAsync("A cat was sitting on a mat", TestContext.Current.CancellationToken);

        var similarity = CosineSimilarity(embedding1, embedding2);

        Assert.True(similarity > 0.7, $"Expected high similarity, got {similarity}");
    }

    [Fact]
    public async Task EmbeddingService_DifferentTexts_HaveLowerSimilarity()
    {
        if (EmbeddingService == null)
        {
            return;
        }

        var embedding1 = await EmbeddingService.GetEmbeddingAsync("Programming in Python", TestContext.Current.CancellationToken);
        var embedding2 = await EmbeddingService.GetEmbeddingAsync("Cooking Italian pasta", TestContext.Current.CancellationToken);

        var similarity = CosineSimilarity(embedding1, embedding2);

        Assert.True(similarity < 0.5, $"Expected lower similarity, got {similarity}");
    }

    #endregion

    #region Unicode Handling Tests

    [Fact]
    public async Task EmbeddingService_UnicodeText_HandlesCorrectly()
    {
        if (EmbeddingService == null)
        {
            return;
        }

        var embedding = await EmbeddingService.GetEmbeddingAsync("??????? Chinese ??", TestContext.Current.CancellationToken);

        Assert.NotNull(embedding);
        Assert.NotEmpty(embedding);
    }

    [Fact]
    public async Task EmbeddingService_SurrogatePairUnicode_HandledGracefully()
    {
        if (EmbeddingService == null)
        {
            return;
        }

        var textWithEmojis = "Testing emojis \ud83d\ude80 rocket \ud83c\udf1f star \ud83d\udc4d thumbs up";

        var embedding = await EmbeddingService.GetEmbeddingAsync(textWithEmojis, TestContext.Current.CancellationToken);

        Assert.NotNull(embedding);
        Assert.NotEmpty(embedding);
        Assert.All(embedding, v => Assert.False(float.IsNaN(v)));
    }

    [Fact]
    public async Task EmbeddingService_UnpairedSurrogate_HandledGracefully()
    {
        if (EmbeddingService == null)
        {
            return;
        }

        var textWithUnpairedSurrogates = "Testing unpaired \ud83d high surrogate alone";

        var embedding = await EmbeddingService.GetEmbeddingAsync(textWithUnpairedSurrogates, TestContext.Current.CancellationToken);

        Assert.NotNull(embedding);
        Assert.NotEmpty(embedding);
        Assert.All(embedding, v => Assert.False(float.IsNaN(v)));
    }

    #endregion

    #region Repository Surrogate Pair Tests

    [Fact]
    public async Task Repository_SurrogatePairUnicode_HandledGracefully()
    {
        var memory = CreateTestMemory(
            "Emoji Test \ud83d\ude80\ud83c\udf1f\ud83d\udc4d",
            "Testing emojis: \ud83d\ude00 smile \ud83d\udc96 heart \ud83c\udf89 party",
            "Content with emojis \ud83d\udca1 and regular text mixed together \ud83c\udf08");

        await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);

        Assert.Contains("\ud83d\ude80", retrieved.Title);
        Assert.Contains("smile", retrieved.Summary);
        Assert.Contains("regular text", retrieved.Content);
    }

    [Fact]
    public async Task Repository_UnpairedSurrogate_HandledGracefully()
    {
        var memory = CreateTestMemory(
            "Unpaired Surrogate Test \ud83d alone",
            "Testing unpaired: \ud83d high and \ude00 low orphans",
            "Content with unpaired \ud83d surrogate that should not crash");

        await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.NotEqual(Guid.Empty, retrieved.Id);
        Assert.Contains("Unpaired Surrogate Test", retrieved.Title);
        Assert.Contains("alone", retrieved.Title);
        Assert.Contains("Testing unpaired:", retrieved.Summary);
        Assert.Contains("should not crash", retrieved.Content);
    }

    #endregion
}
