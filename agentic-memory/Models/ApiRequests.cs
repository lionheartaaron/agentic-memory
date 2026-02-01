namespace AgenticMemory.Models;

/// <summary>
/// Request model for creating a new memory.
/// </summary>
/// <param name="Title">The title of the memory.</param>
/// <param name="Summary">A brief summary of the memory content.</param>
/// <param name="Content">The full content of the memory (optional).</param>
/// <param name="Tags">Optional tags for categorization.</param>
/// <param name="Importance">Importance score between 0 and 1 (default: 0.5).</param>
public record MemoryCreateRequest(
    string Title,
    string Summary,
    string? Content = null,
    string[]? Tags = null,
    double? Importance = null);

/// <summary>
/// Request model for updating an existing memory.
/// </summary>
/// <param name="Title">New title (optional).</param>
/// <param name="Summary">New summary (optional).</param>
/// <param name="Content">New content (optional).</param>
/// <param name="Tags">New tags (optional).</param>
public record MemoryUpdateRequest(
    string? Title = null,
    string? Summary = null,
    string? Content = null,
    string[]? Tags = null);

/// <summary>
/// Request model for searching memories.
/// </summary>
/// <param name="Query">The search query text.</param>
/// <param name="TopN">Maximum number of results to return (default: 5).</param>
/// <param name="Tags">Optional tags to filter results.</param>
public record SearchRequest(
    string Query,
    int? TopN = null,
    string[]? Tags = null);
