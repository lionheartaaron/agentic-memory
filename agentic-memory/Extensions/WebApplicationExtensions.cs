using AgenticMemory.Brain.Interfaces;
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

        return app;
    }

    private static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/health", () =>
            Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));
    }

    private static void MapMemoryEndpoints(this WebApplication app)
    {
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
}
