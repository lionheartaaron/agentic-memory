using Microsoft.Extensions.Logging;

namespace AgenticMemory.Http.Mcp;

/// <summary>
/// MCP Handler - Session functionality (initialize, ping, prompts/list)
/// </summary>
public partial class McpHandler
{
    private JsonRpcResponse HandleInitialize(JsonRpcRequest request, string? sessionId)
    {
        var result = new InitializeResult
        {
            ProtocolVersion = "2025-03-26",  // Updated to latest spec
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = false },
                Resources = new ResourcesCapability { Subscribe = false, ListChanged = false },
                Prompts = new PromptsCapability { ListChanged = false }
            },
            ServerInfo = new ServerInfo
            {
                Name = "agentic-memory",
                Version = "1.0.0"
            }
        };

        return CreateJsonRpcSuccessResponse(request.Id, result);
    }

    private JsonRpcResponse HandlePing(JsonRpcRequest request)
    {
        return CreateJsonRpcSuccessResponse(request.Id, new { });
    }

    /// <summary>
    /// Handle prompts/list - returns empty list (tools provide all functionality)
    /// </summary>
    private JsonRpcResponse HandlePromptsList(JsonRpcRequest request)
    {
        // We use tools instead of prompts - tools are actually exposed by VS Copilot
        _logger?.LogDebug("[MCP] prompts/list called - returning empty (use tools instead)");
        return CreateJsonRpcSuccessResponse(request.Id, new { prompts = Array.Empty<object>() });
    }
}
