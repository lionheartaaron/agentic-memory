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
/// Implements JSON-RPC 2.0 over HTTP for tool and resource access
/// </summary>
public class McpHandler : IHandler
{
    private readonly IMemoryRepository? _repository;
    private readonly ISearchService? _searchService;
    private readonly IConflictAwareStorage? _conflictStorage;
    private readonly StorageSettings _storageSettings;
    private readonly ILogger<McpHandler>? _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public McpHandler(
        IMemoryRepository? repository = null,
        ISearchService? searchService = null,
        IConflictAwareStorage? conflictStorage = null,
        StorageSettings? storageSettings = null,
        ILogger<McpHandler>? logger = null)
    {
        _repository = repository;
        _searchService = searchService;
        _conflictStorage = conflictStorage;
        _storageSettings = storageSettings ?? new StorageSettings();
        _logger = logger;
    }

    public async Task<Response> HandleAsync(Request request, CancellationToken cancellationToken)
    {
        if (request.Method != Models.HttpMethod.POST)
        {
            return Response.MethodNotAllowed("MCP requires POST requests");
        }

        try
        {
            var rpcRequest = request.GetBodyAs<JsonRpcRequest>();
            if (rpcRequest is null)
            {
                return CreateErrorResponse(null, -32700, "Parse error: Invalid JSON");
            }

            _logger?.LogDebug("MCP request: {Method} (id: {Id})", rpcRequest.Method, rpcRequest.Id);

            var response = rpcRequest.Method switch
            {
                "initialize" => HandleInitialize(rpcRequest),
                "initialized" => HandleInitialized(rpcRequest),
                "tools/list" => HandleToolsList(rpcRequest),
                "tools/call" => await HandleToolsCallAsync(rpcRequest, cancellationToken),
                "resources/list" => await HandleResourcesListAsync(rpcRequest, cancellationToken),
                "resources/read" => await HandleResourcesReadAsync(rpcRequest, cancellationToken),
                "ping" => HandlePing(rpcRequest),
                _ => CreateErrorResponse(rpcRequest.Id, -32601, $"Method not found: {rpcRequest.Method}")
            };

            return response;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "MCP JSON parse error");
            return CreateErrorResponse(null, -32700, "Parse error: " + ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "MCP internal error");
            return CreateErrorResponse(null, -32603, "Internal error: " + ex.Message);
        }
    }

    private Response HandleInitialize(JsonRpcRequest request)
    {
        var result = new InitializeResult
        {
            ProtocolVersion = "2024-11-05",
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = false },
                Resources = new ResourcesCapability { Subscribe = false, ListChanged = false }
            },
            ServerInfo = new ServerInfo
            {
                Name = "agentic-memory",
                Version = "1.0.0"
            }
        };

        return CreateSuccessResponse(request.Id, result);
    }

    private Response HandleInitialized(JsonRpcRequest request)
    {
        // Notification - no response needed, but we return success for HTTP
        return CreateSuccessResponse(request.Id, new { });
    }

    private Response HandlePing(JsonRpcRequest request)
    {
        return CreateSuccessResponse(request.Id, new { });
    }

    private Response HandleToolsList(JsonRpcRequest request)
    {
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "search_memories",
                Description = "Search for memories using semantic vector similarity and fuzzy text matching. Use this FIRST before storing new memories to check if related information already exists. Returns memories ranked by relevance score.",
                InputSchema = new ToolInputSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, PropertySchema>
                    {
                        ["query"] = new() { Type = "string", Description = "Natural language search query. Can include concepts, keywords, or questions." },
                        ["top_n"] = new() { Type = "integer", Description = "Maximum number of results to return (1-100)", Default = 5 },
                        ["tags"] = new() { Type = "array", Description = "Optional: Filter results to only memories containing these tags", Items = new PropertySchema { Type = "string" } }
                    },
                    Required = ["query"]
                }
            },
            new()
            {
                Name = "store_memory",
                Description = "Store a new memory with automatic conflict resolution. Duplicates are detected and reinforced. Singular-state tags (employment, residence, etc.) automatically supersede older memories. Good for: facts learned, user preferences, decisions made, important context.",
                InputSchema = new ToolInputSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, PropertySchema>
                    {
                        ["title"] = new() { Type = "string", Description = "Short, descriptive title (used in search results)" },
                        ["summary"] = new() { Type = "string", Description = "1-2 sentence summary of the key information" },
                        ["content"] = new() { Type = "string", Description = "Full details, context, and any relevant information" },
                        ["tags"] = new() { Type = "array", Description = "Categorization tags. Singular-state tags (employment, residence, relationship-status) auto-supersede older memories with the same tag.", Items = new PropertySchema { Type = "string" } },
                        ["importance"] = new() { Type = "number", Description = "Priority 0.0-1.0 (higher = slower decay). Default 0.5" }
                    },
                    Required = ["title", "summary"]
                }
            },
            new()
            {
                Name = "update_memory",
                Description = "Update an existing memory by ID. Use this to correct information, add details, or update tags. The memory's last accessed time will be refreshed.",
                InputSchema = new ToolInputSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, PropertySchema>
                    {
                        ["id"] = new() { Type = "string", Format = "uuid", Description = "Memory ID (from search results or previous store)" },
                        ["title"] = new() { Type = "string", Description = "Updated title (optional)" },
                        ["summary"] = new() { Type = "string", Description = "Updated summary (optional)" },
                        ["content"] = new() { Type = "string", Description = "Updated content (optional)" },
                        ["tags"] = new() { Type = "array", Description = "Replace all tags with these (optional)", Items = new PropertySchema { Type = "string" } }
                    },
                    Required = ["id"]
                }
            },
            new()
            {
                Name = "get_memory",
                Description = "Retrieve full details of a specific memory by ID. Also reinforces the memory (increases strength). Use when you need complete content from a search result.",
                InputSchema = new ToolInputSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, PropertySchema>
                    {
                        ["id"] = new() { Type = "string", Format = "uuid", Description = "Memory ID from search results" }
                    },
                    Required = ["id"]
                }
            },
            new()
            {
                Name = "delete_memory",
                Description = "Permanently delete a memory. Use sparingly - memories naturally decay if unused. Only delete if information is wrong or explicitly requested.",
                InputSchema = new ToolInputSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, PropertySchema>
                    {
                        ["id"] = new() { Type = "string", Format = "uuid", Description = "Memory ID to delete" }
                    },
                    Required = ["id"]
                }
            },
            new()
            {
                Name = "get_stats",
                Description = "Get memory system statistics: total memories, average strength, database size. Useful for understanding memory health.",
                InputSchema = new ToolInputSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, PropertySchema>()
                }
            },
            new()
            {
                Name = "get_tag_history",
                Description = "Get the history of memories for a specific tag, including superseded/archived memories. Useful for seeing how information changed over time (e.g., employment history, location changes).",
                InputSchema = new ToolInputSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, PropertySchema>
                    {
                        ["tag"] = new() { Type = "string", Description = "The tag to get history for (e.g., 'employment', 'residence')" },
                        ["include_archived"] = new() { Type = "boolean", Description = "Include archived/superseded memories", Default = true }
                    },
                    Required = ["tag"]
                }
            }
        };

        return CreateSuccessResponse(request.Id, new { tools });
    }

    private async Task<Response> HandleToolsCallAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        var paramsJson = JsonSerializer.Serialize(request.Params, JsonOptions);
        var callParams = JsonSerializer.Deserialize<ToolCallParams>(paramsJson, JsonOptions);

        if (callParams is null || string.IsNullOrEmpty(callParams.Name))
        {
            return CreateErrorResponse(request.Id, -32602, "Invalid params: missing tool name");
        }

        try
        {
            var result = callParams.Name switch
            {
                "search_memories" => await ExecuteSearchMemoriesAsync(callParams.Arguments, cancellationToken),
                "store_memory" => await ExecuteStoreMemoryAsync(callParams.Arguments, cancellationToken),
                "update_memory" => await ExecuteUpdateMemoryAsync(callParams.Arguments, cancellationToken),
                "get_memory" => await ExecuteGetMemoryAsync(callParams.Arguments, cancellationToken),
                "delete_memory" => await ExecuteDeleteMemoryAsync(callParams.Arguments, cancellationToken),
                "get_stats" => await ExecuteGetStatsAsync(cancellationToken),
                "get_tag_history" => await ExecuteGetTagHistoryAsync(callParams.Arguments, cancellationToken),
                _ => new ToolCallResult
                {
                    IsError = true,
                    Content = [new ToolContent { Type = "text", Text = $"Unknown tool: {callParams.Name}" }]
                }
            };

            return CreateSuccessResponse(request.Id, result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Tool execution error: {Tool}", callParams.Name);
            return CreateSuccessResponse(request.Id, new ToolCallResult
            {
                IsError = true,
                Content = [new ToolContent { Type = "text", Text = $"Error executing {callParams.Name}: {ex.Message}" }]
            });
        }
    }

    private async Task<ToolCallResult> ExecuteSearchMemoriesAsync(Dictionary<string, object?>? args, CancellationToken cancellationToken)
    {
        if (_searchService is null)
        {
            return new ToolCallResult
            {
                IsError = true,
                Content = [new ToolContent { Type = "text", Text = "Search service not available" }]
            };
        }

        var query = GetStringArg(args, "query") ?? "";
        var topN = GetIntArg(args, "top_n") ?? 5;
        var tags = GetStringArrayArg(args, "tags");

        var results = await _searchService.SearchAsync(query, topN, tags, cancellationToken);

        var text = results.Count == 0
            ? "No memories found matching the query."
            : string.Join("\n\n", results.Select(r =>
                $"**{r.Memory.Title}** (Score: {r.Score:F2})\n" +
                $"ID: {r.Memory.Id}\n" +
                $"Summary: {r.Memory.Summary}\n" +
                $"Tags: {string.Join(", ", r.Memory.Tags)}"));

        return new ToolCallResult
        {
            Content = [new ToolContent { Type = "text", Text = text }]
        };
    }

    private async Task<ToolCallResult> ExecuteStoreMemoryAsync(Dictionary<string, object?>? args, CancellationToken cancellationToken)
    {
        if (_repository is null)
        {
            return new ToolCallResult
            {
                IsError = true,
                Content = [new ToolContent { Type = "text", Text = "Repository not available" }]
            };
        }

        var title = GetStringArg(args, "title");
        var summary = GetStringArg(args, "summary");
        var content = GetStringArg(args, "content") ?? "";
        var tags = GetStringArrayArg(args, "tags") ?? [];
        var importance = GetDoubleArg(args, "importance") ?? 0.5;

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary))
        {
            return new ToolCallResult
            {
                IsError = true,
                Content = [new ToolContent { Type = "text", Text = "Title and summary are required" }]
            };
        }

        // Validate and enforce size limits
        if (title.Length > _storageSettings.MaxTitleLength)
        {
            title = title[.._storageSettings.MaxTitleLength];
            _logger?.LogDebug("Title truncated to {MaxLength} characters", _storageSettings.MaxTitleLength);
        }

        if (summary.Length > _storageSettings.MaxSummaryLength)
        {
            summary = summary[.._storageSettings.MaxSummaryLength];
            _logger?.LogDebug("Summary truncated to {MaxLength} characters", _storageSettings.MaxSummaryLength);
        }

        if (content.Length > _storageSettings.MaxContentSizeBytes)
        {
            content = content[.._storageSettings.MaxContentSizeBytes];
            _logger?.LogDebug("Content truncated to {MaxLength} bytes", _storageSettings.MaxContentSizeBytes);
        }

        if (tags.Count > _storageSettings.MaxTagsPerMemory)
        {
            tags = tags[.._storageSettings.MaxTagsPerMemory];
            _logger?.LogDebug("Tags limited to {MaxTags}", _storageSettings.MaxTagsPerMemory);
        }

        // Clamp importance to valid range
        importance = Math.Clamp(importance, 0.0, 1.0);

        var entity = new MemoryNodeEntity
        {
            Title = title,
            Summary = summary,
            Content = content,
            Tags = tags.ToList(),
            Importance = importance
        };

        // Use conflict-aware storage if available
        if (_conflictStorage is not null)
        {
            var result = await _conflictStorage.StoreAsync(entity, cancellationToken);
            
            var responseText = result.Action switch
            {
                StoreAction.ReinforcedExisting => 
                    $"Similar memory already exists.\n{result.Message}\nID: {result.Memory.Id}\nTitle: {result.Memory.Title}",
                StoreAction.StoredWithSupersede => 
                    $"Memory stored with conflict resolution.\n{result.Message}\nID: {result.Memory.Id}\nTitle: {result.Memory.Title}\nImportance: {importance:F1}",
                StoreAction.StoredCoexist => 
                    $"Memory stored (coexists with similar).\n{result.Message}\nID: {result.Memory.Id}\nTitle: {result.Memory.Title}\nImportance: {importance:F1}",
                _ => 
                    $"Memory stored successfully.\nID: {result.Memory.Id}\nTitle: {result.Memory.Title}\nImportance: {importance:F1}"
            };

            return new ToolCallResult
            {
                Content = [new ToolContent { Type = "text", Text = responseText }]
            };
        }

        // Fallback to direct repository save
        await _repository.SaveAsync(entity, cancellationToken);

        return new ToolCallResult
        {
            Content = [new ToolContent { Type = "text", Text = $"Memory stored successfully.\nID: {entity.Id}\nTitle: {entity.Title}\nImportance: {importance:F1}" }]
        };
    }

    private async Task<ToolCallResult> ExecuteUpdateMemoryAsync(Dictionary<string, object?>? args, CancellationToken cancellationToken)
    {
        if (_repository is null)
        {
            return new ToolCallResult
            {
                IsError = true,
                Content = [new ToolContent { Type = "text", Text = "Repository not available" }]
            };
        }

        var idStr = GetStringArg(args, "id");
        if (!Guid.TryParse(idStr, out var id))
        {
            return new ToolCallResult
            {
                IsError = true,
                Content = [new ToolContent { Type = "text", Text = "Invalid memory ID format" }]
            };
        }

        var existing = await _repository.GetAsync(id, cancellationToken);
        if (existing is null)
        {
            return new ToolCallResult
            {
                IsError = true,
                Content = [new ToolContent { Type = "text", Text = $"Memory not found: {id}" }]
            };
        }

        // Update only provided fields
        var title = GetStringArg(args, "title");
        var summary = GetStringArg(args, "summary");
        var content = GetStringArg(args, "content");
        var tags = GetStringArrayArg(args, "tags");

        if (title is not null) existing.Title = title;
        if (summary is not null) existing.Summary = summary;
        if (content is not null) existing.Content = content;
        if (tags is not null) existing.Tags = tags.ToList();

        await _repository.SaveAsync(existing, cancellationToken);

        return new ToolCallResult
        {
            Content = [new ToolContent { Type = "text", Text = $"Memory updated successfully.\nID: {existing.Id}\nTitle: {existing.Title}" }]
        };
    }

    private async Task<ToolCallResult> ExecuteGetMemoryAsync(Dictionary<string, object?>? args, CancellationToken cancellationToken)
    {
        if (_repository is null)
        {
            return new ToolCallResult
            {
                IsError = true,
                Content = [new ToolContent { Type = "text", Text = "Repository not available" }]
            };
        }

        var idStr = GetStringArg(args, "id");
        if (!Guid.TryParse(idStr, out var id))
        {
            return new ToolCallResult
            {
                IsError = true,
                Content = [new ToolContent { Type = "text", Text = "Invalid memory ID" }]
            };
        }

        var memory = await _repository.GetAsync(id, cancellationToken);
        if (memory is null)
        {
            return new ToolCallResult
            {
                IsError = true,
                Content = [new ToolContent { Type = "text", Text = $"Memory not found: {id}" }]
            };
        }

        await _repository.ReinforceAsync(id, cancellationToken);

        var text = $"**{memory.Title}**\n\n" +
                   $"ID: {memory.Id}\n" +
                   $"Summary: {memory.Summary}\n\n" +
                   $"Content:\n{memory.Content}\n\n" +
                   $"Tags: {string.Join(", ", memory.Tags)}\n" +
                   $"Created: {memory.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC\n" +
                   $"Strength: {memory.GetCurrentStrength():F2}";

        return new ToolCallResult
        {
            Content = [new ToolContent { Type = "text", Text = text }]
        };
    }

    private async Task<ToolCallResult> ExecuteDeleteMemoryAsync(Dictionary<string, object?>? args, CancellationToken cancellationToken)
    {
        if (_repository is null)
        {
            return new ToolCallResult
            {
                IsError = true,
                Content = [new ToolContent { Type = "text", Text = "Repository not available" }]
            };
        }

        var idStr = GetStringArg(args, "id");
        if (!Guid.TryParse(idStr, out var id))
        {
            return new ToolCallResult
            {
                IsError = true,
                Content = [new ToolContent { Type = "text", Text = "Invalid memory ID" }]
            };
        }

        var deleted = await _repository.DeleteAsync(id, cancellationToken);

        return new ToolCallResult
        {
            Content = [new ToolContent { Type = "text", Text = deleted ? $"Memory deleted: {id}" : $"Memory not found: {id}" }]
        };
    }

    private async Task<ToolCallResult> ExecuteGetStatsAsync(CancellationToken cancellationToken)
    {
        if (_repository is null)
        {
            return new ToolCallResult
            {
                IsError = true,
                Content = [new ToolContent { Type = "text", Text = "Repository not available" }]
            };
        }

        var stats = await _repository.GetStatsAsync(cancellationToken);

        var text = $"**Memory Repository Statistics**\n\n" +
                   $"Total Nodes: {stats.TotalNodes}\n" +
                   $"Average Strength: {stats.AverageStrength:F2}\n" +
                   $"Weak Memories: {stats.WeakMemoriesCount}\n" +
                   $"Database Size: {stats.DatabaseSizeBytes / 1024.0 / 1024.0:F2} MB\n" +
                   $"Oldest Memory: {stats.OldestMemory?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}\n" +
                   $"Newest Memory: {stats.NewestMemory?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}";

        return new ToolCallResult
        {
            Content = [new ToolContent { Type = "text", Text = text }]
        };
    }

    private Task<Response> HandleResourcesListAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        // Only expose lightweight resources - no memory://all to prevent loading entire database
        var resources = new List<ResourceDefinition>
        {
            new()
            {
                Uri = "memory://recent",
                Name = "Recent Memories",
                Description = "Recently accessed memories (last 10)",
                MimeType = "application/json"
            },
            new()
            {
                Uri = "memory://stats",
                Name = "Statistics",
                Description = "Memory repository statistics",
                MimeType = "application/json"
            }
        };

        // Note: Individual memories should be accessed via get_memory tool or memory://{id} URI
        // We don't enumerate all memories here for performance reasons

        return Task.FromResult(CreateSuccessResponse(request.Id, new { resources }));
    }

    private async Task<Response> HandleResourcesReadAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        var paramsJson = JsonSerializer.Serialize(request.Params, JsonOptions);
        var readParams = JsonSerializer.Deserialize<ResourceReadParams>(paramsJson, JsonOptions);

        if (readParams is null || string.IsNullOrEmpty(readParams.Uri))
        {
            return CreateErrorResponse(request.Id, -32602, "Invalid params: missing uri");
        }

        var uri = readParams.Uri;

        if (!uri.StartsWith("memory://"))
        {
            return CreateErrorResponse(request.Id, -32602, $"Invalid URI scheme. Expected memory://, got: {uri}");
        }

        var path = uri["memory://".Length..];

        try
        {
            string content;

            if (path == "recent")
            {
                if (_repository is null)
                {
                    content = JsonSerializer.Serialize(new { error = "Repository not available" }, JsonOptions);
                }
                else
                {
                    var memories = await _repository.GetAllAsync(cancellationToken);
                    var recent = memories.OrderByDescending(m => m.LastAccessedAt).Take(10);
                    content = JsonSerializer.Serialize(recent.Select(m => new
                    {
                        m.Id,
                        m.Title,
                        m.Summary,
                        m.LastAccessedAt,
                        Strength = m.GetCurrentStrength()
                    }), JsonOptions);
                }
            }
            else if (path == "stats")
            {
                if (_repository is null)
                {
                    content = JsonSerializer.Serialize(new { error = "Repository not available" }, JsonOptions);
                }
                else
                {
                    var stats = await _repository.GetStatsAsync(cancellationToken);
                    content = JsonSerializer.Serialize(stats, JsonOptions);
                }
            }
            else if (Guid.TryParse(path, out var memoryId))
            {
                if (_repository is null)
                {
                    content = JsonSerializer.Serialize(new { error = "Repository not available" }, JsonOptions);
                }
                else
                {
                    var memory = await _repository.GetAsync(memoryId, cancellationToken);
                    if (memory is null)
                    {
                        return CreateErrorResponse(request.Id, -32602, $"Memory not found: {memoryId}");
                    }
                    await _repository.ReinforceAsync(memoryId, cancellationToken);
                    content = JsonSerializer.Serialize(new
                    {
                        memory.Id,
                        memory.Title,
                        memory.Summary,
                        memory.Content,
                        memory.Tags,
                        memory.CreatedAt,
                        memory.LastAccessedAt,
                        Strength = memory.GetCurrentStrength(),
                        memory.LinkedNodeIds
                    }, JsonOptions);
                }
            }
            else
            {
                return CreateErrorResponse(request.Id, -32602, $"Invalid resource path: {path}");
            }

            var result = new ResourceReadResult
            {
                Contents =
                [
                    new ResourceContent
                    {
                        Uri = uri,
                        MimeType = "application/json",
                        Text = content
                    }
                ]
            };

            return CreateSuccessResponse(request.Id, result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error reading resource: {Uri}", uri);
            return CreateErrorResponse(request.Id, -32603, $"Error reading resource: {ex.Message}");
        }
    }

    private async Task<ToolCallResult> ExecuteGetTagHistoryAsync(Dictionary<string, object?>? args, CancellationToken cancellationToken)
    {
        if (_conflictStorage is null)
        {
            return new ToolCallResult
            {
                IsError = true,
                Content = [new ToolContent { Type = "text", Text = "Conflict-aware storage not available. Tag history requires conflict resolution feature." }]
            };
        }

        var tag = GetStringArg(args, "tag");
        var includeArchived = GetBoolArg(args, "include_archived") ?? true;

        if (string.IsNullOrWhiteSpace(tag))
        {
            return new ToolCallResult
            {
                IsError = true,
                Content = [new ToolContent { Type = "text", Text = "Tag parameter is required" }]
            };
        }

        var history = await _conflictStorage.GetTagHistoryAsync(tag, includeArchived, cancellationToken);

        if (history.Count == 0)
        {
            return new ToolCallResult
            {
                Content = [new ToolContent { Type = "text", Text = $"No memories found with tag '{tag}'." }]
            };
        }

        var text = $"**Memory History for tag '{tag}'** ({history.Count} memor{(history.Count == 1 ? "y" : "ies")})\n\n" +
            string.Join("\n\n", history.Select(m =>
            {
                var status = m.IsCurrent ? "? CURRENT" : "? Superseded";
                var validity = m.ValidUntil.HasValue
                    ? $"Valid: {m.ValidFrom:yyyy-MM-dd} ? {m.ValidUntil:yyyy-MM-dd}"
                    : $"Valid from: {m.ValidFrom:yyyy-MM-dd}";
                var supersededBy = m.SupersededBy.HasValue ? $"\nSuperseded by: {m.SupersededBy}" : "";

                return $"**{m.Title}** [{status}]\n" +
                       $"ID: {m.Id}\n" +
                       $"Summary: {m.Summary}\n" +
                       $"{validity}{supersededBy}";
            }));

        return new ToolCallResult
        {
            Content = [new ToolContent { Type = "text", Text = text }]
        };
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

    private Response CreateSuccessResponse(object? id, object result)
    {
        var response = new JsonRpcResponse
        {
            Jsonrpc = "2.0",
            Id = id,
            Result = result
        };

        return Response.Ok(response);
    }

    private Response CreateErrorResponse(object? id, int code, string message)
    {
        var response = new JsonRpcResponse
        {
            Jsonrpc = "2.0",
            Id = id,
            Error = new JsonRpcError
            {
                Code = code,
                Message = message
            }
        };

        return Response.Ok(response);
    }
}
