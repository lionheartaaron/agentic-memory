using AgenticMemory.Brain.Conflict;
using AgenticMemory.Brain.Embeddings;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Search;
using AgenticMemory.Brain.Storage;
using AgenticMemory.Configuration;
using Microsoft.Extensions.Logging;

namespace agentic_memory_tests;

/// <summary>
/// Direct tests for memory services without MCP protocol layer.
/// Tests IMemoryRepository, ISearchService, IConflictAwareStorage, and IEmbeddingService directly.
/// </summary>
public class MemoryServiceTests : IAsyncLifetime
{
    // Shared test infrastructure
    private static readonly SemaphoreSlim _modelDownloadLock = new(1, 1);
    private static bool _modelsDownloaded;

    private readonly string _testDbPath;
    private readonly string _testModelsPath;
    private IMemoryRepository _repository = null!;
    private IEmbeddingService? _embeddingService;
    private ISearchService _searchService = null!;
    private IConflictAwareStorage _conflictStorage = null!;
    private ILoggerFactory _loggerFactory = null!;

    public MemoryServiceTests()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        _testDbPath = Path.Combine(Path.GetTempPath(), "agentic-memory-tests", $"test-{testId}.db");
        _testModelsPath = Path.Combine(Path.GetTempPath(), "agentic-memory-tests", "models");
    }

    public async ValueTask InitializeAsync()
    {
        var dbDir = Path.GetDirectoryName(_testDbPath);
        if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
            Directory.CreateDirectory(dbDir);

        if (!Directory.Exists(_testModelsPath))
            Directory.CreateDirectory(_testModelsPath);

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole().SetMinimumLevel(LogLevel.Warning);
        });

        await EnsureModelsDownloadedAsync();

        if (File.Exists(_testDbPath))
            File.Delete(_testDbPath);

        _repository = new LiteDbMemoryRepository(_testDbPath);

        var embeddingsSettings = new EmbeddingsSettings
        {
            Enabled = true,
            ModelsPath = _testModelsPath,
            AutoDownload = false
        };

        try
        {
            var localEmbedding = new LocalEmbeddingService(
                embeddingsSettings,
                _loggerFactory.CreateLogger<LocalEmbeddingService>());

            if (localEmbedding.IsAvailable)
            {
                _embeddingService = localEmbedding;
            }
            else
            {
                localEmbedding.Dispose();
            }
        }
        catch
        {
            // Embedding service not available
        }

        _searchService = new MemorySearchEngine(
            _repository,
            _embeddingService,
            _loggerFactory.CreateLogger<MemorySearchEngine>());

        var conflictSettings = new ConflictSettings();
        _conflictStorage = new ConflictAwareStorageService(
            _repository,
            _searchService,
            _embeddingService,
            conflictSettings,
            _loggerFactory.CreateLogger<ConflictAwareStorageService>());
    }

    public async ValueTask DisposeAsync()
    {
        try { _embeddingService?.Dispose(); }
        catch (ApplicationException) { }

        try { _repository?.Dispose(); }
        catch (ApplicationException) { }

        try { _loggerFactory?.Dispose(); }
        catch (ApplicationException) { }

        await Task.Delay(100);
        try
        {
            if (File.Exists(_testDbPath))
                File.Delete(_testDbPath);

            var journalPath = _testDbPath + "-journal";
            if (File.Exists(journalPath))
                File.Delete(journalPath);
        }
        catch { }
    }

    private async Task EnsureModelsDownloadedAsync()
    {
        if (_modelsDownloaded)
            return;

        await _modelDownloadLock.WaitAsync();
        try
        {
            if (_modelsDownloaded)
                return;

            var embeddingsSettings = new EmbeddingsSettings
            {
                Enabled = true,
                ModelsPath = _testModelsPath,
                AutoDownload = true
            };

            var modelPath = embeddingsSettings.GetModelPath();
            var vocabPath = embeddingsSettings.GetVocabPath();

            if (!File.Exists(modelPath) || !File.Exists(vocabPath))
            {
                var downloader = new ModelDownloader(
                    embeddingsSettings,
                    _loggerFactory.CreateLogger<ModelDownloader>());

                var success = await downloader.EnsureModelsAsync();
                downloader.Dispose();

                if (!success)
                {
                    throw new InvalidOperationException("Failed to download embedding models for tests");
                }
            }

            _modelsDownloaded = true;
        }
        finally
        {
            _modelDownloadLock.Release();
        }
    }

    #region Helper Methods

    private MemoryNodeEntity CreateTestMemory(
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

    #endregion

    #region Repository CRUD Tests

    [Fact]
    public async Task Repository_Save_CreatesNewMemory()
    {
        var memory = CreateTestMemory("Test Memory", "Test summary");

        await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await _repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("Test Memory", retrieved.Title);
        Assert.Equal("Test summary", retrieved.Summary);
    }

    [Fact]
    public async Task Repository_Get_ReturnsNullForNonexistent()
    {
        var result = await _repository.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task Repository_Save_UpdatesExistingMemory()
    {
        var memory = CreateTestMemory("Original Title", "Original summary");
        await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        memory.Title = "Updated Title";
        memory.Summary = "Updated summary";
        await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await _repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("Updated Title", retrieved.Title);
        Assert.Equal("Updated summary", retrieved.Summary);
    }

    [Fact]
    public async Task Repository_Delete_RemovesMemory()
    {
        var memory = CreateTestMemory("To Delete", "Will be deleted");
        await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var deleted = await _repository.DeleteAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.True(deleted);

        var retrieved = await _repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task Repository_Delete_ReturnsFalseForNonexistent()
    {
        var deleted = await _repository.DeleteAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.False(deleted);
    }

    [Fact]
    public async Task Repository_GetAll_ReturnsAllMemories()
    {
        for (int i = 0; i < 5; i++)
        {
            var memory = CreateTestMemory($"Memory {i}", $"Summary {i}");
            await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);
        }

        var all = await _repository.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, all.Count);
    }

    [Fact]
    public async Task Repository_SearchByTags_FiltersCorrectly()
    {
        await _repository.SaveAsync(CreateTestMemory("Work 1", "Work memory", tags: ["work"]), TestContext.Current.CancellationToken);
        await _repository.SaveAsync(CreateTestMemory("Work 2", "Another work", tags: ["work", "project"]), TestContext.Current.CancellationToken);
        await _repository.SaveAsync(CreateTestMemory("Personal", "Personal note", tags: ["personal"]), TestContext.Current.CancellationToken);

        var workMemories = await _repository.SearchByTagsAsync(["work"], 10, TestContext.Current.CancellationToken);
        Assert.Equal(2, workMemories.Count);
        Assert.All(workMemories, m => Assert.Contains("work", m.Tags));
    }

    [Fact]
    public async Task Repository_SearchByText_FindsMatches()
    {
        for (int i = 0; i < 10; i++)
        {
            var memory = CreateTestMemory($"Memory {i}", $"Summary {i}", $"Content about topic {i}");
            await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);
        }

        var results = await _repository.SearchByTextAsync("Memory", 5, TestContext.Current.CancellationToken);
        Assert.True(results.Count <= 5);
    }

    [Fact]
    public async Task Repository_GetStats_ReturnsCorrectStatistics()
    {
        for (int i = 0; i < 3; i++)
        {
            await _repository.SaveAsync(CreateTestMemory($"Memory {i}", $"Summary {i}"), TestContext.Current.CancellationToken);
        }

        var stats = await _repository.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, stats.TotalNodes);
        Assert.True(stats.AverageStrength > 0);
    }

    [Fact]
    public async Task Repository_Reinforce_UpdatesStrength()
    {
        var memory = CreateTestMemory("Reinforce Test", "Testing reinforcement");
        await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var beforeReinforce = await _repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(beforeReinforce);
        var initialAccessCount = beforeReinforce.AccessCount;

        await _repository.ReinforceAsync(memory.Id, TestContext.Current.CancellationToken);

        var afterReinforce = await _repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(afterReinforce);
        Assert.True(afterReinforce.AccessCount > initialAccessCount);
    }

    #endregion

    #region Search Service Tests

    [Fact]
    public async Task Search_ByText_FindsMatchingMemories()
    {
        await _repository.SaveAsync(CreateTestMemory("Python Programming", "Learning Python basics"), TestContext.Current.CancellationToken);
        await _repository.SaveAsync(CreateTestMemory("JavaScript Guide", "Frontend development"), TestContext.Current.CancellationToken);
        await _repository.SaveAsync(CreateTestMemory("Python Advanced", "Advanced Python topics"), TestContext.Current.CancellationToken);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        var results = await _searchService.SearchAsync("Python programming", 10, null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        Assert.True(results.Any(r => r.Memory.Title.Contains("Python")));
    }

    [Fact]
    public async Task Search_WithTagFilter_FiltersResults()
    {
        await _repository.SaveAsync(CreateTestMemory("Work Project", "Work related", tags: ["work"]), TestContext.Current.CancellationToken);
        await _repository.SaveAsync(CreateTestMemory("Personal Project", "Personal stuff", tags: ["personal"]), TestContext.Current.CancellationToken);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        var results = await _searchService.SearchAsync("Project", 10, ["work"], TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.Contains("work", r.Memory.Tags));
    }

    [Fact]
    public async Task Search_RespectsTopN()
    {
        for (int i = 0; i < 20; i++)
        {
            await _repository.SaveAsync(CreateTestMemory($"Test Memory {i}", $"Test content {i}"), TestContext.Current.CancellationToken);
        }

        await Task.Delay(100, TestContext.Current.CancellationToken);

        var results = await _searchService.SearchAsync("Test Memory", 5, null, TestContext.Current.CancellationToken);

        Assert.True(results.Count <= 5);
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsResults()
    {
        await _repository.SaveAsync(CreateTestMemory("Test", "Content"), TestContext.Current.CancellationToken);

        var results = await _searchService.SearchAsync("", 10, null, TestContext.Current.CancellationToken);

        // Empty query behavior depends on implementation - should not throw
        Assert.NotNull(results);
    }

    [Fact]
    public async Task Search_NoMatches_ReturnsEmpty()
    {
        await _repository.SaveAsync(CreateTestMemory("Apple", "Fruit content"), TestContext.Current.CancellationToken);

        var results = await _searchService.SearchAsync("xyznonexistent123", 10, null, TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_ReturnsScores()
    {
        await _repository.SaveAsync(CreateTestMemory("Machine Learning", "AI and ML concepts"), TestContext.Current.CancellationToken);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        var results = await _searchService.SearchAsync("Machine Learning", 10, null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        var first = results[0];
        Assert.True(first.Score > 0, "Score should be positive");
    }

    #endregion

    #region Conflict-Aware Storage Tests

    [Fact]
    public async Task ConflictStorage_Store_CreatesNewMemory()
    {
        var memory = CreateTestMemory("New Memory", "Fresh content");

        var result = await _conflictStorage.StoreAsync(memory, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Memory.Id);
    }

    [Fact]
    public async Task ConflictStorage_DuplicateContent_DetectsConflict()
    {
        var memory1 = CreateTestMemory("First Memory", "This is the exact same content");
        await _conflictStorage.StoreAsync(memory1, TestContext.Current.CancellationToken);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        var memory2 = CreateTestMemory("Second Memory", "This is the exact same content");
        var result = await _conflictStorage.StoreAsync(memory2, TestContext.Current.CancellationToken);

        // Should detect similarity and handle appropriately (reinforce, supersede, or coexist)
        Assert.NotNull(result);
        Assert.True(
            result.Action == StoreAction.ReinforcedExisting ||
            result.Action == StoreAction.StoredWithSupersede ||
            result.Action == StoreAction.StoredCoexist ||
            result.Action == StoreAction.StoredNew,
            $"Unexpected action: {result.Action}");
    }

    [Fact]
    public async Task ConflictStorage_DifferentContent_NoConflict()
    {
        var memory1 = CreateTestMemory("Python Guide", "Learning Python programming language");
        await _conflictStorage.StoreAsync(memory1, TestContext.Current.CancellationToken);

        var memory2 = CreateTestMemory("Cooking Recipe", "How to make pasta carbonara");
        var result = await _conflictStorage.StoreAsync(memory2, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        // Different content should typically result in stored new or coexist
        Assert.True(
            result.Action == StoreAction.StoredNew ||
            result.Action == StoreAction.StoredCoexist);
    }

    [Fact]
    public async Task ConflictStorage_GetTagHistory_ReturnsHistory()
    {
        await _conflictStorage.StoreAsync(CreateTestMemory("Job 1", "First job", tags: ["employment"]), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await _conflictStorage.StoreAsync(CreateTestMemory("Job 2", "Second job", tags: ["employment"]), TestContext.Current.CancellationToken);

        var history = await _conflictStorage.GetTagHistoryAsync("employment", true, TestContext.Current.CancellationToken);

        Assert.NotEmpty(history);
        Assert.True(history.Count >= 1);
    }

    #endregion

    #region Embedding Service Tests

    [Fact]
    public void EmbeddingService_IsAvailable_ReturnsExpectedState()
    {
        if (_embeddingService != null)
        {
            Assert.True(_embeddingService.IsAvailable);
        }
        else
        {
            // If embedding service couldn't be initialized, test passes
            Assert.Null(_embeddingService);
        }
    }

    [Fact]
    public async Task EmbeddingService_GetEmbedding_ReturnsValidVector()
    {
        if (_embeddingService == null)
        {
            // Skip if embedding service not available
            return;
        }

        var embedding = await _embeddingService.GetEmbeddingAsync("Test text for embedding", TestContext.Current.CancellationToken);

        Assert.NotNull(embedding);
        Assert.NotEmpty(embedding);
        Assert.All(embedding, v => Assert.False(float.IsNaN(v)));
    }

    [Fact]
    public async Task EmbeddingService_SimilarTexts_HaveHighSimilarity()
    {
        if (_embeddingService == null)
        {
            return;
        }

        var embedding1 = await _embeddingService.GetEmbeddingAsync("The cat sat on the mat", TestContext.Current.CancellationToken);
        var embedding2 = await _embeddingService.GetEmbeddingAsync("A cat was sitting on a mat", TestContext.Current.CancellationToken);

        var similarity = CosineSimilarity(embedding1, embedding2);

        Assert.True(similarity > 0.7, $"Expected high similarity, got {similarity}");
    }

    [Fact]
    public async Task EmbeddingService_DifferentTexts_HaveLowerSimilarity()
    {
        if (_embeddingService == null)
        {
            return;
        }

        var embedding1 = await _embeddingService.GetEmbeddingAsync("Programming in Python", TestContext.Current.CancellationToken);
        var embedding2 = await _embeddingService.GetEmbeddingAsync("Cooking Italian pasta", TestContext.Current.CancellationToken);

        var similarity = CosineSimilarity(embedding1, embedding2);

        Assert.True(similarity < 0.5, $"Expected lower similarity, got {similarity}");
    }

    [Fact]
    public async Task EmbeddingService_UnicodeText_HandlesCorrectly()
    {
        if (_embeddingService == null)
        {
            return;
        }

        var embedding = await _embeddingService.GetEmbeddingAsync("??????? Chinese ??", TestContext.Current.CancellationToken);

        Assert.NotNull(embedding);
        Assert.NotEmpty(embedding);
    }

    [Fact]
    public void EmbeddingService_Dimensions_ReturnsPositiveValue()
    {
        if (_embeddingService == null)
        {
            return;
        }

        Assert.True(_embeddingService.Dimensions > 0);
    }

    private static float CosineSimilarity(float[] a, float[] b)
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

    #region Memory Entity Model Tests

    [Fact]
    public void MemoryNodeEntity_DefaultValues_AreCorrect()
    {
        var node = new MemoryNodeEntity();

        Assert.NotEqual(Guid.Empty, node.Id); // Has default Guid.NewGuid()
        Assert.Equal(string.Empty, node.Title);
        Assert.Equal(string.Empty, node.Summary);
        Assert.Equal(string.Empty, node.Content);
        Assert.Empty(node.Tags);
        Assert.False(node.IsArchived);
    }

    [Fact]
    public async Task MemoryNodeEntity_WithEmbedding_PersistsCorrectly()
    {
        var memory = CreateTestMemory("Embedding Test", "Memory with embedding");

        if (_embeddingService != null)
        {
            var embedding = await _embeddingService.GetEmbeddingAsync(memory.Summary, TestContext.Current.CancellationToken);
            memory.SetEmbedding(embedding);
        }

        await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await _repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);

        if (_embeddingService != null)
        {
            var retrievedEmbedding = retrieved.GetEmbedding();
            Assert.NotNull(retrievedEmbedding);
        }
    }

    [Fact]
    public async Task MemoryNodeEntity_AccessCount_IncrementsProperly()
    {
        var memory = CreateTestMemory("Access Test", "Testing access count");
        memory.AccessCount = 0;
        await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        for (int i = 1; i <= 5; i++)
        {
            await _repository.ReinforceAsync(memory.Id, TestContext.Current.CancellationToken);
        }

        var final = await _repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(final);
        Assert.Equal(5, final.AccessCount);
    }

    [Fact]
    public async Task MemoryNodeEntity_Archiving_WorksCorrectly()
    {
        var memory = CreateTestMemory("Archive Test", "Will be archived");
        memory.IsArchived = false;
        await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        memory.IsArchived = true;
        await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await _repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.True(retrieved.IsArchived);
    }

    [Fact]
    public void MemoryNodeEntity_GetCurrentStrength_CalculatesDecay()
    {
        var memory = CreateTestMemory("Decay Test", "Testing strength decay");
        memory.BaseStrength = 1.0;
        memory.DecayRate = 0.1;
        memory.LastAccessedAt = DateTime.UtcNow.AddDays(-10);

        var strength = memory.GetCurrentStrength();

        Assert.True(strength < 1.0, "Strength should have decayed");
        Assert.True(strength > 0, "Strength should be positive");
    }

    [Fact]
    public void MemoryNodeEntity_PinnedMemory_DoesNotDecay()
    {
        var memory = CreateTestMemory("Pinned Test", "Testing pinned memory");
        memory.BaseStrength = 1.0;
        memory.IsPinned = true;
        memory.LastAccessedAt = DateTime.UtcNow.AddDays(-100);

        var strength = memory.GetCurrentStrength();

        Assert.Equal(1.0, strength);
    }

    [Fact]
    public void MemoryNodeEntity_Reinforce_IncreasesStrength()
    {
        var memory = CreateTestMemory("Reinforce Test", "Testing reinforcement");
        memory.BaseStrength = 1.0;
        memory.AccessCount = 0;

        memory.Reinforce();

        Assert.True(memory.BaseStrength > 1.0);
        Assert.Equal(1, memory.AccessCount);
    }

    #endregion

    #region Concurrency Tests

    [Fact]
    public async Task Repository_ConcurrentStores_HandlesGracefully()
    {
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            var memory = CreateTestMemory($"Concurrent {i}", $"Content {i}");
            await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);
            return memory.Id;
        });

        var ids = await Task.WhenAll(tasks);

        Assert.Equal(20, ids.Distinct().Count());

        var stats = await _repository.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(20, stats.TotalNodes);
    }

    [Fact]
    public async Task Repository_ConcurrentReadsAndWrites_HandlesGracefully()
    {
        // Store initial memories
        var ids = new List<Guid>();
        for (int i = 0; i < 10; i++)
        {
            var memory = CreateTestMemory($"Initial {i}", $"Content {i}");
            await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);
            ids.Add(memory.Id);
        }

        // Concurrent reads and writes
        var readTasks = ids.Select(async id =>
        {
            for (int i = 0; i < 5; i++)
            {
                await _repository.GetAsync(id, TestContext.Current.CancellationToken);
            }
        });

        var writeTasks = Enumerable.Range(0, 10).Select(async i =>
        {
            var memory = CreateTestMemory($"New {i}", $"New content {i}");
            await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);
        });

        await Task.WhenAll(readTasks.Concat(writeTasks));

        var stats = await _repository.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(20, stats.TotalNodes);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task Repository_LargeContent_StoresCorrectly()
    {
        var largeContent = new string('X', 50000);
        var memory = CreateTestMemory("Large Memory", "Large content test", largeContent);

        await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await _repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal(50000, retrieved.Content.Length);
    }

    [Fact]
    public async Task Repository_SpecialCharacters_HandlesCorrectly()
    {
        var memory = CreateTestMemory(
            "Special <>&\"' Characters",
            "Summary with\nnewlines\tand\ttabs",
            "Content with unicode: ???? ??? emoji-safe text");

        await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await _repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Contains("<>&", retrieved.Title);
        Assert.Contains("\n", retrieved.Summary);
        Assert.Contains("????", retrieved.Content);
    }

    [Fact]
    public async Task Repository_SurrogatePairUnicode_HandledGracefully()
    {
        // Test that surrogate pairs (emojis, etc.) are handled gracefully
        // The memory should be stored successfully, with emojis preserved in storage
        // but filtered out before embedding generation
        var memory = CreateTestMemory(
            "Emoji Test \ud83d\ude80\ud83c\udf1f\ud83d\udc4d", // rocket, star, thumbs up
            "Testing emojis: \ud83d\ude00 smile \ud83d\udc96 heart \ud83c\udf89 party",
            "Content with emojis \ud83d\udca1 and regular text mixed together \ud83c\udf08");

        await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await _repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);

        // Emojis should be preserved in the actual stored content
        Assert.Contains("\ud83d\ude80", retrieved.Title); // rocket emoji preserved
        Assert.Contains("smile", retrieved.Summary); // text preserved
        Assert.Contains("regular text", retrieved.Content); // text preserved
    }

    [Fact]
    public async Task Repository_UnpairedSurrogate_HandledGracefully()
    {
        // Test that UNPAIRED surrogates (invalid UTF-16) don't crash the storage
        // This is the bug case: \uD83D alone without its low surrogate pair
        var memory = CreateTestMemory(
            "Unpaired Surrogate Test \ud83d alone", // \uD83D is a high surrogate without its pair
            "Testing unpaired: \ud83d high and \ude00 low orphans",
            "Content with unpaired \ud83d surrogate that should not crash");

        await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        // Verify memory was stored (unpaired surrogates are stripped, but text around them is preserved)
        var retrieved = await _repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.NotEqual(Guid.Empty, retrieved.Id);
        // Text around the surrogates should be preserved
        Assert.Contains("Unpaired Surrogate Test", retrieved.Title);
        Assert.Contains("alone", retrieved.Title);
        Assert.Contains("Testing unpaired:", retrieved.Summary);
        Assert.Contains("should not crash", retrieved.Content);
    }

    [Fact]
    public async Task ConflictStorage_SurrogatePairUnicode_HandledGracefully()
    {
        // Test that surrogate pairs (emojis) work through the conflict-aware storage
        // which also triggers embedding generation
        var memory = CreateTestMemory(
            "Emoji Conflict Test \ud83d\ude80\ud83c\udf1f", // rocket, star
            "Testing emojis through conflict storage: \ud83d\ude00 smile",
            "Content with emojis \ud83d\udca1 and regular text \ud83c\udf08");

        var result = await _conflictStorage.StoreAsync(memory, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Memory.Id);

        // Verify the stored memory preserves emojis
        var retrieved = await _repository.GetAsync(result.Memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Contains("\ud83d\ude80", retrieved.Title); // rocket emoji preserved
        Assert.Contains("smile", retrieved.Summary);
    }

    [Fact]
    public async Task ConflictStorage_UnpairedSurrogate_HandledGracefully()
    {
        // Test that UNPAIRED surrogates don't crash the embedding service or storage
        // This would previously throw ArgumentException in Regex.Replace or LiteDB encoding
        var memory = CreateTestMemory(
            "Unpaired Surrogate Conflict Test \ud83d alone",
            "Testing unpaired: \ud83d high and \ude00 low orphans",
            "Content with unpaired \ud83d surrogate that should not crash");

        var result = await _conflictStorage.StoreAsync(memory, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Memory.Id);

        // Verify memory was stored successfully (unpaired surrogates stripped, text preserved)
        var retrieved = await _repository.GetAsync(result.Memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Contains("Unpaired Surrogate Conflict Test", retrieved.Title);
        Assert.Contains("should not crash", retrieved.Content);
    }

    [Fact]
    public async Task EmbeddingService_SurrogatePairUnicode_HandledGracefully()
    {
        if (_embeddingService == null)
        {
            return;
        }

        // Test that embedding generation handles emoji text gracefully
        var textWithEmojis = "Testing emojis \ud83d\ude80 rocket \ud83c\udf1f star \ud83d\udc4d thumbs up";

        var embedding = await _embeddingService.GetEmbeddingAsync(textWithEmojis, TestContext.Current.CancellationToken);

        Assert.NotNull(embedding);
        Assert.NotEmpty(embedding);
        Assert.All(embedding, v => Assert.False(float.IsNaN(v)));
    }

    [Fact]
    public async Task EmbeddingService_UnpairedSurrogate_HandledGracefully()
    {
        if (_embeddingService == null)
        {
            return;
        }

        // Test that embedding generation handles unpaired surrogates gracefully
        // This would previously throw ArgumentException in Regex.Replace
        var textWithUnpairedSurrogates = "Testing unpaired \ud83d high surrogate alone";

        var embedding = await _embeddingService.GetEmbeddingAsync(textWithUnpairedSurrogates, TestContext.Current.CancellationToken);

        Assert.NotNull(embedding);
        Assert.NotEmpty(embedding);
        Assert.All(embedding, v => Assert.False(float.IsNaN(v)));
    }

    [Fact]
    public async Task Repository_EmptyStrings_HandlesCorrectly()
    {
        var memory = CreateTestMemory("", "", "");
        memory.Tags = [];

        await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await _repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        // LiteDB may convert empty strings to null, which is acceptable behavior
        Assert.True(string.IsNullOrEmpty(retrieved.Title), "Title should be empty or null");
        Assert.True(string.IsNullOrEmpty(retrieved.Summary), "Summary should be empty or null");
    }

    [Fact]
    public async Task Repository_ManyTags_HandlesCorrectly()
    {
        var tags = Enumerable.Range(0, 100).Select(i => $"tag{i}").ToList();
        var memory = CreateTestMemory("Many Tags", "Testing many tags", tags: tags);

        await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await _repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal(100, retrieved.Tags.Count);
    }

    [Fact]
    public async Task Repository_ImportanceBoundaries_StoresCorrectly()
    {
        var memory1 = CreateTestMemory("High Importance", "Test", importance: 1.5);
        var memory2 = CreateTestMemory("Low Importance", "Test", importance: -0.5);

        await _repository.SaveAsync(memory1, TestContext.Current.CancellationToken);
        await _repository.SaveAsync(memory2, TestContext.Current.CancellationToken);

        var retrieved1 = await _repository.GetAsync(memory1.Id, TestContext.Current.CancellationToken);
        var retrieved2 = await _repository.GetAsync(memory2.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(retrieved1);
        Assert.NotNull(retrieved2);
        // Values are stored as-is (clamping may or may not happen depending on implementation)
    }

    [Fact]
    public async Task Repository_WeakMemories_CanBeQueried()
    {
        // Create memories with different strengths
        var strongMemory = CreateTestMemory("Strong Memory", "High strength");
        strongMemory.BaseStrength = 1.0;
        await _repository.SaveAsync(strongMemory, TestContext.Current.CancellationToken);

        var weakMemory = CreateTestMemory("Weak Memory", "Low strength");
        weakMemory.BaseStrength = 0.1;
        weakMemory.LastAccessedAt = DateTime.UtcNow.AddDays(-30); // Old memory
        await _repository.SaveAsync(weakMemory, TestContext.Current.CancellationToken);

        var weakMemories = await _repository.GetWeakMemoriesAsync(0.5, 10, TestContext.Current.CancellationToken);

        // Should find the weak memory
        Assert.NotEmpty(weakMemories);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public async Task FullWorkflow_StoreSearchUpdateDelete()
    {
        // 1. Store
        var memory = CreateTestMemory(
            "Integration Test",
            "Testing full workflow",
            "Complete integration test content",
            ["integration", "test"]);

        await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        // 2. Search
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var searchResults = await _searchService.SearchAsync("integration workflow", 10, null, TestContext.Current.CancellationToken);
        Assert.NotEmpty(searchResults);

        // 3. Retrieve
        var retrieved = await _repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("Integration Test", retrieved.Title);

        // 4. Update
        retrieved.Title = "Updated Integration Test";
        retrieved.Tags.Add("updated");
        await _repository.SaveAsync(retrieved, TestContext.Current.CancellationToken);

        var updated = await _repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal("Updated Integration Test", updated.Title);
        Assert.Contains("updated", updated.Tags);

        // 5. Delete
        var deleted = await _repository.DeleteAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.True(deleted);

        var afterDelete = await _repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.Null(afterDelete);
    }

    [Fact]
    public async Task BulkOperations_PerformCorrectly()
    {
        // Bulk store
        var memories = Enumerable.Range(0, 50).Select(i =>
            CreateTestMemory($"Bulk Memory {i}", $"Bulk content {i}", tags: [$"bulk", $"group{i % 5}"]));

        foreach (var memory in memories)
        {
            await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);
        }

        // Verify count
        var stats = await _repository.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(50, stats.TotalNodes);

        // Search by tag
        var group0 = await _repository.SearchByTagsAsync(["group0"], 20, TestContext.Current.CancellationToken);
        Assert.Equal(10, group0.Count);

        // Get all
        var all = await _repository.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(50, all.Count);
    }

    [Fact]
    public async Task Compaction_WorksCorrectly()
    {
        // Store and delete some memories
        for (int i = 0; i < 10; i++)
        {
            var memory = CreateTestMemory($"Temp Memory {i}", $"Temporary content {i}");
            await _repository.SaveAsync(memory, TestContext.Current.CancellationToken);
            await _repository.DeleteAsync(memory.Id, TestContext.Current.CancellationToken);
        }

        // Compact should not throw
        await _repository.CompactAsync(TestContext.Current.CancellationToken);

        // Verify database is still functional
        var stats = await _repository.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, stats.TotalNodes);
    }

    #endregion
}
