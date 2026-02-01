using System.ComponentModel;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Configuration;
using ModelContextProtocol.Server;

namespace AgenticMemory.Tools;

/// <summary>
/// MCP Tools for Agentic Memory - provides memory management capabilities to AI agents
/// </summary>
[McpServerToolType]
public class MemoryTools
{
    private readonly IMemoryRepository _repository;
    private readonly ISearchService _searchService;
    private readonly IConflictAwareStorage? _conflictStorage;
    private readonly StorageSettings _storageSettings;

    public MemoryTools(
        IMemoryRepository repository,
        ISearchService searchService,
        IConflictAwareStorage? conflictStorage = null,
        StorageSettings? storageSettings = null)
    {
        _repository = repository;
        _searchService = searchService;
        _conflictStorage = conflictStorage;
        _storageSettings = storageSettings ?? new StorageSettings();
    }

    [McpServerTool(Name = "search_memories")]
    [Description("Search for memories using semantic vector similarity and fuzzy text matching. Use this FIRST before storing new memories to check if related information already exists. Returns memories ranked by relevance score.")]
    public async Task<string> SearchMemories(
        [Description("Natural language search query. Can include concepts, keywords, or questions.")] string query,
        [Description("Maximum number of results to return (1-100)")] int top_n = 5,
        [Description("Optional: Filter results to only memories containing these tags")] string[]? tags = null,
        CancellationToken cancellationToken = default)
    {
        var results = await _searchService.SearchAsync(query, top_n, tags, cancellationToken);

        if (results.Count == 0)
            return "No memories found matching the query.";

        return string.Join("\n\n", results.Select(r =>
            $"**{r.Memory.Title}** (Score: {r.Score:F2})\n" +
            $"ID: {r.Memory.Id}\n" +
            $"Summary: {r.Memory.Summary}\n" +
            $"Tags: {string.Join(", ", r.Memory.Tags)}"));
    }

    [McpServerTool(Name = "store_memory")]
    [Description("Store a new memory with automatic conflict resolution. Duplicates are detected and reinforced. Singular-state tags (employment, residence, etc.) automatically supersede older memories. Good for: facts learned, user preferences, decisions made, important context.")]
    public async Task<string> StoreMemory(
        [Description("Short, descriptive title (used in search results)")] string title,
        [Description("1-2 sentence summary of the key information")] string summary,
        [Description("Full details, context, and any relevant information")] string? content = null,
        [Description("Categorization tags. Singular-state tags (employment, residence, relationship-status) auto-supersede older memories with the same tag.")] string[]? tags = null,
        [Description("Priority 0.0-1.0 (higher = slower decay). Default 0.5")] double importance = 0.5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary))
        {
            return "Error: Title and summary are required";
        }

        // Validate and enforce size limits
        if (title.Length > _storageSettings.MaxTitleLength)
            title = title[.._storageSettings.MaxTitleLength];

        if (summary.Length > _storageSettings.MaxSummaryLength)
            summary = summary[.._storageSettings.MaxSummaryLength];

        var contentValue = content ?? "";
        if (contentValue.Length > _storageSettings.MaxContentSizeBytes)
            contentValue = contentValue[.._storageSettings.MaxContentSizeBytes];

        var tagsList = tags?.ToList() ?? [];
        if (tagsList.Count > _storageSettings.MaxTagsPerMemory)
            tagsList = tagsList[.._storageSettings.MaxTagsPerMemory];

        importance = Math.Clamp(importance, 0.0, 1.0);

        var entity = new MemoryNodeEntity
        {
            Title = title,
            Summary = summary,
            Content = contentValue,
            Tags = tagsList,
            Importance = importance
        };

        // Use conflict-aware storage if available
        if (_conflictStorage is not null)
        {
            var result = await _conflictStorage.StoreAsync(entity, cancellationToken);

            return result.Action switch
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
        }

        // Fallback to direct repository save
        await _repository.SaveAsync(entity, cancellationToken);
        return $"Memory stored successfully.\nID: {entity.Id}\nTitle: {entity.Title}\nImportance: {importance:F1}";
    }

    [McpServerTool(Name = "update_memory")]
    [Description("Update an existing memory by ID. Use this to correct information, add details, or update tags. The memory's last accessed time will be refreshed.")]
    public async Task<string> UpdateMemory(
        [Description("Memory ID (from search results or previous store)")] Guid id,
        [Description("Updated title (optional)")] string? title = null,
        [Description("Updated summary (optional)")] string? summary = null,
        [Description("Updated content (optional)")] string? content = null,
        [Description("Replace all tags with these (optional)")] string[]? tags = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetAsync(id, cancellationToken);
        if (existing is null)
        {
            return $"Error: Memory not found: {id}";
        }

        // Update only provided fields
        if (title is not null) existing.Title = title;
        if (summary is not null) existing.Summary = summary;
        if (content is not null) existing.Content = content;
        if (tags is not null) existing.Tags = tags.ToList();

        await _repository.SaveAsync(existing, cancellationToken);
        return $"Memory updated successfully.\nID: {existing.Id}\nTitle: {existing.Title}";
    }

    [McpServerTool(Name = "get_memory")]
    [Description("Retrieve full details of a specific memory by ID. Also reinforces the memory (increases strength). Use when you need complete content from a search result.")]
    public async Task<string> GetMemory(
        [Description("Memory ID from search results")] Guid id,
        CancellationToken cancellationToken = default)
    {
        var memory = await _repository.GetAsync(id, cancellationToken);
        if (memory is null)
        {
            return $"Error: Memory not found: {id}";
        }

        await _repository.ReinforceAsync(id, cancellationToken);

        return $"**{memory.Title}**\n\n" +
               $"ID: {memory.Id}\n" +
               $"Summary: {memory.Summary}\n\n" +
               $"Content:\n{memory.Content}\n\n" +
               $"Tags: {string.Join(", ", memory.Tags)}\n" +
               $"Created: {memory.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC\n" +
               $"Strength: {memory.GetCurrentStrength():F2}";
    }

    [McpServerTool(Name = "delete_memory")]
    [Description("Permanently delete a memory. Use sparingly - memories naturally decay if unused. Only delete if information is wrong or explicitly requested.")]
    public async Task<string> DeleteMemory(
        [Description("Memory ID to delete")] Guid id,
        CancellationToken cancellationToken = default)
    {
        var deleted = await _repository.DeleteAsync(id, cancellationToken);
        return deleted ? $"Memory deleted: {id}" : $"Memory not found: {id}";
    }

    [McpServerTool(Name = "get_stats")]
    [Description("Get memory system statistics: total memories, average strength, database size. Useful for understanding memory health.")]
    public async Task<string> GetStats(CancellationToken cancellationToken = default)
    {
        var stats = await _repository.GetStatsAsync(cancellationToken);

        return $"**Memory Repository Statistics**\n\n" +
               $"Total Nodes: {stats.TotalNodes}\n" +
               $"Average Strength: {stats.AverageStrength:F2}\n" +
               $"Weak Memories: {stats.WeakMemoriesCount}\n" +
               $"Database Size: {stats.DatabaseSizeBytes / 1024.0 / 1024.0:F2} MB\n" +
               $"Oldest Memory: {stats.OldestMemory?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}\n" +
               $"Newest Memory: {stats.NewestMemory?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}";
    }

    [McpServerTool(Name = "get_tag_history")]
    [Description("Get the history of memories for a specific tag, including superseded/archived memories. Useful for seeing how information changed over time (e.g., employment history, location changes).")]
    public async Task<string> GetTagHistory(
        [Description("The tag to get history for (e.g., 'employment', 'residence')")] string tag,
        [Description("Include archived/superseded memories")] bool include_archived = true,
        CancellationToken cancellationToken = default)
    {
        if (_conflictStorage is null)
        {
            return "Error: Conflict-aware storage not available. Tag history requires conflict resolution feature.";
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            return "Error: Tag parameter is required";
        }

        var history = await _conflictStorage.GetTagHistoryAsync(tag, include_archived, cancellationToken);

        if (history.Count == 0)
        {
            return $"No memories found with tag '{tag}'.";
        }

        return $"**Memory History for tag '{tag}'** ({history.Count} memor{(history.Count == 1 ? "y" : "ies")})\n\n" +
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
    }
}
