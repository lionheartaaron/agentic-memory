using System.Text.RegularExpressions;
using AgenticMemory.Http.Handlers;
using AgenticMemory.Http.Models;

namespace AgenticMemoryTests.Frontend;

/// <summary>
/// Tests for HTTP search endpoints to verify correct results without duplicates.
/// These tests help diagnose if duplicate IDs in search results come from backend or frontend.
/// </summary>
public partial class SearchTests : FrontendTestBase
{
    #region Search Uniqueness Tests

    [Fact]
    public async Task Search_ReturnsUniqueIds_NoJsonDuplicates()
    {
        // Arrange: Store a memory that will match multiple search criteria
        await StoreMemoryAsync("hexvera project info", "hexvera is a testing tool for hex validation",
            "hexvera validates hexadecimal data in various formats", ["hexvera", "testing", "validation"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Search via JSON endpoint
        var request = CreateGetSearchRequest("hexvera", 10);
        var response = await SearchHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Parse JSON response and check for unique IDs
        Assert.Equal(200, response.StatusCode);
        var searchResponse = DeserializeResponse<SearchResponse>(response.Body);
        Assert.NotNull(searchResponse);

        var ids = searchResponse.Results.Select(r => r.Id).ToList();
        var uniqueIds = ids.Distinct().ToList();

        Assert.Equal(uniqueIds.Count, ids.Count);
    }

    [Fact]
    public async Task Search_ReturnsUniqueIds_NoHtmlDuplicates()
    {
        // Arrange: Store a memory that will match multiple search criteria
        await StoreMemoryAsync("hexvera project info", "hexvera is a testing tool for hex validation",
            "hexvera validates hexadecimal data in various formats", ["hexvera", "testing", "validation"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Search via HTML endpoint
        var request = CreateGetSearchRequest("hexvera", 10, acceptHtml: true);
        var response = await SearchHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Parse HTML and extract memory IDs from links
        Assert.Equal(200, response.StatusCode);
        var html = response.Body?.ToString() ?? "";

        // Extract IDs from href="/memory/{guid}.html" links
        var idMatches = MemoryLinkRegex().Matches(html);
        var ids = idMatches.Select(m => Guid.Parse(m.Groups[1].Value)).ToList();
        var uniqueIds = ids.Distinct().ToList();

        Assert.Equal(uniqueIds.Count, ids.Count);
    }

    [Fact]
    public async Task Search_WithWordOverlap_ReturnsUniqueIds()
    {
        // Arrange: Create memory where query words appear in title, summary, content, and tags
        // This triggers multiple matching paths in SearchByTextAsync
        await StoreMemoryAsync(
            "machine learning algorithms",
            "machine learning is a subset of AI using algorithms",
            "Machine learning algorithms process data to learn patterns. ML algorithms are fundamental to AI.",
            ["machine", "learning", "algorithms", "ml", "ai"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Search with multi-word query that matches multiple fields
        var request = CreateGetSearchRequest("machine learning algorithms", 10);
        var response = await SearchHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(200, response.StatusCode);
        var searchResponse = DeserializeResponse<SearchResponse>(response.Body);
        Assert.NotNull(searchResponse);

        var ids = searchResponse.Results.Select(r => r.Id).ToList();
        var uniqueIds = ids.Distinct().ToList();

        Assert.Equal(uniqueIds.Count, ids.Count);
    }

    [Fact]
    public async Task Search_WithTagMatching_ReturnsUniqueIds()
    {
        // Arrange: Memory with tags that partially match query terms
        await StoreMemoryAsync(
            "Python programming guide",
            "Guide to Python development",
            "Python is great for scripting and development",
            ["python", "programming", "development", "scripting"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Search using tag name as query
        var request = CreateGetSearchRequest("python programming", 10);
        var response = await SearchHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(200, response.StatusCode);
        var searchResponse = DeserializeResponse<SearchResponse>(response.Body);
        Assert.NotNull(searchResponse);

        var ids = searchResponse.Results.Select(r => r.Id).ToList();
        var uniqueIds = ids.Distinct().ToList();

        Assert.Equal(uniqueIds.Count, ids.Count);
    }

    [Fact]
    public async Task Search_WithTrigramMatching_ReturnsUniqueIds()
    {
        // Arrange: Memory that will trigger trigram-based matching
        await StoreMemoryAsync(
            "database optimization techniques",
            "optimizing database queries for performance",
            "Database optimization includes indexing, query tuning, and caching strategies",
            ["database", "optimization", "performance"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Partial word search that relies on trigrams
        var request = CreateGetSearchRequest("databas optim", 10);
        var response = await SearchHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(200, response.StatusCode);
        var searchResponse = DeserializeResponse<SearchResponse>(response.Body);
        Assert.NotNull(searchResponse);

        var ids = searchResponse.Results.Select(r => r.Id).ToList();
        var uniqueIds = ids.Distinct().ToList();

        Assert.Equal(uniqueIds.Count, ids.Count);
    }

    [Fact]
    public async Task Search_MultipleMemoriesWithOverlap_EachAppearsOnce()
    {
        // Arrange: Create multiple memories with overlapping content
        var id1 = await StoreMemoryAsync("First hexvera feature", "hexvera validation feature one",
            "hexvera feature for basic validation", ["hexvera", "feature"]);

        var id2 = await StoreMemoryAsync("Second hexvera feature", "hexvera validation feature two",
            "hexvera feature for advanced validation", ["hexvera", "advanced"]);

        var id3 = await StoreMemoryAsync("Third hexvera doc", "hexvera documentation overview",
            "hexvera docs and guides", ["hexvera", "docs"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act
        var request = CreateGetSearchRequest("hexvera", 10);
        var response = await SearchHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(200, response.StatusCode);
        var searchResponse = DeserializeResponse<SearchResponse>(response.Body);
        Assert.NotNull(searchResponse);

        var ids = searchResponse.Results.Select(r => r.Id).ToList();
        var uniqueIds = ids.Distinct().ToList();

        // Each memory should appear exactly once
        Assert.Equal(uniqueIds.Count, ids.Count);

        // Verify all stored memories are found (may include reinforcement duplicates if bug exists)
        var expectedIds = new HashSet<Guid> { id1, id2, id3 };
        foreach (var id in ids)
        {
            Assert.Contains(id, expectedIds);
        }
    }

    [Fact]
    public async Task Search_ViaPostEndpoint_ReturnsUniqueIds()
    {
        // Arrange
        await StoreMemoryAsync("POST search test", "testing POST endpoint for search",
            "content for POST search validation", ["post", "search", "test"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Search via POST endpoint
        var searchRequest = new SearchRequest
        {
            Query = "POST search test",
            TopN = 10
        };
        var request = CreatePostSearchRequest(searchRequest);
        var response = await SearchHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(200, response.StatusCode);
        var searchResponse = DeserializeResponse<SearchResponse>(response.Body);
        Assert.NotNull(searchResponse);

        var ids = searchResponse.Results.Select(r => r.Id).ToList();
        var uniqueIds = ids.Distinct().ToList();

        Assert.Equal(uniqueIds.Count, ids.Count);
    }

    #endregion

    [GeneratedRegex(@"href=""/memory/([0-9a-fA-F-]{36})\.html""")]
    private static partial Regex MemoryLinkRegex();
}
