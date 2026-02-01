using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticMemory.Http.Handlers;
using AgenticMemory.Http.Models;
using HttpMethod = AgenticMemory.Http.Models.HttpMethod;

namespace AgenticMemoryTests.Frontend;

/// <summary>
/// Tests for frontend memory management operations including viewing, deleting,
/// and management UI elements on memory detail pages.
/// </summary>
public partial class MemoryManagementTests : FrontendTestBase
{
    private StaticFileHandler StaticHandler { get; set; } = null!;
    private MemoryHandler MemoryHandler { get; set; } = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        StaticHandler = new StaticFileHandler(Repository);
        MemoryHandler = new MemoryHandler(Repository);
    }

    #region Memory Detail Page Tests

    [Fact]
    public async Task MemoryDetailPage_WithSearchQuery_ShowsBackToSearchResultsLink()
    {
        // Arrange: Store a memory
        var id = await StoreMemoryAsync("Test Memory", "A test memory for navigation",
            "Full content of the test memory", ["test", "navigation"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Request memory detail page with search query parameter
        var request = new Request
        {
            Method = HttpMethod.GET,
            Path = $"/memory/{id}.html",
            QueryString = new Dictionary<string, string> { ["q"] = "test query" }
        };
        var response = await StaticHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Page should have "Back to Search Results" link with query
        Assert.Equal(200, response.StatusCode);
        var html = response.Body?.ToString() ?? "";

        Assert.Contains("Back to Search Results", html);
        Assert.Contains("/search?q=test", html);
    }

    [Fact]
    public async Task MemoryDetailPage_WithoutSearchQuery_ShowsBackToSearchLink()
    {
        // Arrange: Store a memory
        var id = await StoreMemoryAsync("Test Memory", "A test memory",
            "Full content", ["test"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Request memory detail page without search query
        var request = new Request
        {
            Method = HttpMethod.GET,
            Path = $"/memory/{id}.html",
            QueryString = new Dictionary<string, string>()
        };
        var response = await StaticHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Page should have generic "Back to Search" link
        Assert.Equal(200, response.StatusCode);
        var html = response.Body?.ToString() ?? "";

        Assert.Contains("Back to Search", html);
        Assert.Contains("href=\"/\"", html);
    }

    [Fact]
    public async Task MemoryDetailPage_ContainsManagementActionsSection()
    {
        // Arrange: Store a memory
        var id = await StoreMemoryAsync("Test Memory", "A test memory",
            "Full content", ["test"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Request memory detail page
        var request = new Request
        {
            Method = HttpMethod.GET,
            Path = $"/memory/{id}.html",
            QueryString = new Dictionary<string, string>()
        };
        var response = await StaticHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Page should contain management actions
        Assert.Equal(200, response.StatusCode);
        var html = response.Body?.ToString() ?? "";

        Assert.Contains("management-actions", html);
        Assert.Contains("Actions", html);
    }

    [Fact]
    public async Task MemoryDetailPage_ContainsDeleteButton()
    {
        // Arrange: Store a memory
        var id = await StoreMemoryAsync("Test Memory", "A test memory",
            "Full content", ["test"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Request memory detail page
        var request = new Request
        {
            Method = HttpMethod.GET,
            Path = $"/memory/{id}.html",
            QueryString = new Dictionary<string, string>()
        };
        var response = await StaticHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Page should contain delete button
        Assert.Equal(200, response.StatusCode);
        var html = response.Body?.ToString() ?? "";

        Assert.Contains("Delete Memory", html);
        Assert.Contains("deleteMemory", html);
        Assert.Contains("btn-danger", html);
    }

    [Fact]
    public async Task MemoryDetailPage_ContainsReinforceButton()
    {
        // Arrange: Store a memory
        var id = await StoreMemoryAsync("Test Memory", "A test memory",
            "Full content", ["test"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Request memory detail page
        var request = new Request
        {
            Method = HttpMethod.GET,
            Path = $"/memory/{id}.html",
            QueryString = new Dictionary<string, string>()
        };
        var response = await StaticHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Page should contain reinforce button
        Assert.Equal(200, response.StatusCode);
        var html = response.Body?.ToString() ?? "";

        Assert.Contains("Reinforce", html);
        Assert.Contains("reinforceMemory", html);
    }

    [Fact]
    public async Task MemoryDetailPage_ContainsCopyIdButton()
    {
        // Arrange: Store a memory
        var id = await StoreMemoryAsync("Test Memory", "A test memory",
            "Full content", ["test"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Request memory detail page
        var request = new Request
        {
            Method = HttpMethod.GET,
            Path = $"/memory/{id}.html",
            QueryString = new Dictionary<string, string>()
        };
        var response = await StaticHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Page should contain copy ID button
        Assert.Equal(200, response.StatusCode);
        var html = response.Body?.ToString() ?? "";

        Assert.Contains("Copy ID", html);
        Assert.Contains("copyId", html);
    }

    [Fact]
    public async Task MemoryDetailPage_ButtonsReferenceCorrectMemoryId()
    {
        // Arrange: Store a memory
        var id = await StoreMemoryAsync("Test Memory", "A test memory",
            "Full content", ["test"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Request memory detail page
        var request = new Request
        {
            Method = HttpMethod.GET,
            Path = $"/memory/{id}.html",
            QueryString = new Dictionary<string, string>()
        };
        var response = await StaticHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Buttons should reference the correct memory ID
        Assert.Equal(200, response.StatusCode);
        var html = response.Body?.ToString() ?? "";

        // Check that the ID appears in onclick handlers
        Assert.Contains($"deleteMemory('{id}')", html);
        Assert.Contains($"reinforceMemory('{id}')", html);
        Assert.Contains($"copyId('{id}')", html);
    }

    [Fact]
    public async Task MemoryDetailPage_NonExistentMemory_Returns404()
    {
        // Arrange: Use a random ID that doesn't exist
        var nonExistentId = Guid.NewGuid();

        // Act: Request non-existent memory detail page
        var request = new Request
        {
            Method = HttpMethod.GET,
            Path = $"/memory/{nonExistentId}.html",
            QueryString = new Dictionary<string, string>()
        };
        var response = await StaticHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Should return 404
        Assert.Equal(404, response.StatusCode);
    }

    #endregion

    #region Delete Memory API Tests

    [Fact]
    public async Task DeleteMemory_ExistingMemory_ReturnsNoContent()
    {
        // Arrange: Store a memory
        var id = await StoreMemoryAsync("Memory to Delete", "This will be deleted",
            "Content to delete", ["delete", "test"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Delete the memory via API
        var request = new Request
        {
            Method = HttpMethod.DELETE,
            Path = $"/api/memory/{id}",
            Parameters = new Dictionary<string, string> { ["id"] = id.ToString() }
        };
        var response = await MemoryHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Should return 204 No Content
        Assert.Equal(204, response.StatusCode);
    }

    [Fact]
    public async Task DeleteMemory_NonExistentMemory_Returns404()
    {
        // Arrange: Use a random ID that doesn't exist
        var nonExistentId = Guid.NewGuid();

        // Act: Try to delete non-existent memory
        var request = new Request
        {
            Method = HttpMethod.DELETE,
            Path = $"/api/memory/{nonExistentId}",
            Parameters = new Dictionary<string, string> { ["id"] = nonExistentId.ToString() }
        };
        var response = await MemoryHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Should return 404
        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task DeleteMemory_MemoryNoLongerAccessible()
    {
        // Arrange: Store a memory
        var id = await StoreMemoryAsync("Memory to Delete", "This will be deleted",
            "Content to delete", ["delete", "test"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Delete the memory
        var deleteRequest = new Request
        {
            Method = HttpMethod.DELETE,
            Path = $"/api/memory/{id}",
            Parameters = new Dictionary<string, string> { ["id"] = id.ToString() }
        };
        await MemoryHandler.HandleAsync(deleteRequest, TestContext.Current.CancellationToken);

        // Assert: Memory detail page should return 404
        var getRequest = new Request
        {
            Method = HttpMethod.GET,
            Path = $"/memory/{id}.html",
            QueryString = new Dictionary<string, string>()
        };
        var response = await StaticHandler.HandleAsync(getRequest, TestContext.Current.CancellationToken);

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task DeleteMemory_InvalidId_ReturnsBadRequest()
    {
        // Act: Try to delete with invalid ID
        var request = new Request
        {
            Method = HttpMethod.DELETE,
            Path = "/api/memory/not-a-guid",
            Parameters = new Dictionary<string, string> { ["id"] = "not-a-guid" }
        };
        var response = await MemoryHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Should return 400 Bad Request
        Assert.Equal(400, response.StatusCode);
    }

    #endregion

    #region Reinforce Memory API Tests

    [Fact]
    public async Task ReinforceMemory_ExistingMemory_ReturnsSuccess()
    {
        // Arrange: Store a memory
        var id = await StoreMemoryAsync("Memory to Reinforce", "This will be reinforced",
            "Content to reinforce", ["reinforce", "test"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Get initial reinforcement score
        var getRequest = new Request
        {
            Method = HttpMethod.GET,
            Path = $"/api/memory/{id}",
            Parameters = new Dictionary<string, string> { ["id"] = id.ToString() }
        };
        var getResponse = await MemoryHandler.HandleAsync(getRequest, TestContext.Current.CancellationToken);
        var memoryBefore = DeserializeResponse<MemoryNode>(getResponse.Body);
        var initialScore = memoryBefore?.ReinforcementScore ?? 0;

        // Act: Reinforce the memory
        var reinforceHandler = new ReinforceHandler(Repository);
        var reinforceRequest = new Request
        {
            Method = HttpMethod.POST,
            Path = $"/api/memory/{id}/reinforce",
            Parameters = new Dictionary<string, string> { ["id"] = id.ToString() }
        };
        var response = await reinforceHandler.HandleAsync(reinforceRequest, TestContext.Current.CancellationToken);

        // Assert: Should return 200
        Assert.Equal(200, response.StatusCode);

        // Verify reinforcement score increased
        var getResponse2 = await MemoryHandler.HandleAsync(getRequest, TestContext.Current.CancellationToken);
        var memoryAfter = DeserializeResponse<MemoryNode>(getResponse2.Body);
        Assert.NotNull(memoryAfter);
        Assert.True(memoryAfter.ReinforcementScore > initialScore, 
            $"Expected reinforcement score to increase from {initialScore} but got {memoryAfter.ReinforcementScore}");
    }

    [Fact]
    public async Task ReinforceMemory_NonExistentMemory_Returns404()
    {
        // Arrange: Use a random ID that doesn't exist
        var nonExistentId = Guid.NewGuid();

        // Act: Try to reinforce non-existent memory
        var reinforceHandler = new ReinforceHandler(Repository);
        var request = new Request
        {
            Method = HttpMethod.POST,
            Path = $"/api/memory/{nonExistentId}/reinforce",
            Parameters = new Dictionary<string, string> { ["id"] = nonExistentId.ToString() }
        };
        var response = await reinforceHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Should return 404
        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task ReinforceMemory_InvalidMethod_ReturnsMethodNotAllowed()
    {
        // Arrange: Store a memory
        var id = await StoreMemoryAsync("Test Memory", "Test",
            "Content", ["test"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Try to reinforce with GET instead of POST
        var reinforceHandler = new ReinforceHandler(Repository);
        var request = new Request
        {
            Method = HttpMethod.GET,
            Path = $"/api/memory/{id}/reinforce",
            Parameters = new Dictionary<string, string> { ["id"] = id.ToString() }
        };
        var response = await reinforceHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Should return 405 Method Not Allowed
        Assert.Equal(405, response.StatusCode);
    }

    #endregion

    #region Get Memory API Tests

    [Fact]
    public async Task GetMemory_ExistingMemory_ReturnsMemoryData()
    {
        // Arrange: Store a memory
        var id = await StoreMemoryAsync("Get Test Memory", "Summary for get test",
            "Content for get test", ["get", "test"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Get the memory via API
        var request = new Request
        {
            Method = HttpMethod.GET,
            Path = $"/api/memory/{id}",
            Parameters = new Dictionary<string, string> { ["id"] = id.ToString() }
        };
        var response = await MemoryHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Should return 200 with memory data
        Assert.Equal(200, response.StatusCode);
        var memory = DeserializeResponse<MemoryNode>(response.Body);
        Assert.NotNull(memory);
        Assert.Equal(id, memory.Id);
        Assert.Equal("Get Test Memory", memory.Title);
        Assert.Equal("Summary for get test", memory.Summary);
    }

    [Fact]
    public async Task GetMemory_NonExistentMemory_Returns404()
    {
        // Arrange: Use a random ID that doesn't exist
        var nonExistentId = Guid.NewGuid();

        // Act: Try to get non-existent memory
        var request = new Request
        {
            Method = HttpMethod.GET,
            Path = $"/api/memory/{nonExistentId}",
            Parameters = new Dictionary<string, string> { ["id"] = nonExistentId.ToString() }
        };
        var response = await MemoryHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Should return 404
        Assert.Equal(404, response.StatusCode);
    }

    #endregion

    #region Search to Memory Navigation Tests

    [Fact]
    public async Task SearchResultLinks_IncludeQueryParameter()
    {
        // Arrange: Store a memory
        await StoreMemoryAsync("Navigation Test", "Testing navigation from search",
            "Content for navigation test", ["navigation", "test"]);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Act: Search via HTML endpoint
        var request = CreateGetSearchRequest("navigation", 10, acceptHtml: true);
        var response = await SearchHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert: Memory links should include query parameter
        Assert.Equal(200, response.StatusCode);
        var html = response.Body?.ToString() ?? "";

        // Links should have format /memory/{id}.html?q={query}
        Assert.Matches(MemoryLinkWithQueryRegex(), html);
    }

    #endregion

    [GeneratedRegex(@"href=""/memory/[0-9a-fA-F-]{36}\.html\?q=[^""]+""")]
    private static partial Regex MemoryLinkWithQueryRegex();
}
