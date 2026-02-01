using System.Text.Json;
using AgenticMemory.Http.Mcp;

namespace AgenticMemoryTests.McpTests;

/// <summary>
/// Integration tests for MCP workflows, conflict resolution, concurrency, and edge cases.
/// </summary>
public class IntegrationTests : McpTestBase
{
    #region Full Workflow Tests

    [Fact]
    public async Task FullWorkflow_StoreSearchRetrieveUpdateDelete()
    {
        // 1. Store a memory
        var id = await StoreTestMemoryAsync(
            "Integration Test Memory",
            "This is a comprehensive integration test",
            "Full content for the integration test memory",
            ["integration", "test"]);

        // 2. Search for it
        var searchArgs = new Dictionary<string, object?> { ["query"] = "integration test" };
        var searchRequest = CreateToolCallRequest("search_memories", searchArgs);
        var searchResponse = await SendMcpRequestAsync(searchRequest);
        var searchResult = DeserializeResult<ToolCallResult>(searchResponse.Result);
        Assert.NotNull(searchResult);
        Assert.False(searchResult.IsError);
        Assert.Contains("Integration Test Memory", searchResult.Content[0].Text);

        // 3. Retrieve full details
        var getArgs = new Dictionary<string, object?> { ["id"] = id.ToString() };
        var getRequest = CreateToolCallRequest("get_memory", getArgs);
        var getResponse = await SendMcpRequestAsync(getRequest);
        var getResult = DeserializeResult<ToolCallResult>(getResponse.Result);
        Assert.NotNull(getResult);
        Assert.False(getResult.IsError);
        Assert.Contains("Full content for the integration test memory", getResult.Content[0].Text);

        // 4. Update the memory
        var updateArgs = new Dictionary<string, object?>
        {
            ["id"] = id.ToString(),
            ["title"] = "Updated Integration Test",
            ["tags"] = new List<object?> { "integration", "test", "updated" }
        };
        var updateRequest = CreateToolCallRequest("update_memory", updateArgs);
        var updateResponse = await SendMcpRequestAsync(updateRequest);
        var updateResult = DeserializeResult<ToolCallResult>(updateResponse.Result);
        Assert.NotNull(updateResult);
        Assert.False(updateResult.IsError);

        // 5. Verify update
        var verifyResponse = await SendMcpRequestAsync(CreateToolCallRequest("get_memory", getArgs));
        var verifyResult = DeserializeResult<ToolCallResult>(verifyResponse.Result);
        Assert.NotNull(verifyResult);
        Assert.Contains("Updated Integration Test", verifyResult.Content[0].Text);

        // 6. Delete the memory
        var deleteRequest = CreateToolCallRequest("delete_memory", getArgs);
        var deleteResponse = await SendMcpRequestAsync(deleteRequest);
        var deleteResult = DeserializeResult<ToolCallResult>(deleteResponse.Result);
        Assert.NotNull(deleteResult);
        Assert.False(deleteResult.IsError);

        // 7. Verify deletion
        var verifyDeleteResponse = await SendMcpRequestAsync(CreateToolCallRequest("get_memory", getArgs));
        var verifyDeleteResult = DeserializeResult<ToolCallResult>(verifyDeleteResponse.Result);
        Assert.NotNull(verifyDeleteResult);
        Assert.True(verifyDeleteResult.IsError);
        Assert.Contains("not found", verifyDeleteResult.Content[0].Text);
    }

    [Fact]
    public async Task MultipleMemories_ComplexSearch_ReturnsCorrectResults()
    {
        await StoreTestMemoryAsync("Machine Learning Basics", "Introduction to ML algorithms", tags: ["ml", "ai", "basics"]);
        await StoreTestMemoryAsync("Deep Learning Neural Networks", "Understanding neural network architectures", tags: ["ml", "ai", "deep-learning"]);
        await StoreTestMemoryAsync("Python Data Science", "Using Python for data analysis", tags: ["python", "data-science"]);
        await StoreTestMemoryAsync("JavaScript Web Development", "Building web apps with JS", tags: ["javascript", "web"]);
        await StoreTestMemoryAsync("Database Design Patterns", "SQL and NoSQL design principles", tags: ["database", "sql"]);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var searchArgs = new Dictionary<string, object?>
        {
            ["query"] = "machine learning artificial intelligence",
            ["top_n"] = 3
        };

        var request = CreateToolCallRequest("search_memories", searchArgs);
        var response = await SendMcpRequestAsync(request);
        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);

        var text = result.Content[0].Text;
        Assert.True(
            text.Contains("Machine Learning") || text.Contains("Deep Learning") || text.Contains("Neural"),
            "Expected to find ML-related memories");
    }

    #endregion

    #region Conflict Resolution Tests

    [Fact]
    public async Task StoreMemory_DuplicateContent_ReinforcesExisting()
    {
        await StoreTestMemoryAsync("Duplicate Test", "This is duplicate content");

        var args = new Dictionary<string, object?>
        {
            ["title"] = "Duplicate Test",
            ["summary"] = "This is duplicate content",
            ["content"] = "This is duplicate content"
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);
        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);

        var text = result.Content[0].Text;
        Assert.True(
            text.Contains("reinforced", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("stored", StringComparison.OrdinalIgnoreCase),
            "Expected either reinforced or stored message");
    }

    [Fact]
    public async Task StoreMemory_SimilarContent_SupersedesPrevious()
    {
        await StoreTestMemoryAsync("Working at Company A", "I am currently employed at Company A as a software engineer");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var args = new Dictionary<string, object?>
        {
            ["title"] = "Working at Company B",
            ["summary"] = "I am currently employed at Company B as a software engineer",
            ["content"] = "Updated employment information - now working at Company B"
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);
        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);

        var text = result.Content[0].Text;
        Assert.True(
            text.Contains("superseded", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("stored", StringComparison.OrdinalIgnoreCase),
            "Expected superseded or stored message");
    }

    #endregion

    #region Concurrency Tests

    [Fact]
    public async Task ConcurrentOperations_HandleGracefully()
    {
        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            var args = new Dictionary<string, object?>
            {
                ["title"] = $"Concurrent Memory {i}",
                ["summary"] = $"Testing concurrent access {i}"
            };

            var request = CreateToolCallRequest("store_memory", args);
            return await SendMcpRequestAsync(request);
        });

        var responses = await Task.WhenAll(tasks);

        foreach (var response in responses)
        {
            Assert.Null(response.Error);
            var result = DeserializeResult<ToolCallResult>(response.Result);
            Assert.NotNull(result);
            Assert.False(result.IsError);
        }

        var statsRequest = CreateToolCallRequest("get_stats");
        var statsResponse = await SendMcpRequestAsync(statsRequest);
        var statsResult = DeserializeResult<ToolCallResult>(statsResponse.Result);

        Assert.NotNull(statsResult);
        Assert.Contains("Total Nodes: 10", statsResult.Content[0].Text);
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public async Task StoreMemory_UnicodeContent_HandledCorrectly()
    {
        var args = new Dictionary<string, object?>
        {
            ["title"] = "Unicode Test \u65e5\u672c\u8a9e \u4e2d\u6587 \ud55c\uad6d\uc5b4",
            ["summary"] = "Testing unicode: \u00e9 accent, symbols \u2211\u220f\u222b, \u00a9\u00ae",
            ["content"] = "Full unicode content: \u03b1\u03b2\u03b3\u03b4 \u00f1 \u00fc \u00f6 \u0416\u0418\u0412"
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);
        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError, $"Expected success but got error: {result.Content[0].Text}");

        var text = result.Content[0].Text;
        var idLine = text.Split('\n').FirstOrDefault(l => l.StartsWith("ID:"));
        Assert.NotNull(idLine);
        var idStr = idLine.Replace("ID:", "").Trim();
        var id = Guid.Parse(idStr);

        var memory = await Repository.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(memory);
        Assert.Contains("\u65e5\u672c\u8a9e", memory.Title);
        Assert.Contains("\u2211\u220f\u222b", memory.Summary);
        Assert.Contains("\u03b1\u03b2\u03b3", memory.Content);
    }

    [Fact]
    public async Task StoreMemory_SurrogatePairUnicode_HandledGracefully()
    {
        var args = new Dictionary<string, object?>
        {
            ["title"] = "Emoji Test \ud83d\ude80\ud83c\udf1f\ud83d\udc4d",
            ["summary"] = "Testing emojis: \ud83d\ude00 smile \ud83d\udc96 heart \ud83c\udf89 party",
            ["content"] = "Content with emojis \ud83d\udca1 and regular text mixed together \ud83c\udf08"
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);
        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError, $"Expected success but got error: {result.Content[0].Text}");

        var text = result.Content[0].Text;
        var idLine = text.Split('\n').FirstOrDefault(l => l.StartsWith("ID:"));
        Assert.NotNull(idLine);
        var idStr = idLine.Replace("ID:", "").Trim();
        var id = Guid.Parse(idStr);

        var memory = await Repository.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(memory);
        Assert.Contains("\ud83d\ude80", memory.Title);
        Assert.Contains("smile", memory.Summary);
        Assert.Contains("regular text", memory.Content);
    }

    [Fact]
    public async Task StoreMemory_UnpairedSurrogate_HandledGracefully()
    {
        var args = new Dictionary<string, object?>
        {
            ["title"] = "Unpaired Surrogate Test \ud83d alone",
            ["summary"] = "Testing unpaired: \ud83d high and \ude00 low orphans",
            ["content"] = "Content with unpaired \ud83d surrogate that should not crash"
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);
        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError, $"Expected success but got error: {result.Content[0].Text}");

        var text = result.Content[0].Text;
        var idLine = text.Split('\n').FirstOrDefault(l => l.StartsWith("ID:"));
        Assert.NotNull(idLine);
        var idStr = idLine.Replace("ID:", "").Trim();
        Assert.True(Guid.TryParse(idStr, out _), "Should return a valid memory ID");
    }

    [Fact]
    public async Task StoreMemory_LargeContent_HandledCorrectly()
    {
        var largeContent = new string('A', 10000);

        var args = new Dictionary<string, object?>
        {
            ["title"] = "Large Content Test",
            ["summary"] = "Testing large content storage",
            ["content"] = largeContent
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);
        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError, $"Expected success but got error: {result.Content[0].Text}");

        var text = result.Content[0].Text;
        var idLine = text.Split('\n').FirstOrDefault(l => l.StartsWith("ID:"));
        Assert.NotNull(idLine);
        var idStr = idLine.Replace("ID:", "").Trim();
        var id = Guid.Parse(idStr);

        var memory = await Repository.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(memory);
        Assert.Equal(10000, memory.Content.Length);
    }

    #endregion
}
