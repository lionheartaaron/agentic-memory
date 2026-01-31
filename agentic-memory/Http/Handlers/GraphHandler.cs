using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Http.Models;

namespace AgenticMemory.Http.Handlers;

/// <summary>
/// Handle memory graph operations - links between memory nodes
/// </summary>
public class GraphHandler : IHandler
{
    private readonly IMemoryRepository? _repository;
    private const int MaxGraphDepth = 5;

    public GraphHandler(IMemoryRepository? repository = null)
    {
        _repository = repository;
    }

    public async Task<Response> HandleAsync(Request request, CancellationToken cancellationToken)
    {
        if (_repository is null)
        {
            return Response.InternalServerError("Repository not available");
        }

        var path = request.Path;
        var method = request.Method;

        // GET /api/memory/{id}/links - Get linked memories
        if (path.EndsWith("/links") && method == Models.HttpMethod.GET)
        {
            return await HandleGetLinksAsync(request, cancellationToken);
        }

        // POST /api/memory/{id}/link/{targetId} - Create link
        if (path.Contains("/link/") && method == Models.HttpMethod.POST)
        {
            return await HandleCreateLinkAsync(request, cancellationToken);
        }

        // DELETE /api/memory/{id}/link/{targetId} - Remove link
        if (path.Contains("/link/") && method == Models.HttpMethod.DELETE)
        {
            return await HandleDeleteLinkAsync(request, cancellationToken);
        }

        // GET /api/memory/{id}/graph - Get memory subgraph
        if (path.EndsWith("/graph") && method == Models.HttpMethod.GET)
        {
            return await HandleGetGraphAsync(request, cancellationToken);
        }

        return Response.NotFound($"Unknown graph endpoint: {path}");
    }

    private async Task<Response> HandleGetLinksAsync(Request request, CancellationToken cancellationToken)
    {
        var idStr = request.GetParameter("id");
        if (!Guid.TryParse(idStr, out var id))
        {
            return Response.BadRequest("Invalid memory ID");
        }

        var memory = await _repository!.GetAsync(id, cancellationToken);
        if (memory is null)
        {
            return Response.NotFound($"Memory not found: {id}");
        }

        var links = new List<LinkedMemoryInfo>();
        foreach (var linkedId in memory.LinkedNodeIds)
        {
            var linked = await _repository.GetAsync(linkedId, cancellationToken);
            if (linked is not null)
            {
                links.Add(new LinkedMemoryInfo
                {
                    Id = linked.Id,
                    Title = linked.Title,
                    Summary = linked.Summary,
                    Strength = linked.GetCurrentStrength()
                });
            }
        }

        return Response.Ok(new GetLinksResponse
        {
            SourceId = id,
            Links = links
        });
    }

    private async Task<Response> HandleCreateLinkAsync(Request request, CancellationToken cancellationToken)
    {
        var sourceIdStr = request.GetParameter("id");
        var targetIdStr = request.GetParameter("targetId");

        if (!Guid.TryParse(sourceIdStr, out var sourceId))
        {
            return Response.BadRequest("Invalid source memory ID");
        }

        if (!Guid.TryParse(targetIdStr, out var targetId))
        {
            return Response.BadRequest("Invalid target memory ID");
        }

        if (sourceId == targetId)
        {
            return Response.BadRequest("Cannot link a memory to itself");
        }

        var source = await _repository!.GetAsync(sourceId, cancellationToken);
        if (source is null)
        {
            return Response.NotFound($"Source memory not found: {sourceId}");
        }

        var target = await _repository.GetAsync(targetId, cancellationToken);
        if (target is null)
        {
            return Response.NotFound($"Target memory not found: {targetId}");
        }

        // Add link if not already present
        if (!source.LinkedNodeIds.Contains(targetId))
        {
            source.LinkedNodeIds.Add(targetId);
            await _repository.SaveAsync(source, cancellationToken);
        }

        // Optionally add reverse link for bidirectional linking
        var bidirectional = request.GetQueryParameter("bidirectional")?.ToLower() == "true";
        if (bidirectional && !target.LinkedNodeIds.Contains(sourceId))
        {
            target.LinkedNodeIds.Add(sourceId);
            await _repository.SaveAsync(target, cancellationToken);
        }

        return Response.Ok(new CreateLinkResponse
        {
            SourceId = sourceId,
            TargetId = targetId,
            Bidirectional = bidirectional,
            Success = true
        });
    }

    private async Task<Response> HandleDeleteLinkAsync(Request request, CancellationToken cancellationToken)
    {
        var sourceIdStr = request.GetParameter("id");
        var targetIdStr = request.GetParameter("targetId");

        if (!Guid.TryParse(sourceIdStr, out var sourceId))
        {
            return Response.BadRequest("Invalid source memory ID");
        }

        if (!Guid.TryParse(targetIdStr, out var targetId))
        {
            return Response.BadRequest("Invalid target memory ID");
        }

        var source = await _repository!.GetAsync(sourceId, cancellationToken);
        if (source is null)
        {
            return Response.NotFound($"Source memory not found: {sourceId}");
        }

        var removed = source.LinkedNodeIds.Remove(targetId);
        if (removed)
        {
            await _repository.SaveAsync(source, cancellationToken);
        }

        // Optionally remove reverse link
        var bidirectional = request.GetQueryParameter("bidirectional")?.ToLower() == "true";
        if (bidirectional)
        {
            var target = await _repository.GetAsync(targetId, cancellationToken);
            if (target is not null && target.LinkedNodeIds.Remove(sourceId))
            {
                await _repository.SaveAsync(target, cancellationToken);
            }
        }

        return Response.Ok(new DeleteLinkResponse
        {
            SourceId = sourceId,
            TargetId = targetId,
            Removed = removed,
            Bidirectional = bidirectional
        });
    }

    private async Task<Response> HandleGetGraphAsync(Request request, CancellationToken cancellationToken)
    {
        var idStr = request.GetParameter("id");
        if (!Guid.TryParse(idStr, out var id))
        {
            return Response.BadRequest("Invalid memory ID");
        }

        var depthStr = request.GetQueryParameter("depth") ?? "2";
        if (!int.TryParse(depthStr, out var depth) || depth < 1)
        {
            depth = 2;
        }
        depth = Math.Min(depth, MaxGraphDepth);

        var memory = await _repository!.GetAsync(id, cancellationToken);
        if (memory is null)
        {
            return Response.NotFound($"Memory not found: {id}");
        }

        // Build graph using BFS
        var visited = new HashSet<Guid>();
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        var queue = new Queue<(Guid Id, int Depth)>();

        queue.Enqueue((id, 0));
        visited.Add(id);

        while (queue.Count > 0)
        {
            var (currentId, currentDepth) = queue.Dequeue();
            var current = await _repository.GetAsync(currentId, cancellationToken);

            if (current is null) continue;

            nodes.Add(new GraphNode
            {
                Id = current.Id,
                Title = current.Title,
                Summary = current.Summary,
                Depth = currentDepth,
                Strength = current.GetCurrentStrength(),
                LinkCount = current.LinkedNodeIds.Count
            });

            if (currentDepth >= depth) continue;

            foreach (var linkedId in current.LinkedNodeIds)
            {
                edges.Add(new GraphEdge
                {
                    SourceId = currentId,
                    TargetId = linkedId
                });

                if (!visited.Contains(linkedId))
                {
                    visited.Add(linkedId);
                    queue.Enqueue((linkedId, currentDepth + 1));
                }
            }
        }

        return Response.Ok(new GetGraphResponse
        {
            RootId = id,
            Depth = depth,
            Nodes = nodes,
            Edges = edges
        });
    }
}

// Response models for graph operations

public record GetLinksResponse
{
    public Guid SourceId { get; init; }
    public List<LinkedMemoryInfo> Links { get; init; } = [];
}

public record LinkedMemoryInfo
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public double Strength { get; init; }
}

public record CreateLinkResponse
{
    public Guid SourceId { get; init; }
    public Guid TargetId { get; init; }
    public bool Bidirectional { get; init; }
    public bool Success { get; init; }
}

public record DeleteLinkResponse
{
    public Guid SourceId { get; init; }
    public Guid TargetId { get; init; }
    public bool Removed { get; init; }
    public bool Bidirectional { get; init; }
}

public record GetGraphResponse
{
    public Guid RootId { get; init; }
    public int Depth { get; init; }
    public List<GraphNode> Nodes { get; init; } = [];
    public List<GraphEdge> Edges { get; init; } = [];
}

public record GraphNode
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public int Depth { get; init; }
    public double Strength { get; init; }
    public int LinkCount { get; init; }
}

public record GraphEdge
{
    public Guid SourceId { get; init; }
    public Guid TargetId { get; init; }
}
