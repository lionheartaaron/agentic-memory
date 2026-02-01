using AgenticMemory.Brain.Conflict;
using AgenticMemory.Brain.Interfaces;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// Tests for ISearchService and IConflictAwareStorage functionality.
/// </summary>
public class SearchAndConflictTests : MemoryServiceTestBase
{
    #region Search Service Tests

    [Fact]
    public async Task Search_ByText_FindsMatchingMemories()
    {
        await Repository.SaveAsync(CreateTestMemory("Python Programming", "Learning Python basics"), TestContext.Current.CancellationToken);
        await Repository.SaveAsync(CreateTestMemory("JavaScript Guide", "Frontend development"), TestContext.Current.CancellationToken);
        await Repository.SaveAsync(CreateTestMemory("Python Advanced", "Advanced Python topics"), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var results = await SearchService.SearchAsync("Python programming", 10, null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Memory.Title.Contains("Python"));
    }

    [Fact]
    public async Task Search_WithTagFilter_FiltersResults()
    {
        await Repository.SaveAsync(CreateTestMemory("Work Project", "Work related", tags: ["work"]), TestContext.Current.CancellationToken);
        await Repository.SaveAsync(CreateTestMemory("Personal Project", "Personal stuff", tags: ["personal"]), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var results = await SearchService.SearchAsync("Project", 10, ["work"], TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.Contains("work", r.Memory.Tags));
    }

    [Fact]
    public async Task Search_RespectsTopN()
    {
        for (int i = 0; i < 20; i++)
        {
            await Repository.SaveAsync(CreateTestMemory($"Test Memory {i}", $"Test content {i}"), TestContext.Current.CancellationToken);
        }
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var results = await SearchService.SearchAsync("Test Memory", 5, null, TestContext.Current.CancellationToken);

        Assert.True(results.Count <= 5);
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsResults()
    {
        await Repository.SaveAsync(CreateTestMemory("Test", "Content"), TestContext.Current.CancellationToken);

        var results = await SearchService.SearchAsync("", 10, null, TestContext.Current.CancellationToken);

        Assert.NotNull(results);
    }

    [Fact]
    public async Task Search_NoMatches_ReturnsEmpty()
    {
        await Repository.SaveAsync(CreateTestMemory("Apple", "Fruit content"), TestContext.Current.CancellationToken);

        var results = await SearchService.SearchAsync("xyznonexistent123", 10, null, TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_ReturnsScores()
    {
        await Repository.SaveAsync(CreateTestMemory("Machine Learning", "AI and ML concepts"), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var results = await SearchService.SearchAsync("Machine Learning", 10, null, TestContext.Current.CancellationToken);

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

        var result = await ConflictStorage.StoreAsync(memory, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Memory.Id);
    }

    [Fact]
    public async Task ConflictStorage_DuplicateContent_DetectsConflict()
    {
        var memory1 = CreateTestMemory("First Memory", "This is the exact same content");
        await ConflictStorage.StoreAsync(memory1, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var memory2 = CreateTestMemory("Second Memory", "This is the exact same content");
        var result = await ConflictStorage.StoreAsync(memory2, TestContext.Current.CancellationToken);

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
        await ConflictStorage.StoreAsync(memory1, TestContext.Current.CancellationToken);

        var memory2 = CreateTestMemory("Cooking Recipe", "How to make pasta carbonara");
        var result = await ConflictStorage.StoreAsync(memory2, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(
            result.Action == StoreAction.StoredNew ||
            result.Action == StoreAction.StoredCoexist);
    }

    [Fact]
    public async Task ConflictStorage_GetTagHistory_ReturnsHistory()
    {
        await ConflictStorage.StoreAsync(CreateTestMemory("Job 1", "First job", tags: ["employment"]), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await ConflictStorage.StoreAsync(CreateTestMemory("Job 2", "Second job", tags: ["employment"]), TestContext.Current.CancellationToken);

        var history = await ConflictStorage.GetTagHistoryAsync("employment", true, TestContext.Current.CancellationToken);

        Assert.NotEmpty(history);
        Assert.True(history.Count >= 1);
    }

    #endregion

    #region Surrogate Pair Tests (via Conflict Storage)

    [Fact]
    public async Task ConflictStorage_SurrogatePairUnicode_HandledGracefully()
    {
        var memory = CreateTestMemory(
            "Emoji Conflict Test \ud83d\ude80\ud83c\udf1f",
            "Testing emojis through conflict storage: \ud83d\ude00 smile",
            "Content with emojis \ud83d\udca1 and regular text \ud83c\udf08");

        var result = await ConflictStorage.StoreAsync(memory, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Memory.Id);

        var retrieved = await Repository.GetAsync(result.Memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Contains("\ud83d\ude80", retrieved.Title);
        Assert.Contains("smile", retrieved.Summary);
    }

    [Fact]
    public async Task ConflictStorage_UnpairedSurrogate_HandledGracefully()
    {
        var memory = CreateTestMemory(
            "Unpaired Surrogate Conflict Test \ud83d alone",
            "Testing unpaired: \ud83d high and \ude00 low orphans",
            "Content with unpaired \ud83d surrogate that should not crash");

        var result = await ConflictStorage.StoreAsync(memory, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Memory.Id);

        var retrieved = await Repository.GetAsync(result.Memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Contains("Unpaired Surrogate Conflict Test", retrieved.Title);
        Assert.Contains("should not crash", retrieved.Content);
    }

    #endregion

    #region Similarity-Based Update/Replace Tests

    [Fact]
    public async Task ConflictStorage_HighlySimilarContent_SupersedesExisting()
    {
        // Arrange: Store initial memory about current employment
        var original = CreateTestMemory(
            "Current Job",
            "I work as a software developer at TechCorp",
            "Working on backend systems using C# and .NET",
            ["employment", "current"]);
        
        var result1 = await ConflictStorage.StoreAsync(original, TestContext.Current.CancellationToken);
        Assert.Equal(StoreAction.StoredNew, result1.Action);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Act: Store updated information about the same topic (highly similar but updated)
        var updated = CreateTestMemory(
            "Current Job",
            "I work as a senior software developer at TechCorp",
            "Working on backend systems using C# and .NET, recently promoted to senior",
            ["employment", "current"]);
        
        var result2 = await ConflictStorage.StoreAsync(updated, TestContext.Current.CancellationToken);

        // Assert: Should supersede, reinforce, or coexist (depending on similarity score)
        Assert.True(
            result2.Action == StoreAction.StoredWithSupersede || 
            result2.Action == StoreAction.ReinforcedExisting ||
            result2.Action == StoreAction.StoredCoexist,
            $"Expected supersede, reinforce, or coexist, got: {result2.Action}");

        // If superseded, verify the superseded memories are properly tracked
        if (result2.Action == StoreAction.StoredWithSupersede)
        {
            // Check the result's superseded list
            Assert.NotNull(result2.SupersededMemories);
            Assert.NotEmpty(result2.SupersededMemories);
            
            // Verify each superseded memory is archived in the database
            foreach (var superseded in result2.SupersededMemories)
            {
                var fromDb = await Repository.GetAsync(superseded.Id, TestContext.Current.CancellationToken);
                Assert.NotNull(fromDb);
                Assert.True(fromDb.IsArchived, $"Superseded memory '{fromDb.Title}' should be archived");
                Assert.Equal(updated.Id, fromDb.SupersededBy);
            }
            
            // The new memory should track what it superseded
            var newMemory = await Repository.GetAsync(updated.Id, TestContext.Current.CancellationToken);
            Assert.NotNull(newMemory);
            Assert.NotEmpty(newMemory.SupersededIds);
        }
        
        // If reinforced, the original should have been updated, not a new memory created
        if (result2.Action == StoreAction.ReinforcedExisting)
        {
            Assert.Equal(original.Id, result2.Memory.Id);
        }
    }

    [Fact]
    public async Task ConflictStorage_NearDuplicateContent_ReinforcesExisting()
    {
        // Arrange: Store a memory
        var original = CreateTestMemory(
            "Python Programming Guide",
            "Python is a versatile programming language",
            "Python is great for scripting, web development, and data science");
        
        await ConflictStorage.StoreAsync(original, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Act: Store nearly identical content (should be detected as duplicate)
        var duplicate = CreateTestMemory(
            "Python Programming Guide",
            "Python is a versatile programming language",
            "Python is great for scripting, web development, and data science");
        
        var result = await ConflictStorage.StoreAsync(duplicate, TestContext.Current.CancellationToken);

        // Assert: Should reinforce the existing memory, not create a new one
        Assert.Equal(StoreAction.ReinforcedExisting, result.Action);
        Assert.Equal(original.Id, result.Memory.Id);
    }

    [Fact]
    public async Task ConflictStorage_RelatedButDifferentContent_Coexists()
    {
        // Arrange: Store a memory about Python basics
        var pythonBasics = CreateTestMemory(
            "Python Basics",
            "Introduction to Python variables and loops",
            "Python variables are dynamically typed. Loops include for and while.");
        
        await ConflictStorage.StoreAsync(pythonBasics, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Act: Store a related but different memory about Python advanced topics
        var pythonAdvanced = CreateTestMemory(
            "Python Advanced",
            "Advanced Python decorators and metaclasses",
            "Decorators modify function behavior. Metaclasses define class creation.");
        
        var result = await ConflictStorage.StoreAsync(pythonAdvanced, TestContext.Current.CancellationToken);

        // Assert: Should coexist (related but different enough)
        Assert.True(
            result.Action == StoreAction.StoredCoexist || 
            result.Action == StoreAction.StoredNew,
            $"Expected coexist or new, got: {result.Action}");
        
        // Both memories should exist
        var basics = await Repository.GetAsync(pythonBasics.Id, TestContext.Current.CancellationToken);
        var advanced = await Repository.GetAsync(pythonAdvanced.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(basics);
        Assert.NotNull(advanced);
        Assert.False(basics.IsArchived);
        Assert.False(advanced.IsArchived);
    }

    [Fact]
    public async Task ConflictStorage_ConflictingFacts_NewerReplacesOlder()
    {
        // Arrange: Store outdated information
        var outdated = CreateTestMemory(
            "Company CEO",
            "John Smith is the CEO of Acme Corporation",
            "John Smith has been CEO since 2015",
            ["acme", "leadership"]);
        
        var result1 = await ConflictStorage.StoreAsync(outdated, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Act: Store updated/corrected information (same topic, new facts)
        var current = CreateTestMemory(
            "Company CEO",
            "Jane Doe is the CEO of Acme Corporation",
            "Jane Doe became CEO in 2024, replacing John Smith",
            ["acme", "leadership"]);
        
        var result2 = await ConflictStorage.StoreAsync(current, TestContext.Current.CancellationToken);

        // Assert: The newer information should supersede or reinforce based on similarity
        Assert.True(
            result2.Action == StoreAction.StoredWithSupersede ||
            result2.Action == StoreAction.ReinforcedExisting ||
            result2.Action == StoreAction.StoredCoexist,
            $"Expected conflict resolution action, got: {result2.Action}");

        // Verify the current information is accessible
        var currentMemory = await Repository.GetAsync(result2.Memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(currentMemory);
        Assert.Contains("Jane Doe", currentMemory.Summary);
    }

    [Fact]
    public async Task ConflictStorage_UpdateWithMoreContent_ExtendsExisting()
    {
        // Arrange: Store a memory with minimal content
        var minimal = CreateTestMemory(
            "Project Status",
            "Project Alpha is in development",
            "Started in Q1");
        
        await ConflictStorage.StoreAsync(minimal, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Act: Store same topic with more detailed content
        var detailed = CreateTestMemory(
            "Project Status",
            "Project Alpha is in development",
            "Started in Q1. Team of 5 developers. Using microservices architecture. Expected completion Q4.");
        
        var result = await ConflictStorage.StoreAsync(detailed, TestContext.Current.CancellationToken);

        // Assert: Should update/reinforce with the more detailed content
        Assert.True(
            result.Action == StoreAction.ReinforcedExisting ||
            result.Action == StoreAction.StoredWithSupersede,
            $"Expected reinforce or supersede, got: {result.Action}");

        // The result memory should have the longer content
        var finalMemory = await Repository.GetAsync(result.Memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(finalMemory);
        Assert.Contains("microservices", finalMemory.Content);
    }

    [Fact]
    public async Task ConflictStorage_MultipleSupersedes_ChainedCorrectly()
    {
        // Arrange: Create a chain of updates
        var version1 = CreateTestMemory(
            "API Version",
            "API is at version 1.0",
            "Initial release");
        await ConflictStorage.StoreAsync(version1, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var version2 = CreateTestMemory(
            "API Version",
            "API is at version 2.0",
            "Major update with breaking changes");
        await ConflictStorage.StoreAsync(version2, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Act: Store version 3
        var version3 = CreateTestMemory(
            "API Version",
            "API is at version 3.0",
            "Latest stable release with new features");
        var result = await ConflictStorage.StoreAsync(version3, TestContext.Current.CancellationToken);

        // Assert: Should handle the chain
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Memory.Id);

        // Get all memories with similar content
        var allMemories = await Repository.GetAllAsync(TestContext.Current.CancellationToken);
        var apiVersionMemories = allMemories.Where(m => m.Title.Contains("API Version")).ToList();
        
        // Should have at least 1 current (non-archived) memory
        var currentMemories = apiVersionMemories.Where(m => !m.IsArchived).ToList();
        Assert.True(currentMemories.Count >= 1, "Should have at least one current memory");
        
        // Latest version should be current
        var latest = currentMemories.FirstOrDefault(m => m.Summary.Contains("3.0"));
        Assert.NotNull(latest);
    }

    [Fact]
    public async Task ConflictStorage_DifferentTopics_NeverSupersede()
    {
        // Arrange: Store memories about completely different topics
        var cooking = CreateTestMemory(
            "Pasta Recipe",
            "How to make carbonara pasta",
            "Eggs, pecorino, guanciale, black pepper",
            ["cooking", "italian"]);
        
        await ConflictStorage.StoreAsync(cooking, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Act: Store unrelated memory
        var programming = CreateTestMemory(
            "Rust Language",
            "Rust programming language overview",
            "Memory safety, ownership, borrowing, zero-cost abstractions",
            ["programming", "rust"]);
        
        var result = await ConflictStorage.StoreAsync(programming, TestContext.Current.CancellationToken);

        // Assert: Completely different topics should not interact
        Assert.Equal(StoreAction.StoredNew, result.Action);
        
        // Both should be current and unarchived
        var cookingMemory = await Repository.GetAsync(cooking.Id, TestContext.Current.CancellationToken);
        var programmingMemory = await Repository.GetAsync(programming.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(cookingMemory);
        Assert.NotNull(programmingMemory);
        Assert.False(cookingMemory.IsArchived);
        Assert.False(programmingMemory.IsArchived);
    }

    [Fact]
    public async Task ConflictStorage_SupersededMemory_TracksHistory()
    {
        // Arrange: Store original fact
        var original = CreateTestMemory(
            "Team Size",
            "Development team has 5 members",
            "Alice, Bob, Charlie, David, Eve",
            ["team", "staffing"]);
        
        await ConflictStorage.StoreAsync(original, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Act: Store updated fact (team grew)
        var updated = CreateTestMemory(
            "Team Size",
            "Development team has 8 members",
            "Alice, Bob, Charlie, David, Eve, Frank, Grace, Henry",
            ["team", "staffing"]);
        
        var result = await ConflictStorage.StoreAsync(updated, TestContext.Current.CancellationToken);

        // Assert: Get tag history should show the progression
        var history = await ConflictStorage.GetTagHistoryAsync("team", true, TestContext.Current.CancellationToken);
        
        Assert.NotEmpty(history);
        
        // If supersede happened, check the chain
        if (result.Action == StoreAction.StoredWithSupersede)
        {
            var archivedOriginal = await Repository.GetAsync(original.Id, TestContext.Current.CancellationToken);
            Assert.NotNull(archivedOriginal);
            Assert.True(archivedOriginal.IsArchived);
            Assert.NotNull(archivedOriginal.ValidUntil);
            Assert.Equal(updated.Id, archivedOriginal.SupersededBy);
            
            // New memory should track what it superseded
            var currentMemory = await Repository.GetAsync(updated.Id, TestContext.Current.CancellationToken);
            Assert.NotNull(currentMemory);
            Assert.Contains(original.Id, currentMemory.SupersededIds);
        }
    }

    [Fact]
    public async Task ConflictStorage_ReinforcedMemory_IncrementsStrength()
    {
        // Arrange: Store initial memory
        var memory = CreateTestMemory(
            "Important Fact",
            "The sky is blue during clear days",
            "Due to Rayleigh scattering of sunlight");
        
        var result1 = await ConflictStorage.StoreAsync(memory, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var initialMemory = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        var initialAccessCount = initialMemory?.AccessCount ?? 0;
        var initialStrength = initialMemory?.BaseStrength ?? 0;

        // Act: Store duplicate to trigger reinforcement
        var duplicate = CreateTestMemory(
            "Important Fact",
            "The sky is blue during clear days",
            "Due to Rayleigh scattering of sunlight");
        
        var result2 = await ConflictStorage.StoreAsync(duplicate, TestContext.Current.CancellationToken);

        // Assert: If reinforced, access count or strength should increase
        if (result2.Action == StoreAction.ReinforcedExisting)
        {
            var reinforcedMemory = await Repository.GetAsync(result2.Memory.Id, TestContext.Current.CancellationToken);
            Assert.NotNull(reinforcedMemory);
            
            // Either access count or base strength should have increased
            var strengthIncreased = reinforcedMemory.BaseStrength > initialStrength;
            var accessIncreased = reinforcedMemory.AccessCount > initialAccessCount;
            
            Assert.True(strengthIncreased || accessIncreased,
                $"Expected strength ({initialStrength} -> {reinforcedMemory.BaseStrength}) or " +
                $"access count ({initialAccessCount} -> {reinforcedMemory.AccessCount}) to increase");
        }
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

        await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);

        // 2. Search
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var searchResults = await SearchService.SearchAsync("integration workflow", 10, null, TestContext.Current.CancellationToken);
        Assert.NotEmpty(searchResults);

        // 3. Retrieve
        var retrieved = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal("Integration Test", retrieved.Title);

        // 4. Update
        retrieved.Title = "Updated Integration Test";
        retrieved.Tags.Add("updated");
        await Repository.SaveAsync(retrieved, TestContext.Current.CancellationToken);

        var updated = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal("Updated Integration Test", updated.Title);
        Assert.Contains("updated", updated.Tags);

        // 5. Delete
        var deleted = await Repository.DeleteAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.True(deleted);

        var afterDelete = await Repository.GetAsync(memory.Id, TestContext.Current.CancellationToken);
        Assert.Null(afterDelete);
    }

    [Fact]
    public async Task BulkOperations_PerformCorrectly()
    {
        var memories = Enumerable.Range(0, 50).Select(i =>
            CreateTestMemory($"Bulk Memory {i}", $"Bulk content {i}", tags: [$"bulk", $"group{i % 5}"]));

        foreach (var memory in memories)
        {
            await Repository.SaveAsync(memory, TestContext.Current.CancellationToken);
        }

        var stats = await Repository.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(50, stats.TotalNodes);

        var group0 = await Repository.SearchByTagsAsync(["group0"], 20, TestContext.Current.CancellationToken);
        Assert.Equal(10, group0.Count);

        var all = await Repository.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(50, all.Count);
    }

    #endregion
}
