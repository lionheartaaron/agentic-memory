namespace AgenticMemory.Http.Mcp;

/// <summary>
/// JSON-RPC 2.0 request message for MCP
/// </summary>
public class JsonRpcRequest
{
    public string Jsonrpc { get; set; } = "2.0";
    public object? Id { get; set; }
    public string Method { get; set; } = string.Empty;
    public Dictionary<string, object?>? Params { get; set; }
}

/// <summary>
/// JSON-RPC 2.0 response message for MCP
/// </summary>
public class JsonRpcResponse
{
    public string Jsonrpc { get; set; } = "2.0";
    public object? Id { get; set; }
    public object? Result { get; set; }
    public JsonRpcError? Error { get; set; }
}

/// <summary>
/// JSON-RPC 2.0 error object
/// </summary>
public class JsonRpcError
{
    public int Code { get; set; }
    public string Message { get; set; } = string.Empty;
    public object? Data { get; set; }
}

/// <summary>
/// MCP Initialize request params
/// </summary>
public class InitializeParams
{
    public string ProtocolVersion { get; set; } = "2024-11-05";
    public ClientCapabilities? Capabilities { get; set; }
    public ClientInfo? ClientInfo { get; set; }
}

/// <summary>
/// MCP Client capabilities
/// </summary>
public class ClientCapabilities
{
    public Dictionary<string, object>? Roots { get; set; }
    public Dictionary<string, object>? Sampling { get; set; }
}

/// <summary>
/// MCP Client info
/// </summary>
public class ClientInfo
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// MCP Initialize result
/// </summary>
public class InitializeResult
{
    public string ProtocolVersion { get; set; } = "2024-11-05";
    public ServerCapabilities Capabilities { get; set; } = new();
    public ServerInfo ServerInfo { get; set; } = new();
}

/// <summary>
/// MCP Server capabilities
/// </summary>
public class ServerCapabilities
{
    public ToolsCapability? Tools { get; set; }
    public ResourcesCapability? Resources { get; set; }
}

/// <summary>
/// MCP Tools capability
/// </summary>
public class ToolsCapability
{
    public bool ListChanged { get; set; } = false;
}

/// <summary>
/// MCP Resources capability
/// </summary>
public class ResourcesCapability
{
    public bool Subscribe { get; set; } = false;
    public bool ListChanged { get; set; } = false;
}

/// <summary>
/// MCP Server info
/// </summary>
public class ServerInfo
{
    public string Name { get; set; } = "agentic-memory";
    public string Version { get; set; } = "1.0.0";
}

/// <summary>
/// MCP Tool definition
/// </summary>
public class ToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ToolInputSchema InputSchema { get; set; } = new();
}

/// <summary>
/// MCP Tool input schema
/// </summary>
public class ToolInputSchema
{
    public string Type { get; set; } = "object";
    public Dictionary<string, PropertySchema>? Properties { get; set; }
    public List<string>? Required { get; set; }
}

/// <summary>
/// JSON Schema property definition
/// </summary>
public class PropertySchema
{
    public string Type { get; set; } = "string";
    public string? Description { get; set; }
    public object? Default { get; set; }
    public string? Format { get; set; }
    public PropertySchema? Items { get; set; }
}

/// <summary>
/// MCP Tools/call request params
/// </summary>
public class ToolCallParams
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, object?>? Arguments { get; set; }
}

/// <summary>
/// MCP Tool call result
/// </summary>
public class ToolCallResult
{
    public List<ToolContent> Content { get; set; } = [];
    public bool IsError { get; set; } = false;
}

/// <summary>
/// MCP Tool content item
/// </summary>
public class ToolContent
{
    public string Type { get; set; } = "text";
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// MCP Resource definition
/// </summary>
public class ResourceDefinition
{
    public string Uri { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? MimeType { get; set; }
}

/// <summary>
/// MCP Resource content
/// </summary>
public class ResourceContent
{
    public string Uri { get; set; } = string.Empty;
    public string? MimeType { get; set; }
    public string? Text { get; set; }
}

/// <summary>
/// MCP Resources/read params
/// </summary>
public class ResourceReadParams
{
    public string Uri { get; set; } = string.Empty;
}

/// <summary>
/// MCP Resources/read result
/// </summary>
public class ResourceReadResult
{
    public List<ResourceContent> Contents { get; set; } = [];
}
