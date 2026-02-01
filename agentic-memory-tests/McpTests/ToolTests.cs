using System.Text.Json;
using AgenticMemory.Http.Mcp;

namespace AgenticMemoryTests.McpTests;

/// <summary>
/// Tests for MCP tools functionality (tools/list, tools/call, and individual tool behaviors).
/// </summary>
public class ToolTests : McpTestBase
{
    #region Tools/List Tests

    [Fact]
    public async Task ToolsList_ReturnsAllExpectedTools()
    {
        var request = CreateMcpRequest("tools/list");
        var response = await SendMcpRequestAsync(request);

        Assert.Null(response.Error);
        Assert.NotNull(response.Result);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        using var doc = JsonDocument.Parse(resultJson);

        var toolsElement = doc.RootElement.GetProperty("tools");
        var tools = toolsElement.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();

        var expectedTools = new[]
        {
            "search_memories",
            "store_memory",
            "update_memory",
            "get_memory",
            "delete_memory",
            "get_stats",
            "get_tag_history"
        };

        foreach (var expectedTool in expectedTools)
        {
            Assert.Contains(expectedTool, tools);
        }
    }

    [Fact]
    public async Task ToolsList_ToolsHaveValidInputSchemas()
    {
        var request = CreateMcpRequest("tools/list");
        var response = await SendMcpRequestAsync(request);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        using var doc = JsonDocument.Parse(resultJson);

        var toolsElement = doc.RootElement.GetProperty("tools");
        foreach (var tool in toolsElement.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString();
            var description = tool.GetProperty("description").GetString();
            var inputSchema = tool.GetProperty("inputSchema");

            Assert.False(string.IsNullOrEmpty(name), "Tool name should not be empty");
            Assert.False(string.IsNullOrEmpty(description), $"Tool {name} should have a description");
            Assert.Equal("object", inputSchema.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task ToolCall_MissingToolName_ReturnsError()
    {
        var @params = new Dictionary<string, object?>
        {
            ["arguments"] = new Dictionary<string, object?>()
        };

        var request = CreateMcpRequest("tools/call", @params);
        var response = await SendMcpRequestAsync(request);

        Assert.NotNull(response.Error);
        Assert.Contains("missing tool name", response.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolCall_UnknownTool_ReturnsError()
    {
        var args = new Dictionary<string, object?>
        {
            ["someArg"] = "value"
        };

        var request = CreateToolCallRequest("nonexistent_tool", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Contains("Unknown tool", result.Content[0].Text);
    }

    #endregion

    #region store_memory Tool Tests

    [Fact]
    public async Task StoreMemory_WithRequiredFields_Succeeds()
    {
        var args = new Dictionary<string, object?>
        {
            ["title"] = "Test Memory",
            ["summary"] = "This is a test memory"
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);

        Assert.Null(response.Error);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Single(result.Content);
        Assert.Contains("Memory stored", result.Content[0].Text);
    }

    [Fact]
    public async Task StoreMemory_WithAllFields_Succeeds()
    {
        var args = new Dictionary<string, object?>
        {
            ["title"] = "Full Memory",
            ["summary"] = "A memory with all fields",
            ["content"] = "This is the full content of the memory with lots of details.",
            ["tags"] = new List<object?> { "test", "full", "memory" },
            ["importance"] = 0.8
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("Importance: 0.8", result.Content[0].Text);
    }

    [Fact]
    public async Task StoreMemory_MissingTitle_ReturnsError()
    {
        var args = new Dictionary<string, object?>
        {
            ["summary"] = "Missing title"
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Contains("required", result.Content[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StoreMemory_MissingSummary_ReturnsError()
    {
        var args = new Dictionary<string, object?>
        {
            ["title"] = "Missing summary"
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Contains("required", result.Content[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StoreMemory_ImportanceClampedToValidRange()
    {
        var args = new Dictionary<string, object?>
        {
            ["title"] = "High Importance",
            ["summary"] = "Testing importance clamping",
            ["importance"] = 1.5
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("Importance: 1.0", result.Content[0].Text);
    }

    [Fact]
    public async Task StoreMemory_ImportanceNegative_ClampedToZero()
    {
        var args = new Dictionary<string, object?>
        {
            ["title"] = "Low Importance",
            ["summary"] = "Testing negative importance",
            ["importance"] = -0.5
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("Importance: 0.0", result.Content[0].Text);
    }

    [Fact]
    public async Task StoreMemory_EmptyTags_HandledCorrectly()
    {
        var args = new Dictionary<string, object?>
        {
            ["title"] = "No Tags Memory",
            ["summary"] = "Memory without any tags",
            ["tags"] = new List<object?>()
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);
    }

    #endregion

    #region search_memories Tool Tests

    [Fact]
    public async Task SearchMemories_NoResults_ReturnsNoMemoriesMessage()
    {
        var args = new Dictionary<string, object?>
        {
            ["query"] = "nonexistent query xyz123"
        };

        var request = CreateToolCallRequest("search_memories", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("No memories found", result.Content[0].Text);
    }

    [Fact]
    public async Task SearchMemories_WithResults_ReturnsFormattedResults()
    {
        await StoreTestMemoryAsync("Python Programming", "Learning about Python programming language");
        await StoreTestMemoryAsync("JavaScript Basics", "Introduction to JavaScript");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var args = new Dictionary<string, object?>
        {
            ["query"] = "Python programming"
        };

        var request = CreateToolCallRequest("search_memories", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("Python Programming", result.Content[0].Text);
        Assert.Contains("Score:", result.Content[0].Text);
    }

    [Fact]
    public async Task SearchMemories_WithTopN_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
        {
            await StoreTestMemoryAsync($"Test Memory {i}", $"Test summary for memory {i}");
        }
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var args = new Dictionary<string, object?>
        {
            ["query"] = "Test Memory",
            ["top_n"] = 3
        };

        var request = CreateToolCallRequest("search_memories", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);

        var text = result.Content[0].Text;
        var idCount = text.Split("ID:").Length - 1;
        Assert.True(idCount <= 3, $"Expected at most 3 results, got {idCount}");
    }

    [Fact]
    public async Task SearchMemories_WithTags_FiltersResults()
    {
        await StoreTestMemoryAsync("Work Project", "Work related memory", tags: ["work", "project"]);
        await StoreTestMemoryAsync("Personal Note", "Personal memory", tags: ["personal"]);
        await StoreTestMemoryAsync("Work Meeting", "Another work memory", tags: ["work", "meeting"]);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var args = new Dictionary<string, object?>
        {
            ["query"] = "memory",
            ["tags"] = new List<object?> { "work" }
        };

        var request = CreateToolCallRequest("search_memories", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.DoesNotContain("Personal Note", result.Content[0].Text);
    }

    [Fact]
    public async Task SearchMemories_EmptyQuery_HandlesGracefully()
    {
        await StoreTestMemoryAsync("Test Memory", "Some content");

        var args = new Dictionary<string, object?>
        {
            ["query"] = ""
        };

        var request = CreateToolCallRequest("search_memories", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);
    }

    #endregion

    #region get_memory Tool Tests

    [Fact]
    public async Task GetMemory_ValidId_ReturnsMemory()
    {
        var id = await StoreTestMemoryAsync("Retrievable Memory", "This memory can be retrieved", "Full content here", ["test"]);

        var args = new Dictionary<string, object?>
        {
            ["id"] = id.ToString()
        };

        var request = CreateToolCallRequest("get_memory", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);

        var text = result.Content[0].Text;
        Assert.Contains("Retrievable Memory", text);
        Assert.Contains("This memory can be retrieved", text);
        Assert.Contains("Full content here", text);
        Assert.Contains("Strength:", text);
    }

    [Fact]
    public async Task GetMemory_InvalidId_ReturnsError()
    {
        var args = new Dictionary<string, object?>
        {
            ["id"] = "not-a-valid-guid"
        };

        var request = CreateToolCallRequest("get_memory", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Contains("Invalid", result.Content[0].Text);
    }

    [Fact]
    public async Task GetMemory_NonexistentId_ReturnsNotFound()
    {
        var args = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString()
        };

        var request = CreateToolCallRequest("get_memory", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content[0].Text);
    }

    [Fact]
    public async Task GetMemory_ReinforcesMemory()
    {
        var id = await StoreTestMemoryAsync("Reinforcement Test", "Testing reinforcement");

        var args = new Dictionary<string, object?> { ["id"] = id.ToString() };

        for (int i = 0; i < 3; i++)
        {
            var request = CreateToolCallRequest("get_memory", args);
            await SendMcpRequestAsync(request);
        }

        var memory = await Repository.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(memory);
        Assert.True(memory.AccessCount >= 3, $"Expected access count >= 3, got {memory.AccessCount}");
    }

    #endregion

    #region update_memory Tool Tests

    [Fact]
    public async Task UpdateMemory_ValidUpdate_Succeeds()
    {
        var id = await StoreTestMemoryAsync("Original Title", "Original Summary");

        var args = new Dictionary<string, object?>
        {
            ["id"] = id.ToString(),
            ["title"] = "Updated Title",
            ["summary"] = "Updated Summary"
        };

        var request = CreateToolCallRequest("update_memory", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("Updated Title", result.Content[0].Text);

        var memory = await Repository.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(memory);
        Assert.Equal("Updated Title", memory.Title);
        Assert.Equal("Updated Summary", memory.Summary);
    }

    [Fact]
    public async Task UpdateMemory_PartialUpdate_OnlyUpdatesProvidedFields()
    {
        var id = await StoreTestMemoryAsync("Original Title", "Original Summary", "Original Content", ["original"]);

        var args = new Dictionary<string, object?>
        {
            ["id"] = id.ToString(),
            ["title"] = "Updated Title Only"
        };

        var request = CreateToolCallRequest("update_memory", args);
        await SendMcpRequestAsync(request);

        var memory = await Repository.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(memory);
        Assert.Equal("Updated Title Only", memory.Title);
        Assert.Equal("Original Summary", memory.Summary);
        Assert.Equal("Original Content", memory.Content);
        Assert.Contains("original", memory.Tags);
    }

    [Fact]
    public async Task UpdateMemory_UpdateTags_ReplacesAllTags()
    {
        var id = await StoreTestMemoryAsync("Tag Test", "Testing tags", tags: ["old1", "old2"]);

        var args = new Dictionary<string, object?>
        {
            ["id"] = id.ToString(),
            ["tags"] = new List<object?> { "new1", "new2", "new3" }
        };

        var request = CreateToolCallRequest("update_memory", args);
        await SendMcpRequestAsync(request);

        var memory = await Repository.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(memory);
        Assert.DoesNotContain("old1", memory.Tags);
        Assert.DoesNotContain("old2", memory.Tags);
        Assert.Contains("new1", memory.Tags);
        Assert.Contains("new2", memory.Tags);
        Assert.Contains("new3", memory.Tags);
    }

    [Fact]
    public async Task UpdateMemory_InvalidId_ReturnsError()
    {
        var args = new Dictionary<string, object?>
        {
            ["id"] = "invalid",
            ["title"] = "New Title"
        };

        var request = CreateToolCallRequest("update_memory", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task UpdateMemory_NonexistentId_ReturnsNotFound()
    {
        var args = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["title"] = "New Title"
        };

        var request = CreateToolCallRequest("update_memory", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content[0].Text);
    }

    #endregion

    #region delete_memory Tool Tests

    [Fact]
    public async Task DeleteMemory_ValidId_DeletesMemory()
    {
        var id = await StoreTestMemoryAsync("To Be Deleted", "This will be deleted");

        var args = new Dictionary<string, object?>
        {
            ["id"] = id.ToString()
        };

        var request = CreateToolCallRequest("delete_memory", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("deleted", result.Content[0].Text);

        var memory = await Repository.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.Null(memory);
    }

    [Fact]
    public async Task DeleteMemory_NonexistentId_ReturnsNotFound()
    {
        var args = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString()
        };

        var request = CreateToolCallRequest("delete_memory", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("not found", result.Content[0].Text);
    }

    [Fact]
    public async Task DeleteMemory_InvalidId_ReturnsError()
    {
        var args = new Dictionary<string, object?>
        {
            ["id"] = "not-a-guid"
        };

        var request = CreateToolCallRequest("delete_memory", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.True(result.IsError);
    }

    #endregion

    #region get_stats Tool Tests

    [Fact]
    public async Task GetStats_EmptyRepository_ReturnsZeros()
    {
        var request = CreateToolCallRequest("get_stats");
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("Total Nodes: 0", result.Content[0].Text);
    }

    [Fact]
    public async Task GetStats_WithMemories_ReturnsCorrectCounts()
    {
        for (int i = 0; i < 5; i++)
        {
            await StoreTestMemoryAsync($"Stats Test {i}", $"Testing statistics {i}");
        }

        var request = CreateToolCallRequest("get_stats");
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);

        var text = result.Content[0].Text;
        Assert.Contains("Total Nodes: 5", text);
        Assert.Contains("Average Strength:", text);
        Assert.Contains("Database Size:", text);
    }

    #endregion

    #region get_tag_history Tool Tests

    [Fact]
    public async Task GetTagHistory_NoMemoriesWithTag_ReturnsNotFound()
    {
        var args = new Dictionary<string, object?>
        {
            ["tag"] = "nonexistent-tag"
        };

        var request = CreateToolCallRequest("get_tag_history", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("No memories found", result.Content[0].Text);
    }

    [Fact]
    public async Task GetTagHistory_WithMemories_ReturnsHistory()
    {
        await StoreTestMemoryAsync("Job 1", "First job", tags: ["employment"]);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await StoreTestMemoryAsync("Job 2", "Second job", tags: ["employment"]);

        var args = new Dictionary<string, object?>
        {
            ["tag"] = "employment"
        };

        var request = CreateToolCallRequest("get_tag_history", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("employment", result.Content[0].Text);
    }

    [Fact]
    public async Task GetTagHistory_MissingTag_ReturnsError()
    {
        var args = new Dictionary<string, object?>
        {
            ["include_archived"] = true
        };

        var request = CreateToolCallRequest("get_tag_history", args);
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<ToolCallResult>(response.Result);

        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Contains("required", result.Content[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
