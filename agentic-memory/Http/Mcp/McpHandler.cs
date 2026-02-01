using System.Collections.Concurrent;
using System.Text.Json;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Configuration;
using AgenticMemory.Http.Handlers;
using AgenticMemory.Http.Models;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Http.Mcp;

/// <summary>
/// MCP (Model Context Protocol) handler for AI agent integration
/// Implements JSON-RPC 2.0 over Streamable HTTP per specification 2025-03-26
/// Supports Visual Studio's strict Streamable HTTP requirements including session management
/// </summary>
public partial class McpHandler : IHandler
{
    private readonly IMemoryRepository? _repository;
    private readonly ISearchService? _searchService;
    private readonly IConflictAwareStorage? _conflictStorage;
    private readonly StorageSettings _storageSettings;
    private readonly ILogger<McpHandler>? _logger;

    /// <summary>
    /// Session management for MCP Streamable HTTP protocol (2025-03-26)
    /// Key: Session ID, Value: Session state with last access time
    /// </summary>
    private static readonly ConcurrentDictionary<string, SessionState> Sessions = new();
    
    /// <summary>
    /// Session timeout for cleanup (1 hour)
    /// </summary>
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromHours(1);

    /// <summary>
    /// Allowed origins for DNS rebinding protection (null = allow all for local dev)
    /// </summary>
    private readonly HashSet<string>? _allowedOrigins;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    /// <summary>
    /// Pretty-print JSON options for logging
    /// </summary>
    private static readonly JsonSerializerOptions JsonPrettyOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public McpHandler(
        IMemoryRepository? repository = null,
        ISearchService? searchService = null,
        IConflictAwareStorage? conflictStorage = null,
        StorageSettings? storageSettings = null,
        ILogger<McpHandler>? logger = null,
        HashSet<string>? allowedOrigins = null)
    {
        _repository = repository;
        _searchService = searchService;
        _conflictStorage = conflictStorage;
        _storageSettings = storageSettings ?? new StorageSettings();
        _logger = logger;
        _allowedOrigins = allowedOrigins;
    }

    public async Task<Response> HandleAsync(Request request, CancellationToken cancellationToken)
    {
        // Periodic session cleanup (non-blocking)
        CleanupExpiredSessions();

        // Log incoming request details
        LogIncomingRequest(request);

        // Security: Validate Origin header for DNS rebinding protection (2025-03-26 spec)
        if (!ValidateOrigin(request))
        {
            _logger?.LogWarning("[MCP] Request rejected: invalid Origin header '{Origin}'", 
                request.GetHeader("Origin") ?? "(none)");
            return Response.Forbidden("Invalid Origin header");
        }

        // Route based on HTTP method per 2025-03-26 spec
        var response = request.Method switch
        {
            Models.HttpMethod.POST => await HandlePostAsync(request, cancellationToken),
            Models.HttpMethod.GET => HandleGet(request),
            Models.HttpMethod.DELETE => HandleDelete(request),
            _ => Response.MethodNotAllowed("MCP endpoint supports POST, GET, and DELETE methods")
        };

        // Log outgoing response
        LogOutgoingResponse(request.Method, response);

        return response;
    }

    /// <summary>
    /// Log details about incoming MCP request
    /// </summary>
    private void LogIncomingRequest(Request request)
    {
        if (_logger is null) return;

        _logger.LogInformation("[MCP] --> Incoming {Method} request", request.Method);
        
        // Log headers at debug level
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var sessionId = request.GetHeader("Mcp-Session-Id");
            var contentType = request.GetHeader("Content-Type");
            var accept = request.GetHeader("Accept");
            var origin = request.GetHeader("Origin");
            var contentLength = request.GetHeader("Content-Length");
            var transferEncoding = request.GetHeader("Transfer-Encoding");
            
            _logger.LogDebug("[MCP] Headers: Session={SessionId}, Content-Type={ContentType}, Accept={Accept}, Origin={Origin}, Content-Length={ContentLength}, Transfer-Encoding={TransferEncoding}",
                sessionId ?? "(none)", contentType ?? "(none)", accept ?? "(none)", origin ?? "(none)", 
                contentLength ?? "(none)", transferEncoding ?? "(none)");
            
            // Log ALL headers for deep debugging
            _logger.LogDebug("[MCP] All headers: {Headers}", 
                string.Join(", ", request.Headers.Select(h => $"{h.Key}={h.Value}")));
        }

        // Log request body at debug level with pretty-print
        if (request.Body is not null && request.Body.Length > 0 && _logger.IsEnabled(LogLevel.Debug))
        {
            try
            {
                var rawBody = System.Text.Encoding.UTF8.GetString(request.Body);
                
                // Try to pretty-print JSON
                var jsonDoc = JsonDocument.Parse(rawBody);
                var prettyJson = JsonSerializer.Serialize(jsonDoc, JsonPrettyOptions);
                
                _logger.LogDebug("[MCP] Request Body ({Length} bytes):\n{Body}", request.Body.Length, prettyJson);
            }
            catch
            {
                // If not valid JSON, log raw
                var rawBody = System.Text.Encoding.UTF8.GetString(request.Body);
                _logger.LogDebug("[MCP] Request Body (raw, {Length} bytes): {Body}", request.Body.Length, rawBody);
            }
        }
    }

    /// <summary>
    /// Log details about outgoing MCP response
    /// </summary>
    private void LogOutgoingResponse(Models.HttpMethod method, Response response)
    {
        if (_logger is null) return;

        var statusIndicator = response.StatusCode switch
        {
            >= 200 and < 300 => "OK",
            >= 400 and < 500 => "WARN",
            >= 500 => "ERROR",
            _ => "OUT"
        };

        _logger.LogInformation("[MCP] [{Status}] Response: {StatusCode} {ReasonPhrase}", 
        statusIndicator, response.StatusCode, response.ReasonPhrase);

        // Log session header if present
        if (response.Headers.TryGetValue("Mcp-Session-Id", out var sessionId))
        {
            _logger.LogDebug("[MCP] Response Session-Id: {SessionId}", sessionId);
        }

        // Log response body at debug level with pretty-print
        if (response.Body is not null && _logger.IsEnabled(LogLevel.Debug))
        {
            try
            {
                string prettyJson;
                if (response.Body is JsonRpcResponse rpcResponse)
                {
                    prettyJson = JsonSerializer.Serialize(rpcResponse, JsonPrettyOptions);
                }
                else if (response.Body is List<JsonRpcResponse> rpcResponses)
                {
                    prettyJson = JsonSerializer.Serialize(rpcResponses, JsonPrettyOptions);
                }
                else
                {
                    prettyJson = JsonSerializer.Serialize(response.Body, JsonPrettyOptions);
                }
                
                _logger.LogDebug("[MCP] Response Body:\n{Body}", prettyJson);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[MCP] Response Body: (could not serialize: {Error})", ex.Message);
            }
        }
    }

    /// <summary>
    /// Handle POST requests - main JSON-RPC message handler (2025-03-26 spec)
    /// </summary>
    private async Task<Response> HandlePostAsync(Request request, CancellationToken cancellationToken)
    {
        try
        {
            // Parse the JSON-RPC message(s) - can be single or batched
            var (messages, isBatch) = ParseJsonRpcMessages(request.Body);
            
            if (messages is null || messages.Count == 0)
            {
                var rawBody = request.Body is not null && request.Body.Length > 0
                    ? System.Text.Encoding.UTF8.GetString(request.Body)
                    : "(empty)";
                _logger?.LogWarning("[MCP] Request body could not be parsed as JSON-RPC. Raw body ({Length} bytes): {Body}", 
                    request.Body?.Length ?? 0, rawBody);
                return CreateErrorResponse(null, -32700, "Parse error: Invalid JSON", null);
            }

            _logger?.LogDebug("[MCP] Parsed {Count} message(s), isBatch={IsBatch}", messages.Count, isBatch);

            // Check if this is an initialization request
            var isInitRequest = messages.Any(m => m.Method == "initialize" && m.Id is not null);
            
            // Session management per 2025-03-26 spec
            string? sessionId = null;
            if (isInitRequest)
            {
                // Initialize creates a new session - don't require existing session
                sessionId = CreateNewSession();
                _logger?.LogInformation("[MCP] New session created: {SessionId}", sessionId);
            }
            else
            {
                // All other requests require a valid session
                sessionId = request.GetHeader("Mcp-Session-Id");
                if (string.IsNullOrEmpty(sessionId))
                {
                    _logger?.LogWarning("[MCP] Request missing Mcp-Session-Id header");
                    return Response.BadRequest("Missing Mcp-Session-Id header");
                }
                
                if (!Sessions.TryGetValue(sessionId, out var session))
                {
                    _logger?.LogWarning("[MCP] Session not found or expired: {SessionId}", sessionId);
                    return Response.NotFound("Session not found or expired");
                }
                
                // Update last access time
                session.LastAccessTime = DateTime.UtcNow;
                _logger?.LogDebug("[MCP] Session validated: {SessionId}", sessionId);
            }

            // Classify messages: requests have id, notifications/responses don't need response
            var requests = messages.Where(m => m.Id is not null && !string.IsNullOrEmpty(m.Method)).ToList();
            var notifications = messages.Where(m => m.Id is null && !string.IsNullOrEmpty(m.Method)).ToList();
            var responses = messages.Where(m => m.Id is not null && string.IsNullOrEmpty(m.Method)).ToList();

            _logger?.LogDebug("[MCP] Message classification: {RequestCount} requests, {NotificationCount} notifications, {ResponseCount} responses",
                requests.Count, notifications.Count, responses.Count);

            // Per spec: If input consists solely of notifications or responses, return 202 Accepted
            if (requests.Count == 0)
            {
                _logger?.LogDebug("[MCP] No requests in message, processing notifications only");
                
                // Process notifications (fire and forget)
                foreach (var notification in notifications)
                {
                    await ProcessNotificationAsync(notification, sessionId, cancellationToken);
                }
                
                var acceptedResponse = Response.Accepted();
                if (!string.IsNullOrEmpty(sessionId))
                {
                    acceptedResponse.Headers["Mcp-Session-Id"] = sessionId;
                }
                return acceptedResponse;
            }

            // Process requests and build responses
            var jsonRpcResponses = new List<JsonRpcResponse>();
            foreach (var rpcRequest in requests)
            {
                _logger?.LogInformation("[MCP] Processing request: method={Method}, id={Id}", 
                rpcRequest.Method, NormalizeId(rpcRequest.Id));
                
                var response = await ProcessRequestAsync(rpcRequest, sessionId, cancellationToken);
                jsonRpcResponses.Add(response);
                
                // Log result
                if (response.Error is not null)
                {
                    _logger?.LogWarning("[MCP] Response error: code={Code}, message={Message}", 
                    response.Error.Code, response.Error.Message);
                }
                else
                {
                    _logger?.LogInformation("[MCP] Response success for id={Id}", response.Id);
                }
            }

            // Also process any notifications in the batch
            foreach (var notification in notifications)
            {
                await ProcessNotificationAsync(notification, sessionId, cancellationToken);
            }

            // Return response(s) with Content-Type: application/json
            Response httpResponse;
            if (isBatch || jsonRpcResponses.Count > 1)
            {
                // Return as array for batched requests
                httpResponse = Response.Ok(jsonRpcResponses);
            }
            else
            {
                // Return single response
                httpResponse = Response.Ok(jsonRpcResponses[0]);
            }

            // Add session header
            if (!string.IsNullOrEmpty(sessionId))
            {
                httpResponse.Headers["Mcp-Session-Id"] = sessionId;
            }

            return httpResponse;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "[MCP] JSON parse error: {Message}", ex.Message);
            return CreateErrorResponse(null, -32700, "Parse error: " + ex.Message, null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[MCP] Internal error: {Message}", ex.Message);
            return CreateErrorResponse(null, -32603, "Internal error: " + ex.Message, null);
        }
    }

    /// <summary>
    /// Handle GET requests - SSE stream for server-to-client messages (2025-03-26 spec)
    /// We don't support SSE streaming, so return 405 Method Not Allowed
    /// </summary>
    private Response HandleGet(Request request)
    {
        // Per 2025-03-26 spec: Server MUST return 405 if it doesn't offer SSE stream
        _logger?.LogDebug("[MCP] GET request received - SSE not supported, returning 405");
        return Response.MethodNotAllowed("This server does not support SSE streaming via GET");
    }

    /// <summary>
    /// Handle DELETE requests - session termination (2025-03-26 spec)
    /// </summary>
    private Response HandleDelete(Request request)
    {
        var sessionId = request.GetHeader("Mcp-Session-Id");
        
        if (string.IsNullOrEmpty(sessionId))
        {
            _logger?.LogWarning("[MCP] DELETE request missing Mcp-Session-Id header");
            return Response.BadRequest("Missing Mcp-Session-Id header");
        }

        if (Sessions.TryRemove(sessionId, out _))
        {
            _logger?.LogInformation("[MCP] Session terminated by client: {SessionId}", sessionId);
            return Response.NoContent();
        }

        _logger?.LogWarning("[MCP] DELETE request for unknown session: {SessionId}", sessionId);
        return Response.NotFound("Session not found");
    }

    /// <summary>
    /// Validate Origin header to prevent DNS rebinding attacks (2025-03-26 spec)
    /// </summary>
    private bool ValidateOrigin(Request request)
    {
        // If no allowed origins configured, allow all (for local development)
        if (_allowedOrigins is null || _allowedOrigins.Count == 0)
            return true;

        var origin = request.GetHeader("Origin");
        if (string.IsNullOrEmpty(origin))
        {
            // No Origin header - allow for non-browser clients
            return true;
        }

        return _allowedOrigins.Contains(origin);
    }

    /// <summary>
    /// Parse JSON-RPC messages from request body (single or batch)
    /// </summary>
    private (List<JsonRpcRequest>? messages, bool isBatch) ParseJsonRpcMessages(byte[]? body)
    {
        if (body is null || body.Length == 0)
            return (null, false);

        var json = System.Text.Encoding.UTF8.GetString(body);
        var trimmed = json.TrimStart();

        if (trimmed.StartsWith('['))
        {
            // Batch request
            var batch = JsonSerializer.Deserialize<List<JsonRpcRequest>>(body, JsonOptions);
            return (batch, true);
        }
        else
        {
            // Single request
            var single = JsonSerializer.Deserialize<JsonRpcRequest>(body, JsonOptions);
            return (single is not null ? [single] : null, false);
        }
    }

    /// <summary>
    /// Process a JSON-RPC notification (no response expected)
    /// </summary>
    private Task ProcessNotificationAsync(JsonRpcRequest notification, string? sessionId, CancellationToken cancellationToken)
    {
        _logger?.LogInformation("[MCP] Notification received: {Method}", notification.Method);
        
        // Handle known notifications
        switch (notification.Method)
        {
            case "initialized":
                // Client has completed initialization
                _logger?.LogInformation("[MCP] Client initialization complete, session: {SessionId}", sessionId);
                break;
            case "notifications/cancelled":
                // Client cancelled a request
                _logger?.LogInformation("[MCP] Request cancelled by client");
                break;
            default:
                _logger?.LogDebug("[MCP] Unknown notification: {Method}", notification.Method);
                break;
        }
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Process a JSON-RPC request and return a response
    /// </summary>
    private async Task<JsonRpcResponse> ProcessRequestAsync(JsonRpcRequest request, string? sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var response = request.Method switch
            {
                "initialize" => HandleInitialize(request, sessionId),
                "tools/list" => HandleToolsList(request),
                "tools/call" => await HandleToolsCallAsync(request, cancellationToken),
                "resources/list" => await HandleResourcesListAsync(request, cancellationToken),
                "resources/read" => await HandleResourcesReadAsync(request, cancellationToken),
                "resources/templates/list" => HandleResourcesTemplatesList(request),
                "prompts/list" => HandlePromptsList(request),
                "ping" => HandlePing(request),
                _ => CreateJsonRpcErrorResponse(request.Id, -32601, $"Method not found: {request.Method}")
            };

            return response;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[MCP] Error processing request '{Method}': {Message}", request.Method, ex.Message);
            return CreateJsonRpcErrorResponse(request.Id, -32603, $"Internal error: {ex.Message}");
        }
    }

    /// <summary>
    /// Create a new session ID (2025-03-26 spec: globally unique, cryptographically secure)
    /// </summary>
    private string CreateNewSession()
    {
        var sessionId = Guid.NewGuid().ToString();
        Sessions[sessionId] = new SessionState { LastAccessTime = DateTime.UtcNow };
        _logger?.LogDebug("[MCP] Session storage now contains {Count} active session(s)", Sessions.Count);
        return sessionId;
    }

    /// <summary>
    /// Remove expired sessions (older than SessionTimeout)
    /// </summary>
    private void CleanupExpiredSessions()
    {
        var cutoff = DateTime.UtcNow - SessionTimeout;
        var expiredSessions = Sessions.Where(kvp => kvp.Value.LastAccessTime < cutoff).Select(kvp => kvp.Key).ToList();
        
        foreach (var sessionId in expiredSessions)
        {
            if (Sessions.TryRemove(sessionId, out _))
            {
                _logger?.LogDebug("Removed expired MCP session: {SessionId}", sessionId);
            }
        }
    }

    private static string? GetStringArg(Dictionary<string, object?>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out var value))
            return null;
        
        if (value is JsonElement je && je.ValueKind == JsonValueKind.String)
            return je.GetString();
        
        return value?.ToString();
    }

    private static int? GetIntArg(Dictionary<string, object?>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out var value))
            return null;

        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number)
                return je.GetInt32();
            if (je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var parsed))
                return parsed;
        }


        if (value is int i)
            return i;

        if (int.TryParse(value?.ToString(), out var result))
            return result;

        return null;
    }

    private static double? GetDoubleArg(Dictionary<string, object?>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out var value))
            return null;

        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number)
                return je.GetDouble();
            if (je.ValueKind == JsonValueKind.String && double.TryParse(je.GetString(), out var parsed))
                return parsed;
        }

        if (value is double d)
            return d;

        if (double.TryParse(value?.ToString(), out var result))
            return result;

        return null;
    }

    private static bool? GetBoolArg(Dictionary<string, object?>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out var value))
            return null;

        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.True)
                return true;
            if (je.ValueKind == JsonValueKind.False)
                return false;
            if (je.ValueKind == JsonValueKind.String && bool.TryParse(je.GetString(), out var parsed))
                return parsed;
        }

        if (value is bool b)
            return b;

        if (bool.TryParse(value?.ToString(), out var boolResult))
            return boolResult;

        return null;
    }

    private static List<string>? GetStringArrayArg(Dictionary<string, object?>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out var value))
            return null;

        if (value is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            return je.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList();
        }

        if (value is List<string> list)
            return list;

        return null;
    }

    private Response CreateErrorResponse(object? id, int code, string message, string? sessionId)
    {
        var response = CreateJsonRpcErrorResponse(id, code, message);
        var httpResponse = Response.Ok(response);
        
        if (!string.IsNullOrEmpty(sessionId))
        {
            httpResponse.Headers["Mcp-Session-Id"] = sessionId;
        }
        
        return httpResponse;
    }

    private JsonRpcResponse CreateJsonRpcSuccessResponse(object? id, object result)
    {
        return new JsonRpcResponse
        {
            Jsonrpc = "2.0",
            Id = NormalizeId(id),
            Result = result
        };
    }

    private JsonRpcResponse CreateJsonRpcErrorResponse(object? id, int code, string message)
    {
        return new JsonRpcResponse
        {
            Jsonrpc = "2.0",
            Id = NormalizeId(id),
            Error = new JsonRpcError
            {
                Code = code,
                Message = message
            }
        };
    }

    /// <summary>
    /// Normalize JSON-RPC ID from deserialized JsonElement to a primitive type
    /// that will serialize correctly in the response
    /// </summary>
    private static object? NormalizeId(object? id)
    {
        if (id is null)
            return null;

        if (id is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Null => null,
                _ => id // Fallback: keep as-is
            };
        }

        return id;
    }
}

/// <summary>
/// Session state for MCP 2025-03-26 protocol
/// </summary>
internal class SessionState
{
    public DateTime LastAccessTime { get; set; }
}
