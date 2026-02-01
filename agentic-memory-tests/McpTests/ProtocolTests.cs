using System.Text.Json;
using AgenticMemory.Http.Mcp;
using AgenticMemory.Http.Models;
using HttpMethod = AgenticMemory.Http.Models.HttpMethod;

namespace AgenticMemoryTests.McpTests;

/// <summary>
/// Tests for MCP protocol compliance, session management, and JSON-RPC behavior.
/// </summary>
public class ProtocolTests : McpTestBase
{
    #region Initialize Tests

    [Fact]
    public async Task Initialize_ReturnsCorrectProtocolVersion()
    {
        var request = CreateMcpRequest("initialize");
        var response = await SendMcpRequestAsync(request);

        Assert.Null(response.Error);
        Assert.NotNull(response.Result);

        var result = DeserializeResult<InitializeResult>(response.Result);

        Assert.NotNull(result);
        Assert.Equal("2025-03-26", result.ProtocolVersion);
        Assert.Equal("agentic-memory", result.ServerInfo.Name);
        Assert.Equal("1.0.0", result.ServerInfo.Version);
    }

    [Fact]
    public async Task Initialize_ReturnsCapabilities()
    {
        var request = CreateMcpRequest("initialize");
        var response = await SendMcpRequestAsync(request);

        var result = DeserializeResult<InitializeResult>(response.Result);

        Assert.NotNull(result);
        Assert.NotNull(result.Capabilities.Tools);
        Assert.NotNull(result.Capabilities.Resources);
        Assert.False(result.Capabilities.Tools.ListChanged);
        Assert.False(result.Capabilities.Resources.Subscribe);
        Assert.False(result.Capabilities.Resources.ListChanged);
    }

    [Fact]
    public async Task Initialized_Notification_Returns202Accepted()
    {
        // "initialized" is a notification (no id), not a request
        // Per MCP spec, notifications return 202 Accepted
        var rpcNotification = new JsonRpcRequest
        {
            Jsonrpc = "2.0",
            Id = null, // Notifications have no id
            Method = "initialized"
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(rpcNotification, JsonOptions);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json"
        };

        if (!string.IsNullOrEmpty(SessionId))
        {
            headers["Mcp-Session-Id"] = SessionId;
        }

        var request = new Request
        {
            Method = HttpMethod.POST,
            Path = "/mcp",
            Headers = headers,
            Body = body
        };

        var response = await McpHandler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Notifications return 202 Accepted per MCP spec
        Assert.Equal(202, response.StatusCode);
    }

    #endregion

    #region Ping Tests

    [Fact]
    public async Task Ping_ReturnsEmptyResult()
    {
        var request = CreateMcpRequest("ping");
        var response = await SendMcpRequestAsync(request);

        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
    }

    #endregion

    #region Error Handling Tests

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

        var response = await McpHandler.HandleAsync(request, TestContext.Current.CancellationToken);
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

        var response = await McpHandler.HandleAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(200, response.StatusCode);

        var json = JsonSerializer.Serialize(response.Body, JsonOptions);
        var rpcResponse = JsonSerializer.Deserialize<JsonRpcResponse>(json, JsonOptions);

        Assert.NotNull(rpcResponse?.Error);
        Assert.Equal(-32700, rpcResponse.Error.Code);
        Assert.Contains("Parse error", rpcResponse.Error.Message);
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
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json"
        };

        if (!string.IsNullOrEmpty(SessionId))
        {
            headers["Mcp-Session-Id"] = SessionId;
        }

        var request = new Request
        {
            Method = HttpMethod.POST,
            Path = "/mcp",
            Headers = headers,
            Body = body
        };

        var httpResponse = await McpHandler.HandleAsync(request, TestContext.Current.CancellationToken);
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
