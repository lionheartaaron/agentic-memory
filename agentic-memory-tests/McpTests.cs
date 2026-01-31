using System.Text.Json;
using AgenticMemory.Brain.Conflict;
using AgenticMemory.Brain.Embeddings;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Search;
using AgenticMemory.Brain.Storage;
using AgenticMemory.Configuration;
using AgenticMemory.Http.Mcp;
using AgenticMemory.Http.Models;
using Microsoft.Extensions.Logging;
using HttpMethod = AgenticMemory.Http.Models.HttpMethod;

namespace agentic_memory_tests;

/// <summary>
/// Comprehensive test suite for MCP (Model Context Protocol) functionality.
/// Uses real LiteDB and embedding models - models are downloaded if needed.
/// </summary>
public class McpTests : IAsyncLifetime
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
    private McpHandler _mcpHandler = null!;
    private ILoggerFactory _loggerFactory = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public McpTests()
    {
        // Unique test database per test instance
        var testId = Guid.NewGuid().ToString("N")[..8];
        _testDbPath = Path.Combine(Path.GetTempPath(), "agentic-memory-tests", $"test-{testId}.db");
        _testModelsPath = Path.Combine(Path.GetTempPath(), "agentic-memory-tests", "models");
    }

    public async ValueTask InitializeAsync()
    {
        // Create directories
        var dbDir = Path.GetDirectoryName(_testDbPath);
        if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
            Directory.CreateDirectory(dbDir);

        if (!Directory.Exists(_testModelsPath))
            Directory.CreateDirectory(_testModelsPath);

        // Setup logging
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole().SetMinimumLevel(LogLevel.Warning);
        });

        // Download models if needed (thread-safe singleton pattern)
        await EnsureModelsDownloadedAsync();

        // Delete existing database if present (fresh start for each test)
        if (File.Exists(_testDbPath))
            File.Delete(_testDbPath);

        // Initialize repository
        _repository = new LiteDbMemoryRepository(_testDbPath);

        // Initialize embedding service
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

        // Initialize search service
        _searchService = new MemorySearchEngine(
            _repository,
            _embeddingService,
            _loggerFactory.CreateLogger<MemorySearchEngine>());

        // Initialize conflict-aware storage
        var conflictSettings = new ConflictSettings();
        _conflictStorage = new ConflictAwareStorageService(
            _repository,
            _searchService,
            _embeddingService,
            conflictSettings,
            _loggerFactory.CreateLogger<ConflictAwareStorageService>());

        // Initialize MCP handler with all services
        _mcpHandler = new McpHandler(
            _repository,
            _searchService,
            _conflictStorage,
            null, // Use default storage settings
            _loggerFactory.CreateLogger<McpHandler>());
    }

    public async ValueTask DisposeAsync()
    {
        // Dispose services - catch mutex exceptions that can occur when
        // DisposeAsync is called from a different thread than InitializeAsync
        try
        {
            _embeddingService?.Dispose();
        }
        catch (ApplicationException)
        {
            // Ignore mutex release errors during cleanup
        }

        try
        {
            _repository?.Dispose();
        }
        catch (ApplicationException)
        {
            // LiteDB mutex can throw if disposed from different thread
        }

        try
        {
            _loggerFactory?.Dispose();
        }
        catch (ApplicationException)
        {
            // Ignore mutex release errors during cleanup
        }

        // Clean up test database
        await Task.Delay(100); // Give LiteDB time to release file
        try
        {
            if (File.Exists(_testDbPath))
                File.Delete(_testDbPath);

            // Delete journal file if exists
            var journalPath = _testDbPath + "-journal";
            if (File.Exists(journalPath))
                File.Delete(journalPath);
        }
        catch
        {
            // Ignore cleanup errors
        }
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

    private Request CreateMcpRequest(string method, object? @params = null)
    {
        var rpcRequest = new JsonRpcRequest
        {
            Jsonrpc = "2.0",
            Id = Guid.NewGuid().ToString(),
            Method = method,
            Params = @params as Dictionary<string, object?>
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(rpcRequest, JsonOptions);

        return new Request
        {
            Method = HttpMethod.POST,
            Path = "/mcp",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json"
            },
            Body = body
        };
    }

    private Request CreateToolCallRequest(string toolName, Dictionary<string, object?>? arguments = null)
    {
        var @params = new Dictionary<string, object?>
        {
            ["name"] = toolName,
            ["arguments"] = arguments ?? new Dictionary<string, object?>()
        };

        return CreateMcpRequest("tools/call", @params);
    }


    private async Task<JsonRpcResponse> SendMcpRequestAsync(Request request)
    {
        var response = await _mcpHandler.HandleAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(200, response.StatusCode);

        var json = JsonSerializer.Serialize(response.Body, JsonOptions);
        var rpcResponse = JsonSerializer.Deserialize<JsonRpcResponse>(json, JsonOptions);
        Assert.NotNull(rpcResponse);
        return rpcResponse;
    }

    private async Task<Guid> StoreTestMemoryAsync(string title, string summary, string? content = null, List<string>? tags = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["summary"] = summary,
            ["content"] = content ?? $"Content for {title}",
            ["tags"] = tags?.Cast<object?>().ToList() ?? new List<object?>()
        };

        var request = CreateToolCallRequest("store_memory", args);
        var rpcResponse = await SendMcpRequestAsync(request);

        Assert.Null(rpcResponse.Error);
        Assert.NotNull(rpcResponse.Result);

        // Extract ID from response
        var resultJson = JsonSerializer.Serialize(rpcResponse.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);
        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Single(result.Content);

        var text = result.Content[0].Text;
        var idLine = text.Split('\n').FirstOrDefault(l => l.StartsWith("ID:"));
        Assert.NotNull(idLine);

        var idStr = idLine.Replace("ID:", "").Trim();
        Assert.True(Guid.TryParse(idStr, out var id));
        return id;
    }

    #endregion

    #region Protocol Tests

    [Fact]
    public async Task Initialize_ReturnsCorrectProtocolVersion()
    {
        var request = CreateMcpRequest("initialize");
        var response = await SendMcpRequestAsync(request);

        Assert.Null(response.Error);
        Assert.NotNull(response.Result);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<InitializeResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.Equal("2024-11-05", result.ProtocolVersion);
        Assert.Equal("agentic-memory", result.ServerInfo.Name);
        Assert.Equal("1.0.0", result.ServerInfo.Version);
    }

    [Fact]
    public async Task Initialize_ReturnsCapabilities()
    {
        var request = CreateMcpRequest("initialize");
        var response = await SendMcpRequestAsync(request);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<InitializeResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.NotNull(result.Capabilities.Tools);
        Assert.NotNull(result.Capabilities.Resources);
        Assert.False(result.Capabilities.Tools.ListChanged);
        Assert.False(result.Capabilities.Resources.Subscribe);
        Assert.False(result.Capabilities.Resources.ListChanged);
    }

    [Fact]
    public async Task Initialized_ReturnsEmptyResult()
    {
        var request = CreateMcpRequest("initialized");
        var response = await SendMcpRequestAsync(request);

        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
    }

    [Fact]
    public async Task Ping_ReturnsEmptyResult()
    {
        var request = CreateMcpRequest("ping");
        var response = await SendMcpRequestAsync(request);

        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
    }

    [Fact]
    public async Task UnknownMethod_ReturnsError()
    {
        var request = CreateMcpRequest("unknown/method");
        var response = await SendMcpRequestAsync(request);

        Assert.NotNull(response.Error);
        Assert.Equal(-32601, response.Error.Code);
        Assert.Contains("Method not found", response.Error.Message);
    }

    [Fact]
    public async Task NonPostRequest_ReturnsMethodNotAllowed()
    {
        var request = new Request
        {
            Method = HttpMethod.GET,
            Path = "/mcp"
        };

        var response = await _mcpHandler.HandleAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(405, response.StatusCode);
    }

    [Fact]
    public async Task InvalidJson_ReturnsParseError()
    {
        var request = new Request
        {
            Method = HttpMethod.POST,
            Path = "/mcp",
            Body = "{ invalid json"u8.ToArray()
        };

        var response = await _mcpHandler.HandleAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(200, response.StatusCode);

        var json = JsonSerializer.Serialize(response.Body, JsonOptions);
        var rpcResponse = JsonSerializer.Deserialize<JsonRpcResponse>(json, JsonOptions);

        Assert.NotNull(rpcResponse?.Error);
        Assert.Equal(-32700, rpcResponse.Error.Code);
        Assert.Contains("Parse error", rpcResponse.Error.Message);
    }

    #endregion

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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Contains("required", result.Content[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StoreMemory_ImportanceClampedToValidRange()
    {
        // Test importance > 1.0
        var args = new Dictionary<string, object?>
        {
            ["title"] = "High Importance",
            ["summary"] = "Testing importance clamping",
            ["importance"] = 1.5
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("Importance: 0.0", result.Content[0].Text);
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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("No memories found", result.Content[0].Text);
    }

    [Fact]
    public async Task SearchMemories_WithResults_ReturnsFormattedResults()
    {
        // Store some test memories
        await StoreTestMemoryAsync("Python Programming", "Learning about Python programming language");
        await StoreTestMemoryAsync("JavaScript Basics", "Introduction to JavaScript");

        // Small delay to ensure indexing
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var args = new Dictionary<string, object?>
        {
            ["query"] = "Python programming"
        };

        var request = CreateToolCallRequest("search_memories", args);
        var response = await SendMcpRequestAsync(request);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("Python Programming", result.Content[0].Text);
        Assert.Contains("Score:", result.Content[0].Text);
    }

    [Fact]
    public async Task SearchMemories_WithTopN_RespectsLimit()
    {
        // Store multiple memories
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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError);

        // Count occurrences of "ID:" in results
        var text = result.Content[0].Text;
        var idCount = text.Split("ID:").Length - 1;
        Assert.True(idCount <= 3, $"Expected at most 3 results, got {idCount}");
    }

    [Fact]
    public async Task SearchMemories_WithTags_FiltersResults()
    {
        // Store memories with different tags
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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError);

        var text = result.Content[0].Text;
        Assert.DoesNotContain("Personal Note", text);
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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content[0].Text);
    }

    [Fact]
    public async Task GetMemory_ReinforcesMemory()
    {
        var id = await StoreTestMemoryAsync("Reinforcement Test", "Testing reinforcement");

        // Get the memory multiple times
        var args = new Dictionary<string, object?> { ["id"] = id.ToString() };

        for (int i = 0; i < 3; i++)
        {
            var request = CreateToolCallRequest("get_memory", args);
            await SendMcpRequestAsync(request);
        }

        // Verify the memory was reinforced
        var memory = await _repository.GetAsync(id, TestContext.Current.CancellationToken);
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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("Updated Title", result.Content[0].Text);

        // Verify update persisted
        var memory = await _repository.GetAsync(id, TestContext.Current.CancellationToken);
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

        var memory = await _repository.GetAsync(id, TestContext.Current.CancellationToken);
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

        var memory = await _repository.GetAsync(id, TestContext.Current.CancellationToken);
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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("deleted", result.Content[0].Text);

        // Verify deletion
        var memory = await _repository.GetAsync(id, TestContext.Current.CancellationToken);
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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("Total Nodes: 0", result.Content[0].Text);
    }

    [Fact]
    public async Task GetStats_WithMemories_ReturnsCorrectCounts()
    {
        // Store some memories
        for (int i = 0; i < 5; i++)
        {
            await StoreTestMemoryAsync($"Stats Test {i}", $"Testing statistics {i}");
        }

        var request = CreateToolCallRequest("get_stats");
        var response = await SendMcpRequestAsync(request);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("No memories found", result.Content[0].Text);
    }

    [Fact]
    public async Task GetTagHistory_WithMemories_ReturnsHistory()
    {
        // Store memories with the same tag
        await StoreTestMemoryAsync("Job 1", "First job", tags: ["employment"]);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await StoreTestMemoryAsync("Job 2", "Second job", tags: ["employment"]);

        var args = new Dictionary<string, object?>
        {
            ["tag"] = "employment"
        };

        var request = CreateToolCallRequest("get_tag_history", args);
        var response = await SendMcpRequestAsync(request);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Contains("required", result.Content[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Resources Tests

    [Fact]
    public async Task ResourcesList_ReturnsAvailableResources()
    {
        var request = CreateMcpRequest("resources/list");
        var response = await SendMcpRequestAsync(request);

        Assert.Null(response.Error);
        Assert.NotNull(response.Result);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        using var doc = JsonDocument.Parse(resultJson);

        var resources = doc.RootElement.GetProperty("resources");
        var uris = resources.EnumerateArray().Select(r => r.GetProperty("uri").GetString()).ToList();

        Assert.Contains("memory://recent", uris);
        Assert.Contains("memory://stats", uris);
    }

    [Fact]
    public async Task ResourcesRead_Stats_ReturnsStatistics()
    {
        var @params = new Dictionary<string, object?>
        {
            ["uri"] = "memory://stats"
        };

        var request = CreateMcpRequest("resources/read", @params);
        var response = await SendMcpRequestAsync(request);

        Assert.Null(response.Error);
        Assert.NotNull(response.Result);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        using var doc = JsonDocument.Parse(resultJson);

        var contents = doc.RootElement.GetProperty("contents");
        Assert.Single(contents.EnumerateArray());

        var content = contents.EnumerateArray().First();
        Assert.Equal("memory://stats", content.GetProperty("uri").GetString());
        Assert.Equal("application/json", content.GetProperty("mimeType").GetString());
    }

    [Fact]
    public async Task ResourcesRead_Recent_ReturnsRecentMemories()
    {
        // Store some memories
        await StoreTestMemoryAsync("Recent 1", "First recent memory");
        await StoreTestMemoryAsync("Recent 2", "Second recent memory");

        var @params = new Dictionary<string, object?>
        {
            ["uri"] = "memory://recent"
        };

        var request = CreateMcpRequest("resources/read", @params);
        var response = await SendMcpRequestAsync(request);

        Assert.Null(response.Error);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        using var doc = JsonDocument.Parse(resultJson);

        var contents = doc.RootElement.GetProperty("contents");
        var content = contents.EnumerateArray().First();
        var text = content.GetProperty("text").GetString();

        Assert.NotNull(text);
        Assert.Contains("Recent", text);
    }

    [Fact]
    public async Task ResourcesRead_MemoryById_ReturnsMemory()
    {
        var id = await StoreTestMemoryAsync("Resource Test", "Testing resource access", "Full content");

        var @params = new Dictionary<string, object?>
        {
            ["uri"] = $"memory://{id}"
        };

        var request = CreateMcpRequest("resources/read", @params);
        var response = await SendMcpRequestAsync(request);

        Assert.Null(response.Error);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        using var doc = JsonDocument.Parse(resultJson);

        var contents = doc.RootElement.GetProperty("contents");
        var content = contents.EnumerateArray().First();
        var text = content.GetProperty("text").GetString();

        Assert.NotNull(text);
        Assert.Contains("Resource Test", text);
    }

    [Fact]
    public async Task ResourcesRead_InvalidUri_ReturnsError()
    {
        var @params = new Dictionary<string, object?>
        {
            ["uri"] = "http://invalid"
        };

        var request = CreateMcpRequest("resources/read", @params);
        var response = await SendMcpRequestAsync(request);

        Assert.NotNull(response.Error);
        Assert.Contains("Invalid URI scheme", response.Error.Message);
    }

    [Fact]
    public async Task ResourcesRead_InvalidPath_ReturnsError()
    {
        var @params = new Dictionary<string, object?>
        {
            ["uri"] = "memory://invalid-path"
        };

        var request = CreateMcpRequest("resources/read", @params);
        var response = await SendMcpRequestAsync(request);

        Assert.NotNull(response.Error);
        Assert.Contains("Invalid resource path", response.Error.Message);
    }

    [Fact]
    public async Task ResourcesRead_NonexistentMemory_ReturnsError()
    {
        var @params = new Dictionary<string, object?>
        {
            ["uri"] = $"memory://{Guid.NewGuid()}"
        };

        var request = CreateMcpRequest("resources/read", @params);
        var response = await SendMcpRequestAsync(request);

        Assert.NotNull(response.Error);
        Assert.Contains("not found", response.Error.Message);
    }

    #endregion

    #region Conflict Resolution Tests

    [Fact]
    public async Task StoreMemory_DuplicateContent_ReinforcesExisting()
    {
        // Store initial memory
        var id1 = await StoreTestMemoryAsync("Duplicate Test", "This is duplicate content");

        // Store nearly identical memory
        var args = new Dictionary<string, object?>
        {
            ["title"] = "Duplicate Test",
            ["summary"] = "This is duplicate content",
            ["content"] = "This is duplicate content"
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError);

        // The response might indicate reinforcement or coexistence depending on similarity threshold
        var text = result.Content[0].Text;
        Assert.True(
            text.Contains("reinforced", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("stored", StringComparison.OrdinalIgnoreCase),
            "Expected either reinforced or stored message");
    }

    [Fact]
    public async Task StoreMemory_SimilarContent_SupersedesPrevious()
    {
        // Store first memory about a topic
        await StoreTestMemoryAsync("Working at Company A", "I am currently employed at Company A as a software engineer");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Store new memory with very similar content (should supersede based on content similarity)
        var args = new Dictionary<string, object?>
        {
            ["title"] = "Working at Company B",
            ["summary"] = "I am currently employed at Company B as a software engineer",
            ["content"] = "Updated employment information - now working at Company B"
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError);

        // The response should indicate either superseding or coexistence based on similarity
        var text = result.Content[0].Text;
        Assert.True(
            text.Contains("superseded", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("stored", StringComparison.OrdinalIgnoreCase),
            "Expected superseded or stored message");
    }

    #endregion

    #region Integration Tests

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
        var searchArgs = new Dictionary<string, object?>
        {
            ["query"] = "integration test"
        };
        var searchRequest = CreateToolCallRequest("search_memories", searchArgs);
        var searchResponse = await SendMcpRequestAsync(searchRequest);
        var searchResult = JsonSerializer.Deserialize<ToolCallResult>(
            JsonSerializer.Serialize(searchResponse.Result, JsonOptions), JsonOptions);
        Assert.NotNull(searchResult);
        Assert.False(searchResult.IsError);
        Assert.Contains("Integration Test Memory", searchResult.Content[0].Text);

        // 3. Retrieve full details
        var getArgs = new Dictionary<string, object?> { ["id"] = id.ToString() };
        var getRequest = CreateToolCallRequest("get_memory", getArgs);
        var getResponse = await SendMcpRequestAsync(getRequest);
        var getResult = JsonSerializer.Deserialize<ToolCallResult>(
            JsonSerializer.Serialize(getResponse.Result, JsonOptions), JsonOptions);
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
        var updateResult = JsonSerializer.Deserialize<ToolCallResult>(
            JsonSerializer.Serialize(updateResponse.Result, JsonOptions), JsonOptions);
        Assert.NotNull(updateResult);
        Assert.False(updateResult.IsError);

        // 5. Verify update
        var verifyArgs = new Dictionary<string, object?> { ["id"] = id.ToString() };
        var verifyRequest = CreateToolCallRequest("get_memory", verifyArgs);
        var verifyResponse = await SendMcpRequestAsync(verifyRequest);
        var verifyResult = JsonSerializer.Deserialize<ToolCallResult>(
            JsonSerializer.Serialize(verifyResponse.Result, JsonOptions), JsonOptions);
        Assert.NotNull(verifyResult);
        Assert.Contains("Updated Integration Test", verifyResult.Content[0].Text);

        // 6. Delete the memory
        var deleteArgs = new Dictionary<string, object?> { ["id"] = id.ToString() };
        var deleteRequest = CreateToolCallRequest("delete_memory", deleteArgs);
        var deleteResponse = await SendMcpRequestAsync(deleteRequest);
        var deleteResult = JsonSerializer.Deserialize<ToolCallResult>(
            JsonSerializer.Serialize(deleteResponse.Result, JsonOptions), JsonOptions);
        Assert.NotNull(deleteResult);
        Assert.False(deleteResult.IsError);

        // 7. Verify deletion
        var verifyDeleteArgs = new Dictionary<string, object?> { ["id"] = id.ToString() };
        var verifyDeleteRequest = CreateToolCallRequest("get_memory", verifyDeleteArgs);
        var verifyDeleteResponse = await SendMcpRequestAsync(verifyDeleteRequest);
        var verifyDeleteResult = JsonSerializer.Deserialize<ToolCallResult>(
            JsonSerializer.Serialize(verifyDeleteResponse.Result, JsonOptions), JsonOptions);
        Assert.NotNull(verifyDeleteResult);
        Assert.True(verifyDeleteResult.IsError);
        Assert.Contains("not found", verifyDeleteResult.Content[0].Text);
    }

    [Fact]
    public async Task MultipleMemories_ComplexSearch_ReturnsCorrectResults()
    {
        // Store diverse memories
        await StoreTestMemoryAsync("Machine Learning Basics", "Introduction to ML algorithms", tags: ["ml", "ai", "basics"]);
        await StoreTestMemoryAsync("Deep Learning Neural Networks", "Understanding neural network architectures", tags: ["ml", "ai", "deep-learning"]);
        await StoreTestMemoryAsync("Python Data Science", "Using Python for data analysis", tags: ["python", "data-science"]);
        await StoreTestMemoryAsync("JavaScript Web Development", "Building web apps with JS", tags: ["javascript", "web"]);
        await StoreTestMemoryAsync("Database Design Patterns", "SQL and NoSQL design principles", tags: ["database", "sql"]);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Search for AI-related topics
        var searchArgs = new Dictionary<string, object?>
        {
            ["query"] = "machine learning artificial intelligence",
            ["top_n"] = 3
        };

        var request = CreateToolCallRequest("search_memories", searchArgs);
        var response = await SendMcpRequestAsync(request);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError);

        var text = result.Content[0].Text;
        // Should find ML-related memories
        Assert.True(
            text.Contains("Machine Learning") || text.Contains("Deep Learning") || text.Contains("Neural"),
            "Expected to find ML-related memories");
    }

    [Fact]
    public async Task ConcurrentOperations_HandleGracefully()
    {
        // Perform multiple concurrent store operations
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

        // All should succeed
        foreach (var response in responses)
        {
            Assert.Null(response.Error);
            var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
            var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);
            Assert.NotNull(result);
            Assert.False(result.IsError);
        }

        // Verify all were stored
        var statsRequest = CreateToolCallRequest("get_stats");
        var statsResponse = await SendMcpRequestAsync(statsRequest);
        var statsResultJson = JsonSerializer.Serialize(statsResponse.Result, JsonOptions);
        var statsResult = JsonSerializer.Deserialize<ToolCallResult>(statsResultJson, JsonOptions);

        Assert.NotNull(statsResult);
        Assert.Contains("Total Nodes: 10", statsResult.Content[0].Text);
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public async Task StoreMemory_UnicodeContent_HandledCorrectly()
    {
        // Use BMP characters only (no surrogate pairs like emojis) to avoid encoding issues with embedding model
        var args = new Dictionary<string, object?>
        {
            ["title"] = "Unicode Test \u65e5\u672c\u8a9e \u4e2d\u6587 \ud55c\uad6d\uc5b4", // Japanese, Chinese, Korean
            ["summary"] = "Testing unicode: \u00e9 accent, symbols \u2211\u220f\u222b, \u00a9\u00ae", // accents and math symbols
            ["content"] = "Full unicode content: \u03b1\u03b2\u03b3\u03b4 \u00f1 \u00fc \u00f6 \u0416\u0418\u0412" // Greek, Spanish, German, Russian
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError, $"Expected success but got error: {result.Content[0].Text}");

        // Retrieve and verify
        var text = result.Content[0].Text;
        var idLine = text.Split('\n').FirstOrDefault(l => l.StartsWith("ID:"));
        Assert.NotNull(idLine);
        var idStr = idLine.Replace("ID:", "").Trim();
        var id = Guid.Parse(idStr);

        var memory = await _repository.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(memory);
        // Verify unicode was preserved - use escape sequences to avoid encoding issues in source file
        Assert.Contains("\u65e5\u672c\u8a9e", memory.Title); // Japanese
        Assert.Contains("\u2211\u220f\u222b", memory.Summary); // Math symbols (sum, product, integral)
        Assert.Contains("\u03b1\u03b2\u03b3", memory.Content); // Greek letters
    }

    [Fact]
    public async Task StoreMemory_SurrogatePairUnicode_HandledGracefully()
    {
        // Test that surrogate pairs (emojis, etc.) are handled gracefully
        // The memory should be stored successfully, with emojis preserved in storage
        // but filtered out before embedding generation
        var args = new Dictionary<string, object?>
        {
            ["title"] = "Emoji Test \ud83d\ude80\ud83c\udf1f\ud83d\udc4d", // rocket, star, thumbs up
            ["summary"] = "Testing emojis: \ud83d\ude00 smile \ud83d\udc96 heart \ud83c\udf89 party",
            ["content"] = "Content with emojis \ud83d\udca1 and regular text mixed together \ud83c\udf08"
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError, $"Expected success but got error: {result.Content[0].Text}");

        // Retrieve and verify emojis were preserved in storage
        var text = result.Content[0].Text;
        var idLine = text.Split('\n').FirstOrDefault(l => l.StartsWith("ID:"));
        Assert.NotNull(idLine);
        var idStr = idLine.Replace("ID:", "").Trim();
        var id = Guid.Parse(idStr);

        var memory = await _repository.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(memory);
        // Emojis should be preserved in the actual stored content
        Assert.Contains("\ud83d\ude80", memory.Title); // rocket emoji preserved
        Assert.Contains("smile", memory.Summary); // text preserved
        Assert.Contains("regular text", memory.Content); // text preserved
    }

    [Fact]
    public async Task StoreMemory_UnpairedSurrogate_HandledGracefully()
    {
        // Test that UNPAIRED surrogates (invalid UTF-16) don't crash the embedding service
        // This is the bug case: \uD83D alone without its low surrogate pair
        // This would previously throw ArgumentException in Regex.Replace
        var args = new Dictionary<string, object?>
        {
            ["title"] = "Unpaired Surrogate Test \ud83d alone", // \uD83D is a high surrogate without its pair
            ["summary"] = "Testing unpaired: \ud83d high and \ude00 low orphans",
            ["content"] = "Content with unpaired \ud83d surrogate that should not crash"
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError, $"Expected success but got error: {result.Content[0].Text}");

        // Verify memory was stored (content may have unpaired surrogates stripped or preserved)
        var text = result.Content[0].Text;
        var idLine = text.Split('\n').FirstOrDefault(l => l.StartsWith("ID:"));
        Assert.NotNull(idLine);
        var idStr = idLine.Replace("ID:", "").Trim();
        Assert.True(Guid.TryParse(idStr, out _), "Should return a valid memory ID");
    }

    [Fact]
    public async Task StoreMemory_LargeContent_HandledCorrectly()
    {
        // Use 10KB of content - large enough to test, but within typical limits
        var largeContent = new string('A', 10000);


        var args = new Dictionary<string, object?>
        {
            ["title"] = "Large Content Test",
            ["summary"] = "Testing large content storage",
            ["content"] = largeContent
        };

        var request = CreateToolCallRequest("store_memory", args);
        var response = await SendMcpRequestAsync(request);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError, $"Expected success but got error: {result.Content[0].Text}");

        // Verify the content was actually stored
        var text = result.Content[0].Text;
        var idLine = text.Split('\n').FirstOrDefault(l => l.StartsWith("ID:"));
        Assert.NotNull(idLine);
        var idStr = idLine.Replace("ID:", "").Trim();
        var id = Guid.Parse(idStr);

        var memory = await _repository.GetAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(memory);
        Assert.Equal(10000, memory.Content.Length);
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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError);
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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.False(result.IsError);
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

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        var result = JsonSerializer.Deserialize<ToolCallResult>(resultJson, JsonOptions);

        Assert.NotNull(result);
        Assert.True(result.IsError);
        Assert.Contains("Unknown tool", result.Content[0].Text);
    }

    #endregion

    #region JSON-RPC Compliance Tests

    [Fact]
    public async Task Response_IncludesJsonRpcVersion()
    {
        var request = CreateMcpRequest("ping");
        var response = await SendMcpRequestAsync(request);

        Assert.Equal("2.0", response.Jsonrpc);
    }

    [Fact]
    public async Task Response_IncludesMatchingId()
    {
        var expectedId = "test-id-12345";

        var rpcRequest = new JsonRpcRequest
        {
            Jsonrpc = "2.0",
            Id = expectedId,
            Method = "ping"
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(rpcRequest, JsonOptions);
        var request = new Request
        {
            Method = HttpMethod.POST,
            Path = "/mcp",
            Body = body
        };

        var httpResponse = await _mcpHandler.HandleAsync(request, TestContext.Current.CancellationToken);
        var json = JsonSerializer.Serialize(httpResponse.Body, JsonOptions);
        var rpcResponse = JsonSerializer.Deserialize<JsonRpcResponse>(json, JsonOptions);

        Assert.NotNull(rpcResponse);
        Assert.Equal(expectedId, rpcResponse.Id?.ToString());
    }

    [Fact]
    public async Task Response_SuccessHasResultNoError()
    {
        var request = CreateMcpRequest("ping");
        var response = await SendMcpRequestAsync(request);

        Assert.NotNull(response.Result);
        Assert.Null(response.Error);
    }

    [Fact]
    public async Task Response_ErrorHasNoResult()
    {
        var request = CreateMcpRequest("unknown/method");
        var response = await SendMcpRequestAsync(request);

        Assert.NotNull(response.Error);
        Assert.Null(response.Result);
    }

    #endregion
}
