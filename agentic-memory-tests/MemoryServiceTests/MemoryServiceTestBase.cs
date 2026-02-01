using AgenticMemory.Brain.Conflict;
using AgenticMemory.Brain.Embeddings;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Search;
using AgenticMemoryTests.Shared;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// Base class for memory service tests. Provides common infrastructure for
/// testing repository, search, embedding, and conflict-aware storage services directly.
/// </summary>
public abstract class MemoryServiceTestBase : IAsyncLifetime
{
    protected TestFixture Fixture { get; private set; } = null!;
    protected IMemoryRepository Repository => Fixture.Repository;
    protected IEmbeddingService? EmbeddingService => Fixture.EmbeddingService;
    protected ISearchService SearchService => Fixture.SearchService;
    protected IConflictAwareStorage ConflictStorage => Fixture.ConflictStorage;

    public async ValueTask InitializeAsync()
    {
        Fixture = new TestFixture();
        await Fixture.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Fixture.DisposeAsync();
    }

    #region Helper Methods

    protected MemoryNodeEntity CreateTestMemory(
        string title,
        string summary,
        string? content = null,
        List<string>? tags = null,
        double importance = 0.5)
    {
        return new MemoryNodeEntity
        {
            Id = Guid.NewGuid(),
            Title = title,
            Summary = summary,
            Content = content ?? $"Content for {title}",
            Tags = tags ?? [],
            Importance = importance,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            BaseStrength = 1.0,
            AccessCount = 0,
            IsArchived = false
        };
    }

    protected static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        float dotProduct = 0;
        float normA = 0;
        float normB = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0) return 0;

        return dotProduct / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    #endregion
}
