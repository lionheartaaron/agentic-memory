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
    double? Importance = null,
    string? UserId = null,
    string? CompanionId = null,
    string? Visibility = null,
    string? Subject = null,
    string? Predicate = null,
    string? Value = null,
    string? Type = null,
    string? Source = null,
    bool? Pinned = null);

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
    string[]? Tags = null,
    string? UserId = null,
    string? CompanionId = null,
    string? Subject = null,
    string? Predicate = null,
    bool IncludeCoreContext = false,

    /// <summary>Answer as of a past instant on the valid-time axis, including facts since replaced.</summary>
    DateTime? AsOf = null,

    /// <summary>0-1 preference for memories this companion has not already brought up.</summary>
    double NoveltyBias = 0);

/// <summary>Resolve a recorded contradiction.</summary>
public record ConflictResolveRequest(
    Guid? WinnerId = null,
    bool Dismiss = false,
    string? UserId = null,
    string? CompanionId = null);

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
    float? Score = null,
    // Symbol reference graph fields (zero/empty for records pre-dating the upgrade)
    int FanIn  = 0,
    int FanOut = 0,
    IReadOnlyList<string>? DependsOnFileIds = null,
    IReadOnlyList<string>? UsedByFileIds    = null,
    // Phase 4 semantic fields
    IReadOnlyList<string>? DomainTags    = null,
    IReadOnlyList<string>? Imports       = null,
    IReadOnlyList<string>? TypeHierarchy = null,
    string? DiagnosticSummary            = null,
    // P1/P2/P6 file-level rollups (zero/empty for records pre-dating the upgrade)
    bool   IsTestFile           = false,
    string? TestFramework       = null,
    IReadOnlyList<string>? TestSubjectFileIds = null,
    bool   HasValidation        = false,
    string? ArchitecturalRole   = null,
    bool   IsEntrypoint         = false);

public record SubProjectStatusDto(
    string SubProjectId,
    string Name,
    string Language,
    int IndexedFiles,
    int StaleFiles,
    int ErrorFiles);

public record QueuedFileDto(string RelativePath, string FilePath);

public record WorkerStatusResponse(
    string? ActiveProjectId,
    string? ActiveProjectName,
    bool IsProcessing,
    string? CurrentFile,
    string? CurrentSummaryFile,
    int QueueDepth,
    int SummaryQueueDepth,
    int TotalIndexableFiles,
    int IndexedFiles,
    int StaleFiles,
    int ErrorFiles,
    IReadOnlyList<RecentJobEntryDto> RecentJobs,
    IReadOnlyList<RecentErrorEntryDto> RecentErrors,
    IReadOnlyList<SubProjectStatusDto> SubProjectStatuses,
    IReadOnlyList<QueuedFileDto> QueuedIngestions,
    IReadOnlyList<QueuedFileDto> QueuedSummaries,
    // Reference analysis worker
    string? CurrentReferenceFile  = null,
    int ReferenceQueueDepth       = 0,
    int TotalSymbolReferences     = 0);

// ── Intelligence API responses ────────────────────────────────────────────────

public record SymbolUsageSiteDto(
    string FileId,
    string RelativePath,
    int    Line,
    string Context,
    string Role = "ref",                 // P2: call/new/read/write/typeref/implements/override
    string? EnclosingName = null);        // P5: the calling symbol

public record SymbolReferenceDto(
    string Id,
    string Name,
    string Kind,
    string Accessibility,
    string DefinedInFileId,
    string DefinedInRelativePath,
    int    DefinedAtLine,
    int    FanIn,
    IReadOnlyList<SymbolUsageSiteDto> UsedBy,
    IReadOnlyList<string>? TestedByFileIds = null);

public record SymbolSearchResponse(
    int Total,
    IReadOnlyList<SymbolReferenceDto> Symbols);

public record DependencyNodeDto(
    string Id,
    string RelativePath,
    int    FanIn,
    int    FanOut,
    int    SymbolCount,
    string Language);

public record DependencyEdgeDto(
    string From,
    string To,
    IReadOnlyList<string> ViaSymbols);

public record DependencyGraphDto(
    IReadOnlyList<DependencyNodeDto> Nodes,
    IReadOnlyList<DependencyEdgeDto> Edges);

public record IntelligenceFileProfileDto(
    CodeIndexFileResponse File,
    IReadOnlyList<SymbolReferenceDto> DefinedSymbols,
    IReadOnlyList<DependencyNodeDto>  DependsOn);

// ── P0–P6 surfacing DTOs ────────────────────────────────────────────────────────

public record FileContentResponse(
    string FileId,
    string RelativePath,
    int    StartLine,
    int    EndLine,
    int    TotalLines,
    bool   Stale,
    string Content);

public record DomainFactDto(
    string Kind,
    int    Line,
    string? Method,
    string? Route,
    string? Name,
    string? TypeRef,
    string? OwnerType,
    IReadOnlyList<string> Items,
    string FileId,
    string RelativePath);

public record PackageDependencyDto(string Name, string Version, bool IsDev);

public record ProjectManifestDto(
    string ManifestType,
    string ManifestPath,
    IReadOnlyList<string> TargetFrameworks,
    string? OutputKind,
    string? LangVersion,
    string? Nullable,
    bool   ImplicitUsings,
    IReadOnlyList<PackageDependencyDto> Packages,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyDictionary<string, string> Scripts);

public record SemanticSymbolHitDto(
    string  Id,
    string  SymbolName,
    string? ContainingType,
    string  Kind,
    string  FileId,
    string  RelativePath,
    int     Line,
    int     EndLine,
    float   Score);

public record IntelligenceOverviewDto(
    int Files,
    int Symbols,
    int Endpoints,
    int DiEdges,
    int EfEntities,
    int MediatrMessages,
    int TypeRelations,
    int ConfigKeys,
    int SecuritySinks,
    int TestFiles,
    int Packages,
    int TypeScriptFilesWithoutTypes);

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
