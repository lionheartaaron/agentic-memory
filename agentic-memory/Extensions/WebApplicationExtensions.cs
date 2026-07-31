using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Retrieval;
using AgenticMemory.Brain.Slots;
using AgenticMemory.Brain.Storage;
using AgenticMemory.CodeIndex;
using AgenticMemory.Configuration;
using AgenticMemory.Models;
using AgenticMemory.Persistence;
using AgenticMemory.Persistence.Migrations;
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
        app.MapWorkspaceEndpoints();
        app.MapProjectEndpoints();
        app.MapCodeIndexEndpoints();
        app.MapIntelligenceEndpoints();

        return app;
    }

    private static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/health", () =>
            Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));
    }

    /// <summary>
    /// Builds the scope for a request. Every memory endpoint runs inside one; there is no
    /// unscoped path through this API.
    /// </summary>
    private static MemoryScope ScopeFrom(string? userId, string? companionId) =>
        string.IsNullOrWhiteSpace(companionId)
            ? MemoryScope.AllFor(userId)
            : MemoryScope.For(userId, companionId);

    private static void MapMemoryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/memory", async (
            IMemoryRepository repository,
            bool? includeArchived, string? userId, string? companionId,
            CancellationToken ct) =>
        {
            var memories = await repository.QueryAsync(
                ScopeFrom(userId, companionId),
                new MemoryQueryOptions { IncludeNonCurrent = includeArchived == true },
                ct);

            return Results.Ok(memories.OrderByDescending(m => m.CreatedAt));
        });

        app.MapGet("/api/memory/{id:guid}", async (
            Guid id, IMemoryRepository repository, string? userId, string? companionId, CancellationToken ct) =>
        {
            var memory = await repository.GetAsync(id, ScopeFrom(userId, companionId), ct);
            return memory is null ? Results.NotFound() : Results.Ok(memory);
        });

        app.MapPost("/api/memory", async (
            MemoryCreateRequest request, IConflictAwareStorage storage, CancellationToken ct) =>
        {
            var wantsPrivate = request.Visibility is not null
                && (request.Visibility.Equals("private", StringComparison.OrdinalIgnoreCase)
                 || request.Visibility.Equals("scoped", StringComparison.OrdinalIgnoreCase));

            if (wantsPrivate && string.IsNullOrWhiteSpace(request.CompanionId))
                return Results.BadRequest(new { error = "companionId is required when visibility is 'private'." });

            var entity = new MemoryNodeEntity
            {
                UserId       = MemoryScope.NormalizeUser(request.UserId),
                Title        = request.Title,
                Summary      = request.Summary,
                Content      = request.Content ?? "",
                Tags         = request.Tags?.ToList() ?? [],
                Importance   = request.Importance ?? 0.5,
                Visibility   = wantsPrivate ? MemoryVisibility.Scoped : MemoryVisibility.Global,
                CompanionIds = wantsPrivate ? [MemoryScope.NormalizeId(request.CompanionId)!] : [],
                SubjectRef   = SubjectRefs.Normalize(request.Subject),
                Predicate    = SlotRegistry.Normalize(request.Predicate),
                ValueKey     = MemoryTextIndexer.BuildValueKey(request.Value ?? request.Summary),
                IsPinned     = request.Pinned ?? false,
                Type         = Enum.TryParse<MemoryType>(request.Type, true, out var t) ? t : MemoryType.Semantic,
                Source       = Enum.TryParse<MemorySource>(request.Source?.Replace("_", ""), true, out var s)
                                   ? s : MemorySource.UserStated,
            };

            var scope  = MemoryScope.For(request.UserId, request.CompanionId);
            var result = await storage.StoreAsync(entity, scope, "api:POST /api/memory", ct);
            return Results.Created($"/api/memory/{result.Memory.Id}", result);
        });

        app.MapPut("/api/memory/{id:guid}", async (
            Guid id, MemoryUpdateRequest request, IMemoryRepository repository,
            string? userId, string? companionId, CancellationToken ct) =>
        {
            var scope = ScopeFrom(userId, companionId);
            var existing = await repository.GetAsync(id, scope, ct);
            if (existing is null) return Results.NotFound();

            if (request.Title is not null) existing.Title = request.Title;
            if (request.Summary is not null) existing.Summary = request.Summary;
            if (request.Content is not null) existing.Content = request.Content;
            if (request.Tags is not null) existing.Tags = request.Tags.ToList();

            try
            {
                await repository.SaveAsync(existing, existing.Version, "api:PUT /api/memory", ct);
            }
            catch (MemoryConcurrencyException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            return Results.Ok(existing);
        });

        // Soft delete. Physical removal happens only via the retention purge, and the audit event
        // outlives the row.
        app.MapDelete("/api/memory/{id:guid}", async (
            Guid id, IMemoryRepository repository, string? userId, string? companionId, CancellationToken ct) =>
        {
            var ok = await repository.ForgetAsync(id, ScopeFrom(userId, companionId), "api:DELETE /api/memory", ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        app.MapPost("/api/memory/{id:guid}/restore", async (
            Guid id, IMemoryRepository repository, string? userId, string? companionId, CancellationToken ct) =>
        {
            var ok = await repository.RestoreAsync(id, ScopeFrom(userId, companionId), "api:restore", ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        app.MapGet("/api/memory/{id:guid}/history", async (
            Guid id, IMemoryEventLog eventLog, CancellationToken ct) =>
            Results.Ok(await eventLog.GetForMemoryAsync(id, ct)));

        app.MapGet("/api/memory/slot", async (
            IMemoryRepository repository, string predicate, string? subject, string? userId, string? companionId,
            CancellationToken ct) =>
        {
            var history = await repository.GetBySlotAsync(
                ScopeFrom(userId, companionId), subject ?? SubjectRefs.User, predicate, includeHistory: true, ct);
            return Results.Ok(history);
        });

        // ── Conflicts ─────────────────────────────────────────────────────────────────────────

        app.MapGet("/api/memory/conflicts", async (
            IMemoryRepository repository, string? userId, string? companionId, bool? openOnly, CancellationToken ct) =>
            Results.Ok(await repository.GetConflictsAsync(ScopeFrom(userId, companionId), openOnly ?? true, ct)));

        app.MapPost("/api/memory/conflicts/{id:guid}/resolve", async (
            Guid id, ConflictResolveRequest request, IMemoryRepository repository, CancellationToken ct) =>
        {
            var ok = await repository.ResolveConflictAsync(
                id, ScopeFrom(request.UserId, request.CompanionId),
                request.WinnerId, request.Dismiss, "api:resolve-conflict", ct);

            return ok ? Results.NoContent() : Results.NotFound();
        });

        app.MapGet("/api/memory/slots", (SlotRegistry slots) =>
            Results.Ok(slots.All.OrderBy(s => s.Predicate)));
    }

    private static void MapSearchEndpoints(this WebApplication app)
    {
        app.MapPost("/api/memory/search", async (
            SearchRequest request, ISearchService searchService, CancellationToken ct) =>
        {
            var result = await searchService.RetrieveAsync(new RetrievalRequest
            {
                Query              = request.Query,
                Scope              = ScopeFrom(request.UserId, request.CompanionId),
                TopN               = request.TopN ?? 5,
                Tags               = request.Tags,
                SubjectRef         = request.Subject,
                Predicate          = request.Predicate,
                IncludeCoreContext = request.IncludeCoreContext,
                AsOf               = request.AsOf,
                NoveltyBias        = Math.Clamp(request.NoveltyBias, 0, 1),
            }, ct);

            // The dashboard binds the flat result list; the richer envelope is additive.
            return Results.Ok(new
            {
                results     = result.Results,
                coreContext = result.CoreContext,
                conflicts   = result.Conflicts,
                confidence  = result.Confidence.ToString(),
                candidatesConsidered   = result.CandidatesConsidered,
                semanticSearchUsed     = result.SemanticSearchUsed,
                incomparableEmbeddings = result.IncomparableEmbeddings,
            });
        });
    }

    private static long? FileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : null; }
        catch { return null; }
    }

    private static void MapAdminEndpoints(this WebApplication app)
    {
        // Unscoped aggregate: administrative by definition, so it goes through the admin store
        // rather than the scoped repository.
        app.MapGet("/api/admin/stats", async (
            IMemoryAdminStore adminStore, string? userId, IMemoryRepository repository, CancellationToken ct) =>
        {
            var stats = string.IsNullOrWhiteSpace(userId)
                ? await adminStore.GetGlobalStatsAsync(ct)
                : await repository.GetStatsAsync(MemoryScope.AllFor(userId), ct);

            return Results.Ok(stats);
        });

        app.MapGet("/api/admin/users", async (IMemoryAdminStore adminStore, CancellationToken ct) =>
            Results.Ok(await adminStore.GetUserIdsAsync(ct)));

        // ── Maintenance ───────────────────────────────────────────────────────

        app.MapGet("/api/admin/maintenance-stats", async (
            IMemoryAdminStore memRepo,
            ICodeIndexRepository codeRepo,
            IKeyValueStore kv,
            CancellationToken ct) =>
        {
            var memStats = await memRepo.GetGlobalStatsAsync(ct);
            var workspaces = LoadWorkspaces(kv);
            var codeTotal = 0;
            foreach (var ws in workspaces)
                codeTotal += await codeRepo.CountAsync(ws.Id, ct);
            return Results.Ok(new
            {
                memories      = memStats.TotalNodes,
                codeIndexFiles= codeTotal,
                workspaces    = workspaces.Count,
                dbSizeBytes   = memStats.DatabaseSizeBytes,
            });
        });

        app.MapDelete("/api/admin/code-index", async (
            ICodeIndexRepository codeRepo,
            ActiveProjectService activeProject,
            WorkerStatusTracker tracker,
            IIngestionQueue ingestionQueue,
            IReferenceQueue referenceQueue,
            CancellationToken ct) =>
        {
            await codeRepo.DeleteAllAsync(ct);
            await codeRepo.DeleteAllSymbolReferencesAsync(ct);
            await codeRepo.DeleteAllDomainFactsAsync(ct);
            await codeRepo.DeleteAllProjectManifestsAsync(ct);
            await codeRepo.DeleteAllSymbolEmbeddingsAsync(ct);
            activeProject.SetActive(null);
            ingestionQueue.Clear();
            referenceQueue.Clear();
            tracker.Reset();
            return Results.NoContent();
        });

        app.MapDelete("/api/admin/memories", async (
            IMemoryAdminStore memRepo,
            IMemoryBackupService backups,
            CancellationToken ct) =>
        {
            // Wiping every memory is the single most destructive thing this API can do. It gets a
            // snapshot whatever the caller intended, and the path comes back so the caller knows
            // recovery is possible.
            var snapshot = await backups.CreateSnapshotAsync("admin-delete-all-memories", ct);
            var deleted  = await memRepo.DeleteAllAsync(ct);

            return Results.Ok(new { deleted, snapshot = snapshot?.Path });
        });

        // Where everything actually lives. An embedding host needs this to show the user their data
        // folder, to size it, and to back it up; and when something looks missing, it is the first
        // question worth answering.
        app.MapGet("/api/admin/paths", (AppPaths paths, AppSettings settings, IMemoryBackupService backups) =>
            Results.Ok(new
            {
                // Per-user, and the only thing that has to survive an application update.
                dataDirectory = paths.DataDirectory,
                databasePath  = settings.Storage.DatabasePath,
                backupPath    = backups.BackupDirectory,
                databaseBytes = FileLength(settings.Storage.DatabasePath),

                // Shipped with the build and shared by every user on the machine.
                modelsDirectory = paths.ModelsDirectory,
                embeddingsPath  = settings.Embeddings.ModelsPath,
                generativePath  = settings.Generation.ModelsPath,

                programDirectory = paths.ProgramDirectory,
                origin           = paths.Origin.ToString(),
            }));

        // What the database says about itself. The two versions are reported separately because they
        // answer separate questions: "which build is running" and "what shape is the data in". A host
        // deciding whether it is safe to launch an older sidecar needs the second one.
        app.MapGet("/api/admin/database", (SharedLiteDatabase database) =>
        {
            var stamp = DatabaseStamp.Read(database.Database);

            return Results.Ok(new
            {
                schemaVersion          = stamp?.SchemaVersion ?? DatabaseSchema.Current,
                supportedSchemaVersion = DatabaseSchema.Current,
                appVersion             = AppVersion.Current,

                createdAt              = stamp?.CreatedAt,
                createdByAppVersion    = stamp?.CreatedByAppVersion,
                lastOpenedAt           = stamp?.LastOpenedAt,
                lastOpenedByAppVersion = stamp?.LastOpenedByAppVersion,

                // What this particular start did, and where the snapshot went if it upgraded.
                migratedOnThisStart = database.Migration.Ran,
                migratedFromVersion = database.Migration.FromVersion,
                snapshotPath        = database.Migration.BackupPath,

                history = stamp?.History ?? [],
            });
        });

        app.MapGet("/api/admin/backups", (IMemoryBackupService backups) =>
            Results.Ok(backups.ListSnapshots()));

        app.MapPost("/api/admin/backups", async (IMemoryBackupService backups, CancellationToken ct) =>
        {
            var snapshot = await backups.CreateSnapshotAsync("manual", ct);
            return snapshot is null
                ? Results.Problem("Snapshot failed or backups are disabled.")
                : Results.Ok(snapshot);
        });

        app.MapDelete("/api/admin/workspaces", (IKeyValueStore kv) =>
        {
            kv.Delete(WorkspacesStoreKey);
            return Results.NoContent();
        });

        app.MapPost("/api/admin/full-reset", async (
            IMemoryAdminStore memRepo,
            IMemoryBackupService backups,
            ICodeIndexRepository codeRepo,
            ActiveProjectService activeProject,
            WorkerStatusTracker tracker,
            IIngestionQueue ingestionQueue,
            IReferenceQueue referenceQueue,
            IKeyValueStore kv,
            CancellationToken ct) =>
        {
            // Clear code index
            await codeRepo.DeleteAllAsync(ct);
            await codeRepo.DeleteAllSymbolReferencesAsync(ct);
            await codeRepo.DeleteAllDomainFactsAsync(ct);
            await codeRepo.DeleteAllProjectManifestsAsync(ct);
            await codeRepo.DeleteAllSymbolEmbeddingsAsync(ct);
            activeProject.SetActive(null);
            ingestionQueue.Clear();
            referenceQueue.Clear();
            tracker.Reset();

            // Clear memories, after a snapshot — this endpoint is unrecoverable otherwise.
            await backups.CreateSnapshotAsync("admin-full-reset", ct);
            await memRepo.DeleteAllAsync(ct);

            // Clear workspaces
            kv.Delete(WorkspacesStoreKey);

            return Results.NoContent();
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

        app.MapPost("/api/generate", async (GenerateRequest request, IGenerativeModelService svc, AppSettings settings) =>
        {
            if (!svc.IsAvailable)
                return Results.Problem(
                    "Generative model is not available. Set Generation.Enabled and Generation.AutoDownload to true in appsettings.json.",
                    statusCode: 503);

            if (string.IsNullOrWhiteSpace(request.UserPrompt))
                return Results.BadRequest(new { error = "UserPrompt is required." });

            var userPrompt = settings.Generation.TruncateIfNeeded(request.UserPrompt);

            // Offload the multi-second blocking call off the Kestrel thread-pool thread.
            var result = await Task.Run(() => svc.Generate(
                request.SystemPrompt ?? "You are a helpful assistant.",
                userPrompt));

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

            // Offload the multi-second blocking call off the Kestrel thread-pool thread.
            var summary = await Task.Run(() => svc.Generate(systemPrompt, userPrompt));

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

    // The projects → workspaces reshape used to live here as an unversioned startup fixup. It is now
    // schema step v3 — see ProjectsToWorkspacesStep — so it is recorded in the database, ordered
    // against other schema changes, and covered by the pre-migration snapshot.

    /// <summary>
    /// Re-registers all workspaces previously saved via POST /api/workspaces with CodeIndexService.
    /// Must be called after app.Build(). Runs as fire-and-forget so it does not delay startup.
    /// </summary>
    public static void ReRegisterSavedWorkspaces(this WebApplication app)
    {
        var kv    = app.Services.GetRequiredService<IKeyValueStore>();
        var index = app.Services.GetService<CodeIndexService>();
        if (index is null) return;

        var workspaces = LoadWorkspaces(kv);
        if (workspaces.Count == 0) return;

        AnsiConsole.MarkupLine(
            "  [blue dim]inf[/] [grey dim]Workspaces[/] Re-registering [white]{0}[/] workspace(s)...",
            workspaces.Count);

        _ = Task.Run(async () =>
        {
            foreach (var ws in workspaces)
            {
                if (ws.SubProjects.Count == 0)
                    await index.RegisterProjectAsync(ws.RootPath);
                else
                    foreach (var sub in ws.SubProjects)
                        await index.RegisterSubProjectAsync(sub, CancellationToken.None);
            }
        });
    }

    private const string WorkspacesStoreKey = "workspaces";
    private const string ProjectsStoreKey   = "projects";

    private static List<WorkspaceRecord> LoadWorkspaces(IKeyValueStore kv)
    {
        var json = kv.Get(WorkspacesStoreKey);
        return string.IsNullOrEmpty(json)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<List<WorkspaceRecord>>(json) ?? [];
    }

    private static void SaveWorkspaces(IKeyValueStore kv, List<WorkspaceRecord> ws) =>
        kv.Set(WorkspacesStoreKey, System.Text.Json.JsonSerializer.Serialize(ws));

    // Legacy — kept for backward compat during migration period
    private sealed record ProjectRecord(string Id, string Name, string RootPath, string CreatedAt);

    private static List<ProjectRecord> LoadProjects(IKeyValueStore kv)
    {
        // Transparently delegates to workspaces
        return LoadWorkspaces(kv)
            .Select(w => new ProjectRecord(w.Id, w.Name, w.RootPath, w.CreatedAt))
            .ToList();
    }

    private static void MapCodeIndexEndpoints(this WebApplication app)
    {
        // ── Active project ────────────────────────────────────────────────────

        app.MapPost("/api/projects/{id}/activate", async (
            string id,
            IKeyValueStore kv,
            ActiveProjectService activeProject,
            ICodeIndexRepository repo) =>
        {
            var project = LoadProjects(kv).Find(p => p.Id == id);
            if (project is null) return Results.NotFound();

            activeProject.SetActive(id);

            var (indexed, _, _) = await repo.GetProjectStatsAsync(id);
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
            ICodeIndexRepository repo,
            ActiveProjectService activeProject,
            IKeyValueStore kv) =>
        {
            WorkspaceRecord? workspace = null;
            if (activeProject.ActiveProjectId is { } id)
                workspace = LoadWorkspaces(kv).Find(w => w.Id == id);

            var snap = tracker.GetSnapshot(repo, workspace);
            return Results.Ok(new WorkerStatusResponse(
                ActiveProjectId: snap.ActiveProjectId,
                ActiveProjectName: snap.ActiveProjectName,
                IsProcessing: snap.IsProcessing,
                CurrentFile: snap.CurrentFile,
                CurrentSummaryFile: snap.CurrentSummaryFile,
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
                    .ToList(),
                SubProjectStatuses: snap.SubProjectStatuses
                    .Select(s => new SubProjectStatusDto(
                        s.SubProjectId, s.Name, s.Language,
                        s.IndexedFiles, s.StaleFiles, s.ErrorFiles))
                    .ToList(),
                QueuedIngestions: snap.QueuedIngestions
                    .Select(q => new QueuedFileDto(q.RelativePath, q.FilePath))
                    .ToList(),
                QueuedSummaries: snap.QueuedSummaries
                    .Select(q => new QueuedFileDto(q.RelativePath, q.FilePath))
                    .ToList(),
                CurrentReferenceFile: snap.CurrentReferenceFile,
                ReferenceQueueDepth:  snap.ReferenceQueueDepth,
                TotalSymbolReferences: snap.TotalSymbolReferences));
        });

        // ── File index queries ────────────────────────────────────────────────

        app.MapGet("/api/projects/{id}/files", async (
            string id,
            string? search,
            string? subProjectId,
            ICodeIndexRepository repo,
            IEmbeddingService embedding,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(search))
            {
                var all = string.IsNullOrEmpty(subProjectId)
                    ? await repo.GetByProjectAsync(id, ct)
                    : await repo.GetBySubProjectAsync(subProjectId, ct);
                return Results.Ok(all.Select(r => new CodeIndexFileResponse(
                    r.Id, r.ProjectId, r.FilePath, r.FileName, r.RelativePath,
                    r.Language, r.ProviderType, r.ExtractedContext, r.LlmSummary,
                    r.Symbols, r.IndexedAt, r.FileModifiedAt, r.IsStale, r.IngestionError)));
            }

            // Run semantic and lexical lanes in parallel
            var semanticTask = embedding.IsAvailable
                ? embedding.GetEmbeddingAsync(search, ct)
                    .ContinueWith(t => repo.SearchByEmbeddingAsync(t.Result, id, subProjectId, 50, ct), ct)
                    .Unwrap()
                : Task.FromResult<IReadOnlyList<(CodeIndexRecord Record, float Score)>>([]);

            var lexicalTask = repo.SearchLexicalAsync(search, id, subProjectId, ct);

            await Task.WhenAll(semanticTask, lexicalTask);

            return Results.Ok(MergeAndScore(semanticTask.Result, lexicalTask.Result, search));
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
            {
                await repo.DeleteByProjectAsync(id, ct);
                await repo.DeleteDomainFactsForProjectAsync(id, ct);
                await repo.DeleteProjectManifestsForProjectAsync(id, ct);
                await repo.DeleteSymbolEmbeddingsForProjectAsync(id, ct);
            }

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

    // ── Workspace endpoints ───────────────────────────────────────────────────

    private static void MapWorkspaceEndpoints(this WebApplication app)
    {
        app.MapPost("/api/workspaces", async (
            ProjectCreateRequest request,
            IKeyValueStore kv,
            CodeIndexService codeIndex,
            WorkspaceDiscoveryService discovery,
            AppSettings settings,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Name is required." });
            if (string.IsNullOrWhiteSpace(request.RootPath))
                return Results.BadRequest(new { error = "RootPath is required." });
            if (!Directory.Exists(request.RootPath))
                return Results.BadRequest(new { error = "RootPath does not exist." });

            var id = Guid.NewGuid().ToString();
            var subProjects = await discovery.DiscoverAsync(request.RootPath.Trim(), ct);
            var subProjectsWithOwner = subProjects
                .Select(sp => sp with { WorkspaceId = id })
                .ToList();

            var workspace = new WorkspaceRecord(
                Id:          id,
                Name:        request.Name.Trim(),
                RootPath:    request.RootPath.Trim(),
                CreatedAt:   DateTime.UtcNow.ToString("O"),
                SubProjects: subProjectsWithOwner);

            var workspaces = LoadWorkspaces(kv);
            workspaces.Add(workspace);
            SaveWorkspaces(kv, workspaces);

            AnsiConsole.MarkupLine(
                "  [blue dim]inf[/] [grey dim]Workspaces[/] Created [white]{0}[/] with [white]{1}[/] sub-project(s)",
                Markup.Escape(workspace.Name), workspace.SubProjects.Count);

            _ = Task.Run(async () =>
            {
                foreach (var sub in subProjectsWithOwner)
                    await codeIndex.RegisterSubProjectAsync(sub, CancellationToken.None);
            });

            return Results.Created($"/api/workspaces/{id}",
                ToWorkspaceDto(workspace, settings, codeIndex));
        });

        app.MapGet("/api/workspaces", (IKeyValueStore kv, AppSettings settings, CodeIndexService codeIndex) =>
            Results.Ok(LoadWorkspaces(kv).Select(ws => ToWorkspaceDto(ws, settings, codeIndex))));

        app.MapGet("/api/workspaces/{id}", (
            string id, IKeyValueStore kv, AppSettings settings, CodeIndexService codeIndex) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            return ws is null
                ? Results.NotFound()
                : Results.Ok(ToWorkspaceDto(ws, settings, codeIndex));
        });

        app.MapDelete("/api/workspaces/{id}", (string id, IKeyValueStore kv) =>
        {
            var workspaces = LoadWorkspaces(kv);
            var idx = workspaces.FindIndex(w => w.Id == id);
            if (idx < 0) return Results.NotFound();
            workspaces.RemoveAt(idx);
            SaveWorkspaces(kv, workspaces);
            return Results.NoContent();
        });

        app.MapPost("/api/workspaces/{id}/discover", async (
            string id,
            IKeyValueStore kv,
            WorkspaceDiscoveryService discovery,
            AppSettings settings,
            CodeIndexService codeIndex,
            CancellationToken ct) =>
        {
            var workspaces = LoadWorkspaces(kv);
            var ws = workspaces.Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });

            var (merged, removed) = await discovery.DiscoverAndMergeAsync(
                ws.RootPath, ws.SubProjects, ct);

            var updated = ws with
            {
                SubProjects = merged.Select(sp => sp with { WorkspaceId = id }).ToList()
            };

            var idx = workspaces.FindIndex(w => w.Id == id);
            workspaces[idx] = updated;
            SaveWorkspaces(kv, workspaces);

            _ = Task.Run(async () =>
            {
                foreach (var sub in updated.SubProjects)
                    await codeIndex.RegisterSubProjectAsync(sub, CancellationToken.None);
            });

            return Results.Ok(new
            {
                workspace = ToWorkspaceDto(updated, settings, codeIndex),
                added  = merged.Count - ws.SubProjects.Count(s => merged.Any(m => m.Id == s.Id)),
                removed = removed.Count
            });
        });

        app.MapGet("/api/workspaces/{id}/sub-projects", (
            string id, IKeyValueStore kv, AppSettings settings, CodeIndexService codeIndex) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });
            return Results.Ok(ws.SubProjects.Select(sp => ToSubProjectDto(sp, settings, codeIndex)));
        });

        app.MapPost("/api/workspaces/{id}/activate", async (
            string id,
            IKeyValueStore kv,
            ActiveProjectService activeProject,
            ICodeIndexRepository repo) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });
            activeProject.SetActive(id);
            var (indexed, _, _) = await repo.GetProjectStatsAsync(id);
            return Results.Ok(new ProjectActivateResponse(
                ProjectId: ws.Id,
                Name: ws.Name,
                RootPath: ws.RootPath,
                QueuedFiles: 0,
                AlreadyIndexed: indexed));
        });

        app.MapGet("/api/workspaces/{id}/files", async (
            string id,
            string? search,
            string? subProjectId,
            IKeyValueStore kv,
            ICodeIndexRepository repo,
            IEmbeddingService embedding,
            CancellationToken ct) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });

            if (string.IsNullOrWhiteSpace(search))
            {
                var all = string.IsNullOrEmpty(subProjectId)
                    ? await repo.GetByProjectAsync(id, ct)
                    : await repo.GetBySubProjectAsync(subProjectId, ct);
                return Results.Ok(all.Select(r => ToFileResponse(r)));
            }

            var semanticTask = embedding.IsAvailable
                ? embedding.GetEmbeddingAsync(search, ct)
                    .ContinueWith(t => repo.SearchByEmbeddingAsync(t.Result, id, subProjectId, 50, ct), ct)
                    .Unwrap()
                : Task.FromResult<IReadOnlyList<(CodeIndexRecord Record, float Score)>>([]);

            var lexicalTask = repo.SearchLexicalAsync(search, id, subProjectId, ct);
            await Task.WhenAll(semanticTask, lexicalTask);

            return Results.Ok(MergeAndScore(semanticTask.Result, lexicalTask.Result, search));
        });

        app.MapPost("/api/workspaces/{id}/reindex", async (
            string id,
            bool? force,
            IKeyValueStore kv,
            ActiveProjectService activeProject,
            StalenessScanner scanner,
            ICodeIndexRepository repo,
            WorkerStatusTracker tracker,
            CancellationToken ct) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });

            if (activeProject.ActiveProjectId != id) activeProject.SetActive(id);
            if (force == true)
            {
                await repo.DeleteByProjectAsync(id, ct);
                await repo.DeleteDomainFactsForProjectAsync(id, ct);
                await repo.DeleteProjectManifestsForProjectAsync(id, ct);
                await repo.DeleteSymbolEmbeddingsForProjectAsync(id, ct);
            }

            var (queued, current) = await scanner.ScanWorkspaceAsync(
                id, ws.RootPath, ws.SubProjects, ct);
            tracker.SetTotalIndexable(queued + current);

            return Results.Ok(new { queued, alreadyCurrent = current });
        });

        app.MapPost("/api/workspaces/{id}/sub-projects/{spId}/reindex", async (
            string id, string spId,
            bool? force,
            IKeyValueStore kv,
            ActiveProjectService activeProject,
            StalenessScanner scanner,
            ICodeIndexRepository repo,
            WorkerStatusTracker tracker,
            CancellationToken ct) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });
            var sub = ws.SubProjects.Find(s => s.Id == spId);
            if (sub is null) return Results.NotFound(new { error = "Sub-project not found." });

            if (activeProject.ActiveProjectId != id) activeProject.SetActive(id);
            if (force == true) await repo.MarkSubProjectStaleAsync(spId, ct);

            var (queued, current) = await scanner.ScanSubProjectByIdAsync(
                id, spId, ws.SubProjects, ct);
            tracker.SetTotalIndexable(queued + current);

            return Results.Ok(new { subProjectId = spId, queued, alreadyCurrent = current });
        });

        app.MapGet("/api/workspaces/{id}/stale-files", async (
            string id,
            IKeyValueStore kv,
            ICodeIndexRepository repo,
            CancellationToken ct) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });
            var records = await repo.GetStaleFilesAsync(id, ct);
            return Results.Ok(records.Select(r => ToFileResponse(r)));
        });

        app.MapGet("/api/workspaces/{id}/error-files", async (
            string id,
            IKeyValueStore kv,
            ICodeIndexRepository repo,
            CancellationToken ct) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });
            var records = await repo.GetErrorFilesAsync(id, ct);
            return Results.Ok(records.Select(r => ToFileResponse(r)));
        });
    }

    private static object ToWorkspaceDto(
        WorkspaceRecord ws, AppSettings settings, CodeIndexService codeIndex) => new
    {
        ws.Id, ws.Name, ws.RootPath, ws.CreatedAt,
        SubProjects = ws.SubProjects.Select(sp => ToSubProjectDto(sp, settings, codeIndex))
    };

    private static object ToSubProjectDto(
        SubProjectRecord sp, AppSettings settings, CodeIndexService codeIndex) => new
    {
        sp.Id, sp.WorkspaceId, sp.Name, sp.RootPath,
        Type = sp.Type.ToString(), sp.ManifestPath, sp.Language, sp.Namespace,
        IsProviderAvailable = ComputeProviderAvailable(sp, settings, codeIndex)
    };

    private static bool ComputeProviderAvailable(
        SubProjectRecord sp, AppSettings settings, CodeIndexService codeIndex) =>
        sp.Type switch
        {
            SubProjectType.CSharpProject
                => settings.CodeIndex.EnableCSharpRoslyn,
            SubProjectType.TypeScript or SubProjectType.Node
                => codeIndex.Providers.Any(p =>
                    p.ProviderType.StartsWith("typescript") &&
                    IsProviderActive(p, settings.CodeIndex)),
            _ => false
        };

    private static CodeIndexFileResponse ToFileResponse(CodeIndexRecord r, float? score = null) =>
        new(r.Id, r.ProjectId, r.FilePath, r.FileName, r.RelativePath,
            r.Language, r.ProviderType, r.ExtractedContext, r.LlmSummary,
            r.Symbols, r.IndexedAt, r.FileModifiedAt, r.IsStale, r.IngestionError,
            Score:             score,
            FanIn:             r.FanIn,
            FanOut:            r.FanOut,
            DependsOnFileIds:  r.DependsOnFileIds,
            UsedByFileIds:     r.UsedByFileIds,
            DomainTags:        r.DomainTags.Count    > 0 ? r.DomainTags    : null,
            Imports:           r.Imports.Count       > 0 ? r.Imports       : null,
            TypeHierarchy:     r.TypeHierarchy.Count > 0 ? r.TypeHierarchy : null,
            DiagnosticSummary: string.IsNullOrEmpty(r.DiagnosticSummary) ? null : r.DiagnosticSummary,
            IsTestFile:            r.IsTestFile,
            TestFramework:         r.TestFramework,
            TestSubjectFileIds:    r.TestSubjectFileIds.Count > 0 ? r.TestSubjectFileIds : null,
            HasValidation:         r.HasValidation,
            ArchitecturalRole:     r.ArchitecturalRole,
            IsEntrypoint:          r.IsEntrypoint);

    private static IEnumerable<CodeIndexFileResponse> MergeAndScore(
        IReadOnlyList<(CodeIndexRecord Record, float Score)> semantic,
        IReadOnlyList<CodeIndexRecord> lexical,
        string? query)
    {
        var semDict = semantic.ToDictionary(x => x.Record.Id, x => x.Score);
        var lexDict = lexical.Select((r, i) => (r.Id, Rank: i + 1))
            .ToDictionary(x => x.Id, x => x.Rank);

        // All records are available from the two lanes — no extra DB round-trip needed.
        var recordMap = new Dictionary<string, CodeIndexRecord>();
        foreach (var (rec, _) in semantic) recordMap[rec.Id] = rec;
        foreach (var rec in lexical) recordMap[rec.Id] = rec;

        var scored = recordMap.Keys
            .Select(id =>
            {
                var r = recordMap[id];
                var structural = !string.IsNullOrEmpty(query) ? SearchScorer.Structural(query, r) : 0f;
                var semantic_s = semDict.TryGetValue(id, out var cos) ? SearchScorer.Semantic(cos) : 0f;
                var lexical_s  = lexDict.TryGetValue(id, out var rank) ? SearchScorer.Lexical(rank) : 0f;
                return (Record: r, Score: SearchScorer.Combine(structural, semantic_s, lexical_s));
            })
            .OrderByDescending(x => x.Score)
            .Take(50)
            .ToList();

        var total = scored.Count;
        return scored.Select((x, i) =>
        {
            var displayScore = total > 1 ? (float)(total - i) / total : 1f;
            return ToFileResponse(x.Record, displayScore);
        });
    }

    // ── Intelligence / symbol-graph endpoints ─────────────────────────────────

    private static void MapIntelligenceEndpoints(this WebApplication app)
    {
        // GET /api/workspaces/{id}/intelligence/symbols
        app.MapGet("/api/workspaces/{id}/intelligence/symbols", async (
            string id,
            string? q,
            string? kind,
            bool? publicOnly,
            int? minFanIn,
            string? subProjectId,
            IKeyValueStore kv,
            ICodeIndexRepository repo,
            CancellationToken ct) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });

            var kinds = string.IsNullOrWhiteSpace(kind)
                ? null
                : kind.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var symbols = await repo.SearchSymbolsAsync(
                q ?? "", id, subProjectId,
                publicOnly ?? false, kinds, minFanIn ?? 0, ct);

            var dtos = symbols.Select(ToSymbolReferenceDto).ToList();
            return Results.Ok(new SymbolSearchResponse(dtos.Count, dtos));
        });

        // GET /api/workspaces/{id}/intelligence/file/{fileId}
        app.MapGet("/api/workspaces/{id}/intelligence/file/{fileId}", async (
            string id,
            string fileId,
            IKeyValueStore kv,
            ICodeIndexRepository repo,
            CancellationToken ct) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });

            var records = await repo.GetByIdsAsync([fileId], ct);
            var record  = records.FirstOrDefault();
            if (record is null) return Results.NotFound(new { error = "File not indexed." });

            var symRefs = await repo.GetDefinedInFileAsync(fileId, ct);

            // Resolve DependsOn stubs
            var dependsOnIds = record.DependsOnFileIds.Distinct().ToList();
            var dependsOnRecords = dependsOnIds.Count > 0
                ? await repo.GetByIdsAsync(dependsOnIds, ct)
                : (IReadOnlyList<AgenticMemory.CodeIndex.CodeIndexRecord>)[];

            return Results.Ok(new IntelligenceFileProfileDto(
                File:           ToFileResponse(record),
                DefinedSymbols: symRefs.Select(ToSymbolReferenceDto).ToList(),
                DependsOn:      dependsOnRecords.Select(r => new DependencyNodeDto(
                    r.Id, r.RelativePath, r.FanIn, r.FanOut, r.Symbols.Count, r.Language)).ToList()));
        });

        // GET /api/workspaces/{id}/intelligence/hotspots?topN=20
        app.MapGet("/api/workspaces/{id}/intelligence/hotspots", async (
            string id,
            int? topN,
            IKeyValueStore kv,
            ICodeIndexRepository repo,
            CancellationToken ct) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });

            var all = await repo.GetByProjectAsync(id, ct);
            var hotspots = all
                .OrderByDescending(r => r.FanIn)
                .Take(topN ?? 20)
                .Select(r => new DependencyNodeDto(r.Id, r.RelativePath, r.FanIn, r.FanOut, r.Symbols.Count, r.Language))
                .ToList();

            return Results.Ok(hotspots);
        });

        // GET /api/workspaces/{id}/intelligence/entrypoints
        app.MapGet("/api/workspaces/{id}/intelligence/entrypoints", async (
            string id,
            IKeyValueStore kv,
            ICodeIndexRepository repo,
            CancellationToken ct) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });

            var all = await repo.GetByProjectAsync(id, ct);
            var entrypoints = all
                .Where(r => r.FanIn == 0)
                .OrderByDescending(r => r.FanOut)
                .Select(r => new DependencyNodeDto(r.Id, r.RelativePath, r.FanIn, r.FanOut, r.Symbols.Count, r.Language))
                .ToList();

            return Results.Ok(entrypoints);
        });

        // GET /api/workspaces/{id}/intelligence/graph
        app.MapGet("/api/workspaces/{id}/intelligence/graph", async (
            string id,
            string? subProjectId,
            IKeyValueStore kv,
            ICodeIndexRepository repo,
            CancellationToken ct) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });

            var allFiles = string.IsNullOrEmpty(subProjectId)
                ? await repo.GetByProjectAsync(id, ct)
                : await repo.GetBySubProjectAsync(subProjectId, ct);

            var allSymRefs = await repo.SearchSymbolsAsync("", id, subProjectId, ct: ct);

            // Build deduplicated edges: one per (from → to) file pair, viaSymbols aggregated
            var edgeMap = new Dictionary<(string From, string To), List<string>>();
            foreach (var sym in allSymRefs)
            {
                foreach (var site in sym.UsedBy)
                {
                    var key = (From: site.FileId, To: sym.DefinedInFileId);
                    if (key.From == key.To) continue;
                    if (!edgeMap.TryGetValue(key, out var names))
                        edgeMap[key] = names = [];
                    if (!names.Contains(sym.SymbolName))
                        names.Add(sym.SymbolName);
                }
            }

            var nodes = allFiles
                .Select(r => new DependencyNodeDto(r.Id, r.RelativePath, r.FanIn, r.FanOut, r.Symbols.Count, r.Language))
                .ToList();

            var edges = edgeMap
                .Select(kvp => new DependencyEdgeDto(kvp.Key.From, kvp.Key.To, kvp.Value))
                .ToList();

            return Results.Ok(new DependencyGraphDto(nodes, edges));
        });

        // GET /api/workspaces/{id}/intelligence/file/{fileId}/content?startLine=&endLine=
        // On-demand line slice (no stored byte offsets) — always current, with a staleness flag.
        app.MapGet("/api/workspaces/{id}/intelligence/file/{fileId}/content", async (
            string id, string fileId, int? startLine, int? endLine,
            IKeyValueStore kv, ICodeIndexRepository repo, CancellationToken ct) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });

            var record = (await repo.GetByIdsAsync([fileId], ct)).FirstOrDefault();
            if (record is null) return Results.NotFound(new { error = "File not indexed." });
            var content = await ReadLineSliceAsync(record, startLine, endLine, ct);
            return content is null ? Results.NotFound(new { error = "File no longer on disk." }) : Results.Ok(content);
        });

        // GET /api/workspaces/{id}/intelligence/file/{fileId}/symbol/{name}
        // "Read symbol X without grep": resolves the symbol's stored Line/EndLine and returns that slice.
        app.MapGet("/api/workspaces/{id}/intelligence/file/{fileId}/symbol/{name}", async (
            string id, string fileId, string name,
            IKeyValueStore kv, ICodeIndexRepository repo, CancellationToken ct) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });

            var record = (await repo.GetByIdsAsync([fileId], ct)).FirstOrDefault();
            if (record is null) return Results.NotFound(new { error = "File not indexed." });
            var sym = record.Symbols.FirstOrDefault(s => s.Name == name);
            if (sym is null) return Results.NotFound(new { error = "Symbol not found in file." });

            var endLine = sym.EndLine > 0 ? sym.EndLine : sym.Line;
            var content = await ReadLineSliceAsync(record, sym.Line, endLine, ct);
            return content is null ? Results.NotFound(new { error = "File no longer on disk." }) : Results.Ok(content);
        });

        // GET /api/workspaces/{id}/intelligence/domain-facts?kind=&subProjectId=
        app.MapGet("/api/workspaces/{id}/intelligence/domain-facts", async (
            string id, string? kind, string? subProjectId,
            IKeyValueStore kv, ICodeIndexRepository repo, CancellationToken ct) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });

            var facts   = await repo.GetDomainFactsByProjectAsync(id, kind, subProjectId, ct);
            var fileIds = facts.Select(f => f.FileId).Distinct().ToList();
            var recs    = fileIds.Count > 0 ? await repo.GetByIdsAsync(fileIds, ct) : [];
            var pathById = recs.ToDictionary(r => r.Id, r => r.RelativePath, StringComparer.Ordinal);

            var dtos = facts.Select(f => new DomainFactDto(
                f.Kind, f.Line, f.Method, f.Route, f.Name, f.TypeRef, f.OwnerType, f.Items,
                f.FileId, pathById.GetValueOrDefault(f.FileId, ""))).ToList();
            return Results.Ok(dtos);
        });

        // GET /api/workspaces/{id}/intelligence/manifests
        app.MapGet("/api/workspaces/{id}/intelligence/manifests", async (
            string id, IKeyValueStore kv, ICodeIndexRepository repo, CancellationToken ct) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });

            var manifests = await repo.GetProjectManifestsAsync(id, ct);
            var dtos = manifests.Select(m => new ProjectManifestDto(
                m.ManifestType, m.ManifestPath, m.TargetFrameworks, m.OutputKind, m.LangVersion, m.Nullable,
                m.ImplicitUsings,
                m.Packages.Select(p => new PackageDependencyDto(p.Name, p.Version, p.IsDev)).ToList(),
                m.ProjectReferences, m.Scripts)).ToList();
            return Results.Ok(dtos);
        });

        // GET /api/workspaces/{id}/intelligence/semantic?q=&topN=&subProjectId=
        app.MapGet("/api/workspaces/{id}/intelligence/semantic", async (
            string id, string? q, int? topN, string? subProjectId,
            IKeyValueStore kv, ICodeIndexRepository repo,
            AgenticMemory.Brain.Interfaces.IEmbeddingService embedding, CancellationToken ct) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });
            if (string.IsNullOrWhiteSpace(q)) return Results.Ok(new List<SemanticSymbolHitDto>());
            if (!embedding.IsAvailable) return Results.Problem("Embedding model unavailable.");

            var vec  = await embedding.GetEmbeddingAsync(q, ct);
            var hits = await repo.SearchSymbolEmbeddingsAsync(vec, id, subProjectId, topN ?? 20, ct);
            var dtos = hits.Select(h => new SemanticSymbolHitDto(
                h.Record.Id, h.Record.SymbolName, h.Record.ContainingType, h.Record.Kind,
                h.Record.FileId, h.Record.RelativePath, h.Record.Line, h.Record.EndLine, h.Score)).ToList();
            return Results.Ok(dtos);
        });

        // GET /api/workspaces/{id}/intelligence/overview
        app.MapGet("/api/workspaces/{id}/intelligence/overview", async (
            string id, string? subProjectId, IKeyValueStore kv, ICodeIndexRepository repo, CancellationToken ct) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            if (ws is null) return Results.NotFound(new { error = "Workspace not found." });

            var files = string.IsNullOrEmpty(subProjectId)
                ? await repo.GetByProjectAsync(id, ct)
                : await repo.GetBySubProjectAsync(subProjectId, ct);
            var syms      = await repo.SearchSymbolsAsync("", id, subProjectId, ct: ct);
            var facts     = await repo.GetDomainFactsByProjectAsync(id, null, subProjectId, ct);
            var manifests = await repo.GetProjectManifestsAsync(id, ct);

            int Count(string k) => facts.Count(f => f.Kind == k);
            return Results.Ok(new IntelligenceOverviewDto(
                Files:           files.Count,
                Symbols:         syms.Count,
                Endpoints:       Count("http-endpoint") + Count("fetch-endpoint"),
                DiEdges:         Count("di-injection"),
                EfEntities:      Count("ef-entity"),
                MediatrMessages: Count("mediatr-message"),
                TypeRelations:   Count("type-relation"),
                ConfigKeys:      Count("config-key"),
                SecuritySinks:   Count("security-sink"),

                TestFiles:       files.Count(f => f.IsTestFile),
                Packages:        manifests.Sum(m => m.Packages.Count),
                TypeScriptFilesWithoutTypes: files.Count(f => f.TypeScriptTypesResolved == false)));
        });
    }

    // On-demand line-range read of an indexed file (no stored byte offsets — see report §"GetContent").
    private static async Task<FileContentResponse?> ReadLineSliceAsync(
        CodeIndexRecord record, int? startLine, int? endLine, CancellationToken ct)
    {
        if (!File.Exists(record.FilePath)) return null;

        string[] lines;
        try { lines = await File.ReadAllLinesAsync(record.FilePath, ct); }
        catch { return null; }

        var total = lines.Length;
        var s = Math.Max(1, startLine ?? 1);
        var e = Math.Min(total, endLine ?? total);
        if (e < s) e = s;

        var slice = total == 0 ? "" : string.Join('\n', lines.Skip(s - 1).Take(e - s + 1));
        var stale = record.IsStale ||
                    File.GetLastWriteTimeUtc(record.FilePath) > record.FileModifiedAt.AddSeconds(1);
        return new FileContentResponse(record.Id, record.RelativePath, s, e, total, stale, slice);
    }

    private static SymbolReferenceDto ToSymbolReferenceDto(AgenticMemory.CodeIndex.SymbolReferenceRecord r) =>
        new(r.Id, r.SymbolName, r.SymbolKind, r.Accessibility,
            r.DefinedInFileId, r.DefinedInRelativePath, r.DefinedAtLine,
            r.UsedBy.Count,
            r.UsedBy.Select(u => new SymbolUsageSiteDto(u.FileId, u.RelativePath, u.Line, u.Context, u.Role, u.EnclosingName)).ToList(),
            r.TestedByFileIds.Count > 0 ? r.TestedByFileIds : null);

    // ── Legacy /api/projects/* aliases (backward compatible) ─────────────────

    private static void MapProjectEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects", (IKeyValueStore kv, AppSettings settings, CodeIndexService codeIndex) =>
            Results.Ok(LoadWorkspaces(kv).Select(ws => ToWorkspaceDto(ws, settings, codeIndex))));

        app.MapGet("/api/projects/{id}", (
            string id, IKeyValueStore kv, AppSettings settings, CodeIndexService codeIndex) =>
        {
            var ws = LoadWorkspaces(kv).Find(w => w.Id == id);
            return ws is null ? Results.NotFound() : Results.Ok(ToWorkspaceDto(ws, settings, codeIndex));
        });

        app.MapPost("/api/projects", async (
            ProjectCreateRequest request,
            IKeyValueStore kv,
            CodeIndexService codeIndex,
            WorkspaceDiscoveryService discovery,
            AppSettings settings,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Name is required." });
            if (string.IsNullOrWhiteSpace(request.RootPath))
                return Results.BadRequest(new { error = "RootPath is required." });
            if (!Directory.Exists(request.RootPath))
                return Results.BadRequest(new { error = "RootPath does not exist." });

            var id = Guid.NewGuid().ToString();
            var subProjects = await discovery.DiscoverAsync(request.RootPath.Trim(), ct);
            var subProjectsWithOwner = subProjects
                .Select(sp => sp with { WorkspaceId = id })
                .ToList();

            var workspace = new WorkspaceRecord(
                Id:          id,
                Name:        request.Name.Trim(),
                RootPath:    request.RootPath.Trim(),
                CreatedAt:   DateTime.UtcNow.ToString("O"),
                SubProjects: subProjectsWithOwner);

            var workspaces = LoadWorkspaces(kv);
            workspaces.Add(workspace);
            SaveWorkspaces(kv, workspaces);

            _ = Task.Run(async () =>
            {
                foreach (var sub in subProjectsWithOwner)
                    await codeIndex.RegisterSubProjectAsync(sub, CancellationToken.None);
            });

            return Results.Created($"/api/projects/{id}",
                ToWorkspaceDto(workspace, settings, codeIndex));
        });

        app.MapDelete("/api/projects/{id}", (string id, IKeyValueStore kv) =>
        {
            var workspaces = LoadWorkspaces(kv);
            var idx = workspaces.FindIndex(w => w.Id == id);
            if (idx < 0) return Results.NotFound();
            workspaces.RemoveAt(idx);
            SaveWorkspaces(kv, workspaces);
            return Results.NoContent();
        });
    }
}
