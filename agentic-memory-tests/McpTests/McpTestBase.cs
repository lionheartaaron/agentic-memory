using System.Text.Json;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Http.Mcp;
using AgenticMemory.Http.Models;
using AgenticMemoryTests.Shared;
using Microsoft.Extensions.Logging;
using HttpMethod = AgenticMemory.Http.Models.HttpMethod;

namespace AgenticMemoryTests.McpTests;

/// <summary>
/// Base class for MCP protocol tests. Provides common infrastructure for
/// testing the MCP handler including session management and helper methods.
/// </summary>
public abstract class McpTestBase : IAsyncLifetime
{
    protected TestFixture Fixture { get; private set; } = null!;
    protected McpHandler McpHandler { get; private set; } = null!;
    protected IMemoryRepository Repository => Fixture.Repository;
    protected string? SessionId { get; set; }

    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async ValueTask InitializeAsync()
    {
        Fixture = new TestFixture();
        await Fixture.InitializeAsync();

        // Initialize MCP handler with all services
        McpHandler = new McpHandler(
            Fixture.Repository,
            Fixture.SearchService,
            Fixture.ConflictStorage,
            null, // Use default storage settings
            Fixture.LoggerFactory.CreateLogger<McpHandler>());

        // Initialize MCP session (required for 2025-03-26 protocol)
        await InitializeSessionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Fixture.DisposeAsync();
    }

    #region Helper Methods

    protected async Task InitializeSessionAsync()
    {
        var request = CreateMcpRequest("initialize");
        var response = await McpHandler.HandleAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(200, response.StatusCode);

        // Capture session ID from response
        if (response.Headers.TryGetValue("Mcp-Session-Id", out var sessionId))
        {
            SessionId = sessionId;
        }

        Assert.False(string.IsNullOrEmpty(SessionId), "Session ID should be returned from initialize");
    }

    protected Request CreateMcpRequest(string method, object? @params = null)
    {
        var rpcRequest = new JsonRpcRequest
        {
            Jsonrpc = "2.0",
            Id = Guid.NewGuid().ToString(),
            Method = method,
            Params = @params as Dictionary<string, object?>
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(rpcRequest, JsonOptions);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json"
        };

        // Add session header for non-initialize requests (required by 2025-03-26 spec)
        if (method != "initialize" && !string.IsNullOrEmpty(SessionId))
        {
            headers["Mcp-Session-Id"] = SessionId;
        }

        return new Request
        {
            Method = HttpMethod.POST,
            Path = "/mcp",
            Headers = headers,
            Body = body
        };
    }

    protected Request CreateToolCallRequest(string toolName, Dictionary<string, object?>? arguments = null)
    {
        var @params = new Dictionary<string, object?>
        {
            ["name"] = toolName,
            ["arguments"] = arguments ?? new Dictionary<string, object?>()
        };

        return CreateMcpRequest("tools/call", @params);
    }

    protected async Task<JsonRpcResponse> SendMcpRequestAsync(Request request)
    {
        var response = await McpHandler.HandleAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(200, response.StatusCode);

        // Capture session ID from response headers (for initialize and subsequent requests)
        if (response.Headers.TryGetValue("Mcp-Session-Id", out var sessionId))
        {
            SessionId = sessionId;
        }

        var json = JsonSerializer.Serialize(response.Body, JsonOptions);
        var rpcResponse = JsonSerializer.Deserialize<JsonRpcResponse>(json, JsonOptions);
        Assert.NotNull(rpcResponse);
        return rpcResponse;
    }

    protected async Task<Guid> StoreTestMemoryAsync(string title, string summary, string? content = null, List<string>? tags = null)
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

    protected static T? DeserializeResult<T>(object? result)
    {
        if (result is null) return default;
        var json = JsonSerializer.Serialize(result, JsonOptions);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    #endregion
}
