using AgenticMemory.Brain.Interfaces;
using AgenticMemory.CodeIndex;
using AgenticMemory.Configuration;
using AgenticMemory.Models;
using Spectre.Console;

namespace AgenticMemory.Extensions;

/// <summary>
/// Extension methods for configuring application endpoints.
/// </summary>
public static class WebApplicationExtensions
{
    /// <summary>
    /// Maps all REST API endpoints for backward compatibility.
    /// </summary>
    public static WebApplication MapRestApiEndpoints(this WebApplication app)
    {
        app.MapHealthEndpoints();
        app.MapMemoryEndpoints();
        app.MapSearchEndpoints();
        app.MapAdminEndpoints();
        app.MapGenerationEndpoints();
        app.MapKeyValueEndpoints();
        app.MapFileBrowserEndpoints();
        app.MapFileContextEndpoints();
        app.MapProjectEndpoints();
        app.MapCodeIndexEndpoints();

        return app;
    }

    private static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/health", () =>
            Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));
    }

    private static void MapMemoryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/memory", async (IMemoryRepository repository, bool? includeArchived, CancellationToken ct) =>
        {
            var all = await repository.GetAllAsync(ct);
            var filtered = includeArchived == true
                ? all
                : all.Where(m => !m.IsArchived);
            return Results.Ok(filtered.OrderByDescending(m => m.CreatedAt));
        });

        app.MapGet("/api/memory/{id:guid}", async (Guid id, IMemoryRepository repository, CancellationToken ct) =>
        {
            var memory = await repository.GetAsync(id, ct);
            return memory is null ? Results.NotFound() : Results.Ok(memory);
        });

        app.MapPost("/api/memory", async (MemoryCreateRequest request, IConflictAwareStorage storage, CancellationToken ct) =>
        {
            var entity = new Brain.Models.MemoryNodeEntity
            {
                Title = request.Title,
                Summary = request.Summary,
                Content = request.Content ?? "",
                Tags = request.Tags?.ToList() ?? [],
                Importance = request.Importance ?? 0.5
            };

            var result = await storage.StoreAsync(entity, ct);
            return Results.Created($"/api/memory/{result.Memory.Id}", result);
        });

        app.MapPut("/api/memory/{id:guid}", async (Guid id, MemoryUpdateRequest request, IMemoryRepository repository, CancellationToken ct) =>
        {
            var existing = await repository.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();

            if (request.Title is not null) existing.Title = request.Title;
            if (request.Summary is not null) existing.Summary = request.Summary;
            if (request.Content is not null) existing.Content = request.Content;
            if (request.Tags is not null) existing.Tags = request.Tags.ToList();

            await repository.SaveAsync(existing, ct);
            return Results.Ok(existing);
        });

        app.MapDelete("/api/memory/{id:guid}", async (Guid id, IMemoryRepository repository, CancellationToken ct) =>
        {
            var deleted = await repository.DeleteAsync(id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });
    }

    private static void MapSearchEndpoints(this WebApplication app)
    {
        app.MapPost("/api/memory/search", async (SearchRequest request, ISearchService searchService, CancellationToken ct) =>
        {
            var results = await searchService.SearchAsync(request.Query, request.TopN ?? 5, request.Tags, ct);
            return Results.Ok(results);
        });
    }

    private static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/stats", async (IMemoryRepository repository, CancellationToken ct) =>
        {
            var stats = await repository.GetStatsAsync(ct);
            return Results.Ok(stats);
        });

        app.MapGet("/api/admin/status", (
            AppSettings settings,
            IEmbeddingService embeddingService,
            IGenerativeModelService generativeService,
            CodeIndexService? codeIndex,
            HttpContext ctx) =>
        {
            var scheme = ctx.Request.Scheme;
            var host   = ctx.Request.Host.Host;
            var port   = settings.Server.Port;
            var listeningUrl = $"{scheme}://{host}:{port}";

            var genModelName = Path.GetFileName(
                settings.Generation.ModelsPath.TrimEnd('/', '\\'));

            var providers = codeIndex?.Providers
                .Select(p => new ProviderStatusEntry(
                    ProviderType: p.ProviderType,
                    CompilerApi: p.Capabilities.CompilerApi,
                    DomainPatternFamilies: p.Capabilities.DomainPatternFamilies,
                    Active: IsProviderActive(p, settings.CodeIndex)))
                .ToList()
                ?? (IReadOnlyList<ProviderStatusEntry>)[];

            return Results.Ok(new SystemStatusResponse(
                Status: "healthy",
                Timestamp: DateTime.UtcNow.ToString("O"),
                Server: new(listeningUrl),
                Generation: new(
                    Enabled: settings.Generation.Enabled,
                    Available: generativeService.IsAvailable,
                    ModelName: generativeService.IsAvailable ? genModelName : null),
                Embeddings: new(
                    Enabled: settings.Embeddings.Enabled,
                    Available: embeddingService.IsAvailable,
                    ModelName: settings.Embeddings.ModelFileName,
                    Dimensions: settings.Embeddings.ModelDimensions),
                Maintenance: new(settings.Maintenance.Enabled),
                CodeIndex: new(
                    Enabled: settings.CodeIndex.Enabled,
                    Providers: providers)));
        });
    }

    private static bool IsProviderActive(ICodeIntelligenceProvider provider, CodeIndexSettings settings)
    {
        if (provider.ProviderType != "typescript-react-native-expo") return true;
        var cachedPath = Path.Combine(settings.TypeScriptModelsPath, "typescript.js");
        return (!string.IsNullOrEmpty(settings.TypeScriptCompilerPath) && File.Exists(settings.TypeScriptCompilerPath))
               || File.Exists(cachedPath);
    }

    private static void MapGenerationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/generate/status", (IGenerativeModelService svc) =>
            Results.Ok(new { available = svc.IsAvailable }));

        app.MapPost("/api/generate", (GenerateRequest request, IGenerativeModelService svc, AppSettings settings) =>
        {
            if (!svc.IsAvailable)
                return Results.Problem(
                    "Generative model is not available. Set Generation.Enabled and Generation.AutoDownload to true in appsettings.json.",
                    statusCode: 503);

            if (string.IsNullOrWhiteSpace(request.UserPrompt))
                return Results.BadRequest(new { error = "UserPrompt is required." });

            var userPrompt = settings.Generation.TruncateIfNeeded(request.UserPrompt);

            var result = svc.Generate(
                request.SystemPrompt ?? "You are a helpful assistant.",
                userPrompt);

            return Results.Ok(new { result });
        });

        // Server-Sent Events streaming endpoint — streams tokens as they are generated.

        app.MapPost("/api/generate/stream", async (
            GenerateRequest request,
            IGenerativeModelService svc,
            AppSettings settings,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!svc.IsAvailable)
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsync("data: {\"error\":\"Generative model not available\"}\n\n", ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(request.UserPrompt))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("data: {\"error\":\"UserPrompt is required\"}\n\n", ct);
                return;
            }

            var userPrompt = settings.Generation.TruncateIfNeeded(request.UserPrompt);

            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            try
            {
                await foreach (var token in svc.GenerateStreamingAsync(
                    request.SystemPrompt ?? "You are a helpful assistant.",
                    userPrompt,
                    ct))
                {
                    var escaped = System.Text.Json.JsonSerializer.Serialize(token);
                    await ctx.Response.WriteAsync($"data: {{\"token\":{escaped}}}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }

                await ctx.Response.WriteAsync("data: [DONE]\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Client disconnected — normal, no action needed
            }
        });
    }

    private static void MapKeyValueEndpoints(this WebApplication app)
    {
        app.MapGet("/api/kv/{key}", (string key, IKeyValueStore kv) =>
        {
            var value = kv.Get(key);
            return Results.Ok(new { value });
        });

        app.MapPut("/api/kv/{key}", (string key, KvSetRequest request, IKeyValueStore kv) =>
        {
            if (request.Value is null)
                return Results.BadRequest(new { error = "value is required." });
            kv.Set(key, request.Value);
            return Results.NoContent();
        });

        app.MapDelete("/api/kv/{key}", (string key, IKeyValueStore kv) =>
        {
            kv.Delete(key);
            return Results.NoContent();
        });
    }

    private sealed record KvSetRequest(string? Value);

    private const string LastBrowsePathKey = "fileBrowser.lastPath";

    private static void MapFileBrowserEndpoints(this WebApplication app)
    {
        app.MapGet("/api/files/browse", (string? path, IKeyValueStore kv) =>
        {
            string dir;

            if (string.IsNullOrWhiteSpace(path))
                dir = Directory.GetCurrentDirectory();
            else if (File.Exists(path))
                dir = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
            else if (Directory.Exists(path))
                dir = path;
            else
                dir = Directory.GetCurrentDirectory();

            try
            {
                var info = new DirectoryInfo(dir);

                var dirs = info.GetDirectories()
                    .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden) && !d.Name.StartsWith('.'))
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(d => new FsItem(d.Name, d.FullName, true, null));

                var files = info.GetFiles()
                    .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden) && !f.Name.StartsWith('.'))
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new FsItem(f.Name, f.FullName, false, f.Extension.ToLowerInvariant()));

                var items = dirs.Concat(files).ToList();

                kv.Set(LastBrowsePathKey, info.FullName);

                return Results.Ok(new
                {
                    path = info.FullName,
                    parent = info.Parent?.FullName,
                    items
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Problem("Access denied to this directory.", statusCode: 403);
            }
        });
    }

    private sealed record FsItem(string Name, string FullPath, bool IsDirectory, string? Extension);

    private static void MapFileContextEndpoints(this WebApplication app)
    {
        app.MapGet("/api/file/context", async (string path, CodeIndexService codeIndex, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path query parameter is required." });

            var context = await codeIndex.ExtractContextAsync(path, ct);

            if (string.IsNullOrEmpty(context))
                return Results.NotFound(new { error = "File not found, too large, binary, or empty." });

            return Results.Ok(new { context });
        });

        app.MapPost("/api/file/summary", async (
            FileSummaryRequest request,
            IGenerativeModelService svc,
            CodeIndexService codeIndex,
            AppSettings settings,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.FilePath))
                return Results.BadRequest(new { error = "FilePath is required." });

            if (!svc.IsAvailable)
                return Results.Problem(
                    "Generative model is not available. Set Generation.Enabled and Generation.AutoDownload to true in appsettings.json.",
                    statusCode: 503);

            var context = await codeIndex.ExtractContextAsync(request.FilePath, ct);

            if (string.IsNullOrEmpty(context))
                return Results.NotFound(new { error = "File not found, too large, binary, or empty." });

            // Classifier-aware system prompt per summary-and-file-context-improvement.md §5
            const string systemPrompt =
                "You are a code indexing assistant. Your output becomes an embedding vector used for semantic search. " +
                "A developer will search the codebase by describing what they need; your summary must contain the " +
                "specific terms that match their query.\n\n" +
                "Rules:\n" +
                "- Write exactly 1–2 prose sentences. No bullet points. No hyphens as list markers. " +
                "No labeled sections. No line breaks within the output.\n" +
                "- Under 60 words. Hard limit.\n" +
                "- Lead with the file's specific role: name the actual framework, library, domain entity, or protocol. " +
                "Not 'React component' — 'React chat page streaming via SSE'. " +
                "Not 'C# service' — 'C# service consolidating duplicate memory embeddings by cosine similarity'.\n" +
                "- Use the proper nouns in the structural summary: endpoint paths, domain type names, library names, " +
                "protocol names (SSE, WebSocket, JWT), specific query keys.\n" +
                "- Do not mention React hooks by name unless the file IS a custom hook. " +
                "Do not write 'component rendering', 'event handling', or 'state management' as standalone concepts.";

            // Inject file-class hint to steer LLM toward discriminating details
            var fileClassHint = "";
            var firstContextLine = context.Split('\n', 2)[0];
            var bracketStart = firstContextLine.IndexOf('[');
            var bracketEnd = firstContextLine.IndexOf(']');
            if (bracketStart >= 0 && bracketEnd > bracketStart)
            {
                var fileClass = firstContextLine[(bracketStart + 1)..bracketEnd];
                fileClassHint = fileClass switch
                {
                    "react-component" => "This is a React component. Focus on: what UI it renders, what props or state it accepts, and what user interactions or side-effects it handles.",
                    "react-page"    => "This is a React page component. Focus on: what domain action it performs, what API endpoints it calls, what data it streams or displays.",
                    "react-hook"    => "This is a React custom hook. Focus on: what state it manages, what it fetches or mutates, what it returns to consumers.",
                    "api-client"    => "This is an API client module. Focus on: the base URL pattern, the endpoints exposed, and the response types.",
                    "cs-controller" => "This is an ASP.NET controller. Focus on: the route prefix and the HTTP operations it exposes.",
                    "cs-service"    => "This is a C# service. Focus on: the business operations it provides and the dependencies it orchestrates.",
                    "cs-repository" => "This is a data access repository. Focus on: the entity it manages and the query and write operations it provides.",
                    "cs-entity"     => "This is a domain entity. Focus on: its fields, key types, and any computed or navigational properties.",
                    _               => ""
                };
                if (!string.IsNullOrEmpty(fileClassHint))
                    AnsiConsole.MarkupLine($"  [blue dim]inf[/] [grey dim]FileSummary[/] Class tag [green][[{Markup.Escape(fileClass)}]][/] detected for [dim]{Markup.Escape(Path.GetFileName(request.FilePath))}[/] — using targeted hint");
                else
                    AnsiConsole.MarkupLine($"  [yellow]wrn[/] [grey dim]FileSummary[/] Unknown class tag [yellow][[{Markup.Escape(fileClass)}]][/] for [dim]{Markup.Escape(Path.GetFileName(request.FilePath))}[/] — generating generic summary");
            }
            else
            {
                AnsiConsole.MarkupLine($"  [yellow]wrn[/] [grey dim]FileSummary[/] No class tag in context for [dim]{Markup.Escape(Path.GetFileName(request.FilePath))}[/] — static extractor used, generating generic summary");
            }

            var userPrompt = settings.Generation.TruncateIfNeeded(
                string.IsNullOrEmpty(fileClassHint)
                    ? $"Describe this file:\n\n{context}"
                    : $"Hint: {fileClassHint}\n\nDescribe this file:\n\n{context}");

            var summary = svc.Generate(systemPrompt, userPrompt);

            // Hard word-count enforcement (per summary-and-file-context-improvement.md §5)
            summary = EnforceWordLimit(summary, maxWords: 65);

            return Results.Ok(new { summary });
        });
    }

    private static string EnforceWordLimit(string text, int maxWords)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= maxWords) return text;
        for (var i = maxWords - 1; i >= maxWords - 10 && i >= 0; i--)
        {
            if (words[i].EndsWith('.') || words[i].EndsWith('!') || words[i].EndsWith('?'))
                return string.Join(' ', words[..(i + 1)]);
        }
        return string.Join(' ', words[..maxWords]) + ".";
    }

    /// <summary>
    /// Re-registers all projects previously saved via POST /api/projects with the CodeIndexService.
    /// Must be called after app.Build() so the KV store and CodeIndexService are available.
    /// Runs as a fire-and-forget background task so it does not delay startup.
    /// </summary>
    public static void ReRegisterSavedProjects(this WebApplication app)
    {
        var kv    = app.Services.GetRequiredService<IKeyValueStore>();
        var index = app.Services.GetService<CodeIndexService>();
        if (index is null) return;

        var json = kv.Get(ProjectsStoreKey);
        if (string.IsNullOrEmpty(json)) return;

        List<string> roots = [];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("RootPath", out var rp))
                {
                    var path = rp.GetString();
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                        roots.Add(path);
                }
            }
        }
        catch { return; }

        if (roots.Count == 0) return;

        AnsiConsole.MarkupLine($"  [blue dim]inf[/] [grey dim]Projects[/] Re-registering [white]{roots.Count}[/] saved project(s) with CodeIndex (background)...");
        _ = Task.Run(async () =>
        {
            foreach (var root in roots)
                await index.RegisterProjectAsync(root);
        });
    }

    private const string ProjectsStoreKey = "projects";

    private sealed record ProjectRecord(string Id, string Name, string RootPath, string CreatedAt);

    private static List<ProjectRecord> LoadProjects(IKeyValueStore kv)
    {
        var json = kv.Get(ProjectsStoreKey);
        return string.IsNullOrEmpty(json)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<List<ProjectRecord>>(json) ?? [];
    }

    private static void SaveProjects(IKeyValueStore kv, List<ProjectRecord> projects) =>
        kv.Set(ProjectsStoreKey, System.Text.Json.JsonSerializer.Serialize(projects));

    private static void MapCodeIndexEndpoints(this WebApplication app)
    {
        // ── Active project ────────────────────────────────────────────────────

        app.MapPost("/api/projects/{id}/activate", (
            string id,
            IKeyValueStore kv,
            ActiveProjectService activeProject,
            ICodeIndexRepository repo) =>
        {
            var project = LoadProjects(kv).Find(p => p.Id == id);
            if (project is null) return Results.NotFound();

            activeProject.SetActive(id);

            var (indexed, _, _) = repo.GetProjectStatsAsync(id).GetAwaiter().GetResult();
            return Results.Ok(new ProjectActivateResponse(
                ProjectId: project.Id,
                Name: project.Name,
                RootPath: project.RootPath,
                QueuedFiles: 0,
                AlreadyIndexed: indexed));
        });

        app.MapDelete("/api/projects/active", (ActiveProjectService activeProject) =>
        {
            activeProject.SetActive(null);
            return Results.NoContent();
        });

        app.MapGet("/api/codeindex/active", (
            IKeyValueStore kv,
            ActiveProjectService activeProject) =>
        {
            var id = activeProject.ActiveProjectId;
            if (string.IsNullOrEmpty(id)) return Results.NoContent();
            var project = LoadProjects(kv).Find(p => p.Id == id);
            if (project is null) return Results.NoContent();
            return Results.Ok(new { projectId = project.Id, name = project.Name, rootPath = project.RootPath });
        });

        // ── Worker status ─────────────────────────────────────────────────────

        app.MapGet("/api/codeindex/worker/status", (
            WorkerStatusTracker tracker,
            ICodeIndexRepository repo) =>
        {
            var snap = tracker.GetSnapshot(repo);
            return Results.Ok(new WorkerStatusResponse(
                ActiveProjectId: snap.ActiveProjectId,
                ActiveProjectName: snap.ActiveProjectName,
                IsProcessing: snap.IsProcessing,
                CurrentFile: snap.CurrentFile,
                QueueDepth: snap.QueueDepth,
                SummaryQueueDepth: snap.SummaryQueueDepth,
                TotalIndexableFiles: snap.TotalIndexableFiles,
                IndexedFiles: snap.IndexedFiles,
                StaleFiles: snap.StaleFiles,
                ErrorFiles: snap.ErrorFiles,
                RecentJobs: snap.RecentJobs
                    .Select(j => new RecentJobEntryDto(
                        j.RelativePath, j.Language, j.SymbolCount,
                        j.DurationMs, j.IndexedAt.ToString("O"), j.WasNew))
                    .ToList(),
                RecentErrors: snap.RecentErrors
                    .Select(e => new RecentErrorEntryDto(
                        e.RelativePath, e.Error, e.OccurredAt.ToString("O")))
                    .ToList()));
        });

        // ── File index queries ────────────────────────────────────────────────

        app.MapGet("/api/projects/{id}/files", async (
            string id,
            string? search,
            ICodeIndexRepository repo,
            IEmbeddingService embedding,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(search))
            {
                var all = await repo.GetByProjectAsync(id, ct);
                return Results.Ok(all.Select(r => new CodeIndexFileResponse(
                    r.Id, r.ProjectId, r.FilePath, r.FileName, r.RelativePath,
                    r.Language, r.ProviderType, r.ExtractedContext, r.LlmSummary,
                    r.Symbols, r.IndexedAt, r.FileModifiedAt, r.IsStale, r.IngestionError)));
            }

            // Run semantic and lexical lanes in parallel
            var semanticTask = embedding.IsAvailable
                ? embedding.GetEmbeddingAsync(search, ct)
                    .ContinueWith(t => repo.SearchByEmbeddingAsync(t.Result, id, 50, ct), ct)
                    .Unwrap()
                : Task.FromResult<IReadOnlyList<(CodeIndexRecord Record, float Score)>>([]);

            var lexicalTask = repo.SearchLexicalAsync(search, id, ct);

            await Task.WhenAll(semanticTask, lexicalTask);

            var semanticRanked = semanticTask.Result
                .Select((x, i) => (x.Record.Id, SemanticRank: i + 1, x.Score))
                .ToList();

            var lexicalRanked = lexicalTask.Result
                .Select((r, i) => (r.Id, LexicalRank: i + 1))
                .ToList();

            // Reciprocal Rank Fusion (k=60)
            const int K = 60;
            var allIds = semanticRanked.Select(x => x.Id)
                .Union(lexicalRanked.Select(x => x.Id))
                .Distinct()
                .ToList();

            var semDict = semanticRanked.ToDictionary(x => x.Id, x => (x.SemanticRank, x.Score));
            var lexDict = lexicalRanked.ToDictionary(x => x.Id, x => x.LexicalRank);

            var merged = allIds
                .Select(rid =>
                {
                    var semScore = semDict.TryGetValue(rid, out var s) ? 1f / (K + s.SemanticRank) : 0f;
                    var lexScore = lexDict.TryGetValue(rid, out var lr) ? 1f / (K + lr) : 0f;
                    return (Id: rid, Rrf: semScore + lexScore);
                })
                .OrderByDescending(x => x.Rrf)
                .Take(50)
                .ToList();

            var recordMap = (await repo.GetByIdsAsync(merged.Select(x => x.Id).ToList(), ct))
                .ToDictionary(r => r.Id);

            var hits = merged.Where(x => recordMap.ContainsKey(x.Id)).ToList();
            var total = hits.Count;

            return Results.Ok(hits
                .Select((x, rankIdx) =>
                {
                    var r = recordMap[x.Id];
                    // Rank-normalized score: rank #1 = 1.0, last = 1/total; always positive and meaningful
                    var displayScore = total > 1 ? (float)(total - rankIdx) / total : 1f;
                    return new CodeIndexFileResponse(
                        r.Id, r.ProjectId, r.FilePath, r.FileName, r.RelativePath,
                        r.Language, r.ProviderType, r.ExtractedContext, r.LlmSummary,
                        r.Symbols, r.IndexedAt, r.FileModifiedAt, r.IsStale, r.IngestionError,
                        displayScore);
                }));
        });

        app.MapGet("/api/codeindex/file", async (
            string path,
            ICodeIndexRepository repo,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path is required." });
            var record = await repo.GetByPathAsync(path, ct);
            if (record is null) return Results.NotFound();
            return Results.Ok(new CodeIndexFileResponse(
                record.Id, record.ProjectId, record.FilePath, record.FileName,
                record.RelativePath, record.Language, record.ProviderType,
                record.ExtractedContext, record.LlmSummary, record.Symbols,
                record.IndexedAt, record.FileModifiedAt, record.IsStale, record.IngestionError));
        });

        app.MapPost("/api/projects/{id}/reindex", async (
            string id,
            bool? force,
            IKeyValueStore kv,
            ActiveProjectService activeProject,
            StalenessScanner scanner,
            ICodeIndexRepository repo,
            WorkerStatusTracker tracker,
            CancellationToken ct) =>
        {
            var project = LoadProjects(kv).Find(p => p.Id == id);
            if (project is null) return Results.NotFound();

            if (activeProject.ActiveProjectId != id)
                activeProject.SetActive(id);

            if (force == true)
                await repo.MarkProjectStaleAsync(id, ct);

            var (queued, current) = await scanner.ScanAsync(id, project.RootPath, ct);
            tracker.SetTotalIndexable(queued + current);

            return Results.Ok(new { queued, alreadyCurrent = current });
        });

        app.MapPost("/api/codeindex/ingest", (
            IngestFileRequest request,
            IIngestionQueue queue,
            IKeyValueStore kv) =>
        {
            if (string.IsNullOrWhiteSpace(request.FilePath) || string.IsNullOrWhiteSpace(request.ProjectId))
                return Results.BadRequest(new { error = "FilePath and ProjectId are required." });

            var project = LoadProjects(kv).Find(p => p.Id == request.ProjectId);
            queue.TryEnqueue(new IngestionJob(
                request.FilePath, request.ProjectId, project?.RootPath, request.Force));
            return Results.Accepted();
        });
    }

    private static void MapProjectEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects", (IKeyValueStore kv) =>
            Results.Ok(LoadProjects(kv)));

        app.MapGet("/api/projects/{id}", (string id, IKeyValueStore kv) =>
        {
            var project = LoadProjects(kv).Find(p => p.Id == id);
            return project is null ? Results.NotFound() : Results.Ok(project);
        });

        app.MapPost("/api/projects", async (
            ProjectCreateRequest request,
            IKeyValueStore kv,
            CodeIndexService codeIndex,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Name is required." });
            if (string.IsNullOrWhiteSpace(request.RootPath))
                return Results.BadRequest(new { error = "RootPath is required." });
            if (!Directory.Exists(request.RootPath))
                return Results.BadRequest(new { error = "RootPath does not exist." });

            var projects = LoadProjects(kv);
            var project = new ProjectRecord(
                Id: Guid.NewGuid().ToString(),
                Name: request.Name.Trim(),
                RootPath: request.RootPath.Trim(),
                CreatedAt: DateTime.UtcNow.ToString("O"));

            projects.Add(project);
            SaveProjects(kv, projects);

            // Register with code index so TypeScript/C# providers build their per-project index.
            // CancellationToken.None: registration outlives the HTTP request.
            AnsiConsole.MarkupLine($"  [blue dim]inf[/] [grey dim]Projects[/] Registering [white]{Markup.Escape(project.Name)}[/] at [dim]{Markup.Escape(project.RootPath)}[/] with CodeIndex...");
            _ = codeIndex.RegisterProjectAsync(project.RootPath, CancellationToken.None);

            return Results.Created($"/api/projects/{project.Id}", project);
        });

        app.MapDelete("/api/projects/{id}", (string id, IKeyValueStore kv) =>
        {
            var projects = LoadProjects(kv);
            var idx = projects.FindIndex(p => p.Id == id);
            if (idx < 0) return Results.NotFound();
            projects.RemoveAt(idx);
            SaveProjects(kv, projects);
            return Results.NoContent();
        });
    }
}
