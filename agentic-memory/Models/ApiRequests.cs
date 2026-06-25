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

/// <summary>
/// Request model for local generative inference.
/// </summary>
/// <param name="UserPrompt">The user message to send to the model.</param>
/// <param name="SystemPrompt">Optional system prompt (defaults to a generic helpful assistant).</param>
public record GenerateRequest(
    string UserPrompt,
    string? SystemPrompt = null);

/// <summary>
/// Request model for generating an AI description of a file.
/// </summary>
/// <param name="FilePath">Absolute or relative path to the source file.</param>
public record FileSummaryRequest(string FilePath);

/// <summary>
/// Request model for creating a new project.
/// </summary>
/// <param name="Name">Display name for the project.</param>
/// <param name="RootPath">Absolute path to the project root directory.</param>
public record ProjectCreateRequest(string Name, string RootPath);

// ── /api/admin/status response ────────────────────────────────────────────────

public record SystemStatusResponse(
    string Status,
    string Timestamp,
    ServerStatus Server,
    GenerationStatus Generation,
    EmbeddingsStatus Embeddings,
    MaintenanceStatusInfo Maintenance,
    CodeIndexStatus CodeIndex);

public record ServerStatus(string ListeningUrl);

public record GenerationStatus(
    bool Enabled,
    bool Available,
    string? ModelName);

public record EmbeddingsStatus(
    bool Enabled,
    bool Available,
    string? ModelName,
    int Dimensions);

public record MaintenanceStatusInfo(bool Enabled);

public record CodeIndexStatus(
    bool Enabled,
    IReadOnlyList<ProviderStatusEntry> Providers);

public record ProviderStatusEntry(
    string ProviderType,
    string CompilerApi,
    string[] DomainPatternFamilies,
    bool Active);

// ── Code Index responses ──────────────────────────────────────────────────────

public record ProjectActivateResponse(
    string ProjectId,
    string Name,
    string RootPath,
    int QueuedFiles,
    int AlreadyIndexed);

public record CodeIndexFileResponse(
    string Id,
    string ProjectId,
    string FilePath,
    string FileName,
    string RelativePath,
    string Language,
    string ProviderType,
    string ExtractedContext,
    string LlmSummary,
    IReadOnlyList<AgenticMemory.CodeIndex.SymbolRecord> Symbols,
    DateTime IndexedAt,
    DateTime FileModifiedAt,
    bool IsStale,
    string? IngestionError,
    float? Score = null);

public record WorkerStatusResponse(
    string? ActiveProjectId,
    string? ActiveProjectName,
    bool IsProcessing,
    string? CurrentFile,
    int QueueDepth,
    int SummaryQueueDepth,
    int TotalIndexableFiles,
    int IndexedFiles,
    int StaleFiles,
    int ErrorFiles,
    IReadOnlyList<RecentJobEntryDto> RecentJobs,
    IReadOnlyList<RecentErrorEntryDto> RecentErrors);

public record RecentJobEntryDto(
    string RelativePath,
    string Language,
    int SymbolCount,
    long DurationMs,
    string IndexedAt,
    bool WasNew);

public record RecentErrorEntryDto(
    string RelativePath,
    string Error,
    string OccurredAt);

public record IngestFileRequest(string FilePath, string ProjectId, bool Force = false);
