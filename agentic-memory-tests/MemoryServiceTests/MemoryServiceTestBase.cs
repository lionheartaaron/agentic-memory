using AgenticMemory.Brain.Conflict;
using AgenticMemory.Brain.Embeddings;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Maintenance;
using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Search;
using AgenticMemory.Brain.Slots;
using AgenticMemory.Brain.Storage;
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
    protected IMemoryAdminStore AdminStore => Fixture.AdminStore;
    protected IMemoryEventLog EventLog => Fixture.EventLog;
    protected IEmbeddingService? EmbeddingService => Fixture.EmbeddingService;
    protected ISearchService SearchService => Fixture.SearchService;
    protected IConflictAwareStorage ConflictStorage => Fixture.ConflictStorage;
    protected IMaintenanceService Maintenance => Fixture.Maintenance;
    protected IMemoryBackupService Backups => Fixture.Backups;

    /// <summary>The default single-user scope most tests operate in.</summary>
    protected static MemoryScope Scope => MemoryScope.Default;

    protected CancellationToken Ct => TestContext.Current.CancellationToken;

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
        double importance = 0.5,
        string? userId = null,
        string? companionId = null,
        MemoryVisibility visibility = MemoryVisibility.Global,
        string? subject = null,
        string? predicate = null,
        string? value = null,
        MemoryType type = MemoryType.Semantic,
        MemorySource source = MemorySource.UserStated)
    {
        return new MemoryNodeEntity
        {
            Id = Guid.NewGuid(),
            UserId = MemoryScope.NormalizeUser(userId),
            Title = title,
            Summary = summary,
            Content = content ?? $"Content for {title}",
            Tags = tags ?? [],
            Importance = importance,
            Visibility = visibility,
            CompanionIds = companionId is null ? [] : [MemoryScope.NormalizeId(companionId)!],
            SubjectRef = SubjectRefs.Normalize(subject),
            Predicate = SlotRegistry.Normalize(predicate),
            ValueKey = value is null ? null : MemoryTextIndexer.BuildValueKey(value),
            Type = type,
            Source = source,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            BaseStrength = 1.0,
            AccessCount = 0,
            State = MemoryState.Active,
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
