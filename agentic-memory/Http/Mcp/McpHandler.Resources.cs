using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Http.Mcp;

/// <summary>
/// MCP Handler - Resources functionality (resources/list, resources/read, resources/templates/list)
/// </summary>
public partial class McpHandler
{
    /// <summary>
    /// Handle resources/templates/list - returns parameterized resource templates
    /// </summary>
    private JsonRpcResponse HandleResourcesTemplatesList(JsonRpcRequest request)
    {
        _logger?.LogDebug("[MCP] resources/templates/list called");
        
        var resourceTemplates = new List<ResourceTemplateDefinition>
        {
            new()
            {
                UriTemplate = "memory://tag/{tag}",
                Name = "Memories by Tag",
                Description = "Get all memories with a specific tag",
                MimeType = "application/json"
            },
            new()
            {
                UriTemplate = "memory://search/{query}",
                Name = "Search Memories",
                Description = "Search memories using a natural language query",
                MimeType = "application/json"
            }
        };
        
        return CreateJsonRpcSuccessResponse(request.Id, new { resourceTemplates });
    }

    private Task<JsonRpcResponse> HandleResourcesListAsync(JsonRpcRequest request, CancellationToken cancellationToken)
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

        return Task.FromResult(CreateJsonRpcSuccessResponse(request.Id, new { resources }));
    }

    private async Task<JsonRpcResponse> HandleResourcesReadAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        var paramsJson = JsonSerializer.Serialize(request.Params, JsonOptions);
        var readParams = JsonSerializer.Deserialize<ResourceReadParams>(paramsJson, JsonOptions);

        if (readParams is null || string.IsNullOrEmpty(readParams.Uri))
        {
            return CreateJsonRpcErrorResponse(request.Id, -32602, "Invalid params: missing uri");
        }

        var uri = readParams.Uri;

        if (!uri.StartsWith("memory://"))
        {
            return CreateJsonRpcErrorResponse(request.Id, -32602, $"Invalid URI scheme. Expected memory://, got: {uri}");
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
            else if (path.StartsWith("tag/"))
            {
                // Handle memory://tag/{tag} template
                var tag = System.Net.WebUtility.UrlDecode(path["tag/".Length..]);
                if (_repository is null)
                {
                    content = JsonSerializer.Serialize(new { error = "Repository not available" }, JsonOptions);
                }
                else
                {
                    var allMemories = await _repository.GetAllAsync(cancellationToken);
                    var tagged = allMemories.Where(m => m.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
                    content = JsonSerializer.Serialize(tagged.Select(m => new
                    {
                        m.Id,
                        m.Title,
                        m.Summary,
                        m.Tags,
                        m.CreatedAt,
                        Strength = m.GetCurrentStrength()
                    }), JsonOptions);
                }
            }
            else if (path.StartsWith("search/"))
            {
                // Handle memory://search/{query} template
                var query = System.Net.WebUtility.UrlDecode(path["search/".Length..]);
                if (_searchService is null)
                {
                    content = JsonSerializer.Serialize(new { error = "Search service not available" }, JsonOptions);
                }
                else
                {
                    var results = await _searchService.SearchAsync(query, 10, null, cancellationToken);
                    content = JsonSerializer.Serialize(results.Select(r => new
                    {
                        r.Memory.Id,
                        r.Memory.Title,
                        r.Memory.Summary,
                        r.Memory.Tags,
                        r.Score,
                        Strength = r.Memory.GetCurrentStrength()
                    }), JsonOptions);
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
                        return CreateJsonRpcErrorResponse(request.Id, -32602, $"Memory not found: {memoryId}");
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
                return CreateJsonRpcErrorResponse(request.Id, -32602, $"Invalid resource path: {path}");
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

            return CreateJsonRpcSuccessResponse(request.Id, result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error reading resource: {Uri}", uri);
            return CreateJsonRpcErrorResponse(request.Id, -32603, $"Error reading resource: {ex.Message}");
        }
    }
}
