using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Http.Models;

namespace AgenticMemory.Http.Handlers;

/// <summary>
/// CRUD operations for memory nodes
/// </summary>
public class MemoryHandler : IHandler
{
    private readonly IMemoryRepository? _repository;
    private readonly IEmbeddingService? _embeddingService;

    public MemoryHandler(IMemoryRepository? repository = null, IEmbeddingService? embeddingService = null)
    {
        _repository = repository;
        _embeddingService = embeddingService;
    }

    public Task<Response> HandleAsync(Request request, CancellationToken cancellationToken)
    {
        return request.Method switch
        {
            Models.HttpMethod.GET => HandleGetAsync(request, cancellationToken),
            Models.HttpMethod.POST => HandlePostAsync(request, cancellationToken),
            Models.HttpMethod.PUT => HandlePutAsync(request, cancellationToken),
            Models.HttpMethod.DELETE => HandleDeleteAsync(request, cancellationToken),
            _ => Task.FromResult(Response.MethodNotAllowed("Use GET, POST, PUT, or DELETE"))
        };
    }


    private async Task<Response> HandleGetAsync(Request request, CancellationToken cancellationToken)
    {
        var idStr = request.GetParameter("id");
        if (string.IsNullOrEmpty(idStr) || !Guid.TryParse(idStr, out var id))
        {
            return Response.BadRequest("Invalid or missing memory ID");
        }

        // Use real repository if available
        if (_repository is not null)
        {
            var entity = await _repository.GetAsync(id, cancellationToken);
            if (entity is null)
            {
                return Response.NotFound($"Memory node with ID {id} not found");
            }

            // Reinforce the memory on access
            await _repository.ReinforceAsync(id, cancellationToken);

            return Response.Ok(entity.ToHandlerModel());
        }

        // Fallback to mock data
        var node = new MemoryNode
        {
            Id = id,
            Title = "Sample Memory Node",
            Summary = "This is a sample memory node retrieved from storage.",
            Content = "Full content of the memory node would go here.",
            Tags = ["sample", "demo"],
            CreatedAt = DateTime.UtcNow.AddDays(-7),
            LastAccessedAt = DateTime.UtcNow,
            ReinforcementScore = 1.5,
            LinkedNodeIds = []
        };

        return Response.Ok(node);
    }

    private async Task<Response> HandlePostAsync(Request request, CancellationToken cancellationToken)
    {
        var createRequest = request.GetBodyAs<CreateMemoryRequest>();

        if (createRequest is null)
        {
            return Response.BadRequest("Invalid request body");
        }

        if (string.IsNullOrWhiteSpace(createRequest.Title))
        {
            return Response.BadRequest("Title is required");
        }

        if (string.IsNullOrWhiteSpace(createRequest.Summary))
        {
            return Response.BadRequest("Summary is required");
        }

        // Use real repository if available
        if (_repository is not null)
        {
            var entity = MemoryNodeEntity.FromCreateRequest(createRequest);
            
            // Generate embedding if service is available
            if (_embeddingService?.IsAvailable == true)
            {
                try
                {
                    var textForEmbedding = $"{entity.Title} {entity.Summary} {entity.Content}";
                    var embedding = await _embeddingService.GetEmbeddingAsync(textForEmbedding, cancellationToken);
                    entity.SetEmbedding(embedding);
                }
                catch
                {
                    // Continue without embedding if generation fails
                }
            }
            
            await _repository.SaveAsync(entity, cancellationToken);
            return Response.Created($"/api/memory/{entity.Id}", entity.ToHandlerModel());
        }

        // Fallback to mock behavior
        var newNode = new MemoryNode
        {
            Id = Guid.NewGuid(),
            Title = createRequest.Title,
            Summary = createRequest.Summary,
            Content = createRequest.Content ?? string.Empty,
            Tags = createRequest.Tags ?? [],
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            ReinforcementScore = 1.0,
            LinkedNodeIds = []
        };

        return Response.Created($"/api/memory/{newNode.Id}", newNode);
    }

    private async Task<Response> HandlePutAsync(Request request, CancellationToken cancellationToken)
    {
        var idStr = request.GetParameter("id");
        if (string.IsNullOrEmpty(idStr) || !Guid.TryParse(idStr, out var id))
        {
            return Response.BadRequest("Invalid or missing memory ID");
        }

        var updateRequest = request.GetBodyAs<UpdateMemoryRequest>();
        if (updateRequest is null)
        {
            return Response.BadRequest("Invalid request body");
        }

        // Use real repository if available
        if (_repository is not null)
        {
            var existing = await _repository.GetAsync(id, cancellationToken);
            if (existing is null)
            {
                return Response.NotFound($"Memory node with ID {id} not found");
            }

            // Update fields
            var contentChanged = false;
            if (updateRequest.Title is not null)
            {
                existing.Title = updateRequest.Title;
                contentChanged = true;
            }
            if (updateRequest.Summary is not null)
            {
                existing.Summary = updateRequest.Summary;
                contentChanged = true;
            }
            if (updateRequest.Content is not null)
            {
                existing.Content = updateRequest.Content;
                contentChanged = true;
            }
            if (updateRequest.Tags is not null)
                existing.Tags = updateRequest.Tags;
            if (updateRequest.ExpiresAt is not null)
                existing.ExpiresAt = updateRequest.ExpiresAt;
            if (updateRequest.Importance is not null)
                existing.Importance = Math.Clamp(updateRequest.Importance.Value, 0.0, 1.0);
            if (updateRequest.IsPinned is not null)
                existing.IsPinned = updateRequest.IsPinned.Value;

            // Regenerate embedding if content changed and service is available
            if (contentChanged && _embeddingService?.IsAvailable == true)
            {
                try
                {
                    var textForEmbedding = $"{existing.Title} {existing.Summary} {existing.Content}";
                    var embedding = await _embeddingService.GetEmbeddingAsync(textForEmbedding, cancellationToken);
                    existing.SetEmbedding(embedding);
                }
                catch
                {
                    // Continue without embedding update if generation fails
                }
            }

            existing.Reinforce();
            await _repository.SaveAsync(existing, cancellationToken);

            return Response.Ok(existing.ToHandlerModel());
        }

        // Fallback to mock behavior
        var updatedNode = new MemoryNode
        {
            Id = id,
            Title = updateRequest.Title ?? "Updated Title",
            Summary = updateRequest.Summary ?? "Updated summary",
            Content = updateRequest.Content ?? string.Empty,
            Tags = updateRequest.Tags ?? [],
            CreatedAt = DateTime.UtcNow.AddDays(-7),
            LastAccessedAt = DateTime.UtcNow,
            ReinforcementScore = 1.5,
            LinkedNodeIds = []
        };

        return Response.Ok(updatedNode);
    }

    private async Task<Response> HandleDeleteAsync(Request request, CancellationToken cancellationToken)
    {
        var idStr = request.GetParameter("id");
        if (string.IsNullOrEmpty(idStr) || !Guid.TryParse(idStr, out var id))
        {
            return Response.BadRequest("Invalid or missing memory ID");
        }

        // Use real repository if available
        if (_repository is not null)
        {
            var deleted = await _repository.DeleteAsync(id, cancellationToken);
            if (!deleted)
            {
                return Response.NotFound($"Memory node with ID {id} not found");
            }
        }

        return Response.NoContent();
    }
}

public record MemoryNode
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public List<string> Tags { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime LastAccessedAt { get; init; }
    public double ReinforcementScore { get; init; }
    public List<Guid> LinkedNodeIds { get; init; } = [];
    public float[]? Embedding { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public double Importance { get; init; }
    public bool IsPinned { get; init; }
    public bool IsArchived { get; init; }
}

public record CreateMemoryRequest
{
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string? Content { get; init; }
    public List<string>? Tags { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public double? Importance { get; init; }
    public bool? IsPinned { get; init; }
}

public record UpdateMemoryRequest
{
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public string? Content { get; init; }
    public List<string>? Tags { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public double? Importance { get; init; }
    public bool? IsPinned { get; init; }
}
