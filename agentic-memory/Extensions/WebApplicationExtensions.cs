using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Configuration;
using AgenticMemory.Helpers;
using AgenticMemory.Models;

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
        app.MapGet("/api/file/context", (string path) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path query parameter is required." });

            var context = CodeContextExtractor.ExtractContext(path);

            if (string.IsNullOrEmpty(context))
                return Results.NotFound(new { error = "File not found, too large, binary, or empty." });

            return Results.Ok(new { context });
        });

        app.MapPost("/api/file/summary", (FileSummaryRequest request, IGenerativeModelService svc, AppSettings settings) =>
        {
            if (string.IsNullOrWhiteSpace(request.FilePath))
                return Results.BadRequest(new { error = "FilePath is required." });

            if (!svc.IsAvailable)
                return Results.Problem(
                    "Generative model is not available. Set Generation.Enabled and Generation.AutoDownload to true in appsettings.json.",
                    statusCode: 503);

            var context = CodeContextExtractor.ExtractContext(request.FilePath);

            if (string.IsNullOrEmpty(context))
                return Results.NotFound(new { error = "File not found, too large, binary, or empty." });

            const string systemPrompt =
                "You are a code indexing assistant. Given a structural summary of a source file, " +
                "write a keyword-dense description in 1–2 prose sentences, strictly under 60 words, " +
                "suitable for embedding-based semantic search. " +
                "Lead with the file type and role (e.g. 'TypeScript API client', 'C# service', 'React page component'). " +
                "Then name the main operations or domain concepts it covers. " +
                "Never use filler phrases like 'this file contains', 'this module provides', or 'the code includes'. " +
                "Output exactly one or two flowing sentences. No bullet points, no hyphens as list markers, " +
                "no labeled sections like 'File Type:' or 'Main Operations:', no markdown, no line breaks.";

            var userPrompt = settings.Generation.TruncateIfNeeded(
                $"Describe this file:\n\n{context}");

            var summary = svc.Generate(systemPrompt, userPrompt);

            return Results.Ok(new { summary });
        });
    }
}
