using AgenticMemory.Brain.Models;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// Tests for IMemoryRepository CRUD operations and data integrity.
/// </summary>
public class RepositoryTests : MemoryServiceTestBase
{
    #region Save/Create Tests

    [Fact]
    public async Task Repository_Save_CreatesNewMemory()
    {
        var memory = CreateTestMemory("Test Memory", "Test summary");

        await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("Test Memory", retrieved.Title);
        Assert.Equal("Test summary", retrieved.Summary);
    }

    [Fact]
    public async Task Repository_Save_UpdatesExistingMemory()
    {
        var memory = CreateTestMemory("Original Title", "Original summary");
        await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        memory.Title = "Updated Title";
        memory.Summary = "Updated summary";
        await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("Updated Title", retrieved.Title);
        Assert.Equal("Updated summary", retrieved.Summary);
    }

    #endregion

    #region Get Tests

    [Fact]
    public async Task Repository_Get_ReturnsNullForNonexistent()
    {
        var result = await Repository.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task Repository_GetAll_ReturnsAllMemories()
    {
        for (int i = 0; i < 5; i++)
        {
            var memory = CreateTestMemory($"Memory {i}", $"Summary {i}");
            await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);
        }

        var all = await Repository.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, all.Count);
    }

    #endregion

    #region Delete Tests

    [Fact]
    public async Task Repository_Delete_RemovesMemory()
    {
        var memory = CreateTestMemory("To Delete", "Will be deleted");
        await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var deleted = await Repository.DeleteAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.True(deleted);

        var retrieved = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task Repository_Delete_ReturnsFalseForNonexistent()
    {
        var deleted = await Repository.DeleteAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.False(deleted);
    }

    #endregion

    #region Search Tests

    [Fact]
    public async Task Repository_SearchByTags_FiltersCorrectly()
    {
        await Repository.SaveAsync(CreateTestMemory("Work 1", "Work memory", tags: ["work"]), TestContext.Current.CancellationToken);
        await Repository.SaveAsync(CreateTestMemory("Work 2", "Another work", tags: ["work", "project"]), TestContext.Current.CancellationToken);
        await Repository.SaveAsync(CreateTestMemory("Personal", "Personal note", tags: ["personal"]), TestContext.Current.CancellationToken);

        var workMemories = await Repository.SearchByTagsAsync(["work"], 10, TestContext.Current.CancellationToken);
        Assert.Equal(2, workMemories.Count);
        Assert.All(workMemories, m => Assert.Contains("work", m.Tags));
    }

    [Fact]
    public async Task Repository_SearchByText_FindsMatches()
    {
        for (int i = 0; i < 10; i++)
        {
            var memory = CreateTestMemory($"Memory {i}", $"Summary {i}", $"Content about topic {i}");
            await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);
        }

        var results = await Repository.SearchByTextAsync("Memory", 5, TestContext.Current.CancellationToken);
        Assert.True(results.Count <= 5);
    }

    #endregion

    #region Stats Tests

    [Fact]
    public async Task Repository_GetStats_ReturnsCorrectStatistics()
    {
        for (int i = 0; i < 3; i++)
        {
            await Repository.SaveAsync(CreateTestMemory($"Memory {i}", $"Summary {i}"), TestContext.Current.CancellationToken);
        }

        var stats = await Repository.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, stats.TotalNodes);
        Assert.True(stats.AverageStrength > 0);
    }

    #endregion

    #region Reinforce Tests

    [Fact]
    public async Task Repository_Reinforce_UpdatesStrength()
    {
        var memory = CreateTestMemory("Reinforce Test", "Testing reinforcement");
        await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var beforeReinforce = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(beforeReinforce);
        var initialAccessCount = beforeReinforce.AccessCount;

        await Repository.ReinforceAsync(memory.Id, TestContext.Current.CancellationToken);

        var afterReinforce = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(afterReinforce);
        Assert.True(afterReinforce.AccessCount > initialAccessCount);
    }

    #endregion

    #region Memory Entity Tests

    [Fact]
    public void MemoryNodeEntity_DefaultValues_AreCorrect()
    {
        var node = new MemoryNodeEntity();

        Assert.NotEqual(Guid.Empty, node.Id);
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

        if (EmbeddingService != null)
        {
            var embedding = await EmbeddingService.GetEmbeddingAsync(memory.Summary, TestContext.Current.CancellationToken);
            memory.SetEmbedding(embedding);
        }

        await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);

        if (EmbeddingService != null)
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
        await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        for (int i = 1; i <= 5; i++)
        {
            await Repository.ReinforceAsync(memory.Id, TestContext.Current.CancellationToken);
        }

        var final = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(final);
        Assert.Equal(5, final.AccessCount);
    }

    [Fact]
    public async Task MemoryNodeEntity_Archiving_WorksCorrectly()
    {
        var memory = CreateTestMemory("Archive Test", "Will be archived");
        memory.IsArchived = false;
        await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        memory.IsArchived = true;
        await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
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
            await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);
            return memory.Id;
        });

        var ids = await Task.WhenAll(tasks);

        Assert.Equal(20, ids.Distinct().Count());

        var stats = await Repository.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(20, stats.TotalNodes);
    }

    [Fact]
    public async Task Repository_ConcurrentReadsAndWrites_HandlesGracefully()
    {
        var ids = new List<Guid>();
        for (int i = 0; i < 10; i++)
        {
            var memory = CreateTestMemory($"Initial {i}", $"Content {i}");
            await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);
            ids.Add(memory.Id);
        }

        var readTasks = ids.Select(async id =>
        {
            for (int i = 0; i < 5; i++)
            {
                await Repository.GetAsync(id, TestContext.Current.CancellationToken);
            }
        });

        var writeTasks = Enumerable.Range(0, 10).Select(async i =>
        {
            var memory = CreateTestMemory($"New {i}", $"New content {i}");
            await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);
        });

        await Task.WhenAll(readTasks.Concat(writeTasks));

        var stats = await Repository.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(20, stats.TotalNodes);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task Repository_LargeContent_StoresCorrectly()
    {
        var largeContent = new string('X', 50000);
        var memory = CreateTestMemory("Large Memory", "Large content test", largeContent);

        await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
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

        await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Contains("<>&", retrieved.Title);
        Assert.Contains("\n", retrieved.Summary);
        Assert.Contains("????", retrieved.Content);
    }

    [Fact]
    public async Task Repository_EmptyStrings_HandlesCorrectly()
    {
        var memory = CreateTestMemory("", "", "");
        memory.Tags = [];

        await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.True(string.IsNullOrEmpty(retrieved.Title), "Title should be empty or null");
        Assert.True(string.IsNullOrEmpty(retrieved.Summary), "Summary should be empty or null");
    }

    [Fact]
    public async Task Repository_ManyTags_HandlesCorrectly()
    {
        var tags = Enumerable.Range(0, 100).Select(i => $"tag{i}").ToList();
        var memory = CreateTestMemory("Many Tags", "Testing many tags", tags: tags);

        await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        var retrieved = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal(100, retrieved.Tags.Count);
    }

    [Fact]
    public async Task Repository_ImportanceBoundaries_StoresCorrectly()
    {
        var memory1 = CreateTestMemory("High Importance", "Test", importance: 1.5);
        var memory2 = CreateTestMemory("Low Importance", "Test", importance: -0.5);

        await Repository.SaveAsync(memory1, TestContext.Current.CancellationToken);
        await Repository.SaveAsync(memory2, TestContext.Current.CancellationToken);

        var retrieved1 = await Repository.GetAsync(memory1.Id, TestContext.Current.CancellationToken);
        var retrieved2 = await Repository.GetAsync(memory2.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(retrieved1);
        Assert.NotNull(retrieved2);
    }

    [Fact]
    public async Task Repository_WeakMemories_CanBeQueried()
    {
        var strongMemory = CreateTestMemory("Strong Memory", "High strength");
        strongMemory.BaseStrength = 1.0;
        await Repository.SaveAsync(strongMemory, TestContext.Current.CancellationToken);

        var weakMemory = CreateTestMemory("Weak Memory", "Low strength");
        weakMemory.BaseStrength = 0.1;
        weakMemory.LastAccessedAt = DateTime.UtcNow.AddDays(-30);
        await Repository.SaveAsync(weakMemory, TestContext.Current.CancellationToken);

        var weakMemories = await Repository.GetWeakMemoriesAsync(0.5, 10, TestContext.Current.CancellationToken);

        Assert.NotEmpty(weakMemories);
    }

    [Fact]
    public async Task Compaction_WorksCorrectly()
    {
        for (int i = 0; i < 10; i++)
        {
            var memory = CreateTestMemory($"Temp Memory {i}", $"Temporary content {i}");
            await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);
            await Repository.DeleteAsync(memory.Id, TestContext.Current.CancellationToken);
        }

        await Repository.CompactAsync(TestContext.Current.CancellationToken);

        var stats = await Repository.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, stats.TotalNodes);
    }

    #endregion
}
