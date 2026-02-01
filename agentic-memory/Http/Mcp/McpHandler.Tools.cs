using System.Text.Json;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Http.Mcp;

/// <summary>
/// MCP Handler - Tools functionality (tools/list, tools/call)
/// </summary>
public partial class McpHandler
{
    private JsonRpcResponse HandleToolsList(JsonRpcRequest request)
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

        return CreateJsonRpcSuccessResponse(request.Id, new { tools });
    }

    private async Task<JsonRpcResponse> HandleToolsCallAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        var paramsJson = JsonSerializer.Serialize(request.Params, JsonOptions);
        var callParams = JsonSerializer.Deserialize<ToolCallParams>(paramsJson, JsonOptions);

        if (callParams is null || string.IsNullOrEmpty(callParams.Name))
        {
            _logger?.LogWarning("[MCP] tools/call missing tool name");
            return CreateJsonRpcErrorResponse(request.Id, -32602, "Invalid params: missing tool name");
        }

        _logger?.LogInformation("[MCP] Executing tool: {ToolName}", callParams.Name);
        
        // Log tool arguments at debug level
        if (callParams.Arguments is not null && _logger?.IsEnabled(LogLevel.Debug) == true)
        {
            var argsJson = JsonSerializer.Serialize(callParams.Arguments, JsonPrettyOptions);
            _logger.LogDebug("[MCP] Tool arguments:\n{Arguments}", argsJson);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
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

            stopwatch.Stop();
            
            if (result.IsError)
            {
                _logger?.LogWarning("[MCP] Tool '{ToolName}' completed with error in {ElapsedMs}ms", 
                callParams.Name, stopwatch.ElapsedMilliseconds);
            }
            else
            {
                _logger?.LogInformation("[MCP] Tool '{ToolName}' completed successfully in {ElapsedMs}ms", 
                callParams.Name, stopwatch.ElapsedMilliseconds);
            }

            return CreateJsonRpcSuccessResponse(request.Id, result);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger?.LogError(ex, "[MCP] Tool '{ToolName}' failed after {ElapsedMs}ms: {Message}", 
            callParams.Name, stopwatch.ElapsedMilliseconds, ex.Message);
            
            return CreateJsonRpcSuccessResponse(request.Id, new ToolCallResult
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

        _logger?.LogDebug("[MCP] search_memories: query='{Query}', topN={TopN}, tags={Tags}", 
            query, topN, tags is not null ? string.Join(",", tags) : "(none)");

        var results = await _searchService.SearchAsync(query, topN, tags, cancellationToken);

        _logger?.LogDebug("[MCP] search_memories: found {Count} results", results.Count);

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
}
