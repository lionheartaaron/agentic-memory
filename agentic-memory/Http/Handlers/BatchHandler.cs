using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Http.Models;

namespace AgenticMemory.Http.Handlers;

/// <summary>
/// Handle batch memory operations for efficiency
/// </summary>
public class BatchHandler : IHandler
{
    private readonly IMemoryRepository? _repository;
    private readonly ISearchService? _searchService;
    private readonly IEmbeddingService? _embeddingService;

    private const int MaxBatchSize = 100;

    public BatchHandler(IMemoryRepository? repository = null, ISearchService? searchService = null, IEmbeddingService? embeddingService = null)
    {
        _repository = repository;
        _searchService = searchService;
        _embeddingService = embeddingService;
    }

    public async Task<Response> HandleAsync(Request request, CancellationToken cancellationToken)
    {
        if (_repository is null)
        {
            return Response.InternalServerError("Repository not available");
        }

        var path = request.Path;

        return path switch
        {
            "/api/memory/batch" when request.Method == Models.HttpMethod.POST => await HandleBatchCreateAsync(request, cancellationToken),
            "/api/memory/batch" when request.Method == Models.HttpMethod.PUT => await HandleBatchUpdateAsync(request, cancellationToken),
            "/api/memory/batch" when request.Method == Models.HttpMethod.DELETE => await HandleBatchDeleteAsync(request, cancellationToken),
            "/api/memory/search/batch" when request.Method == Models.HttpMethod.POST => await HandleBatchSearchAsync(request, cancellationToken),
            _ => Response.NotFound($"Unknown batch endpoint: {path}")
        };
    }

    private async Task<Response> HandleBatchCreateAsync(Request request, CancellationToken cancellationToken)
    {
        var batchRequest = request.GetBodyAs<BatchCreateRequest>();
        if (batchRequest?.Memories is null || batchRequest.Memories.Count == 0)
        {
            return Response.BadRequest("At least one memory is required");
        }

        if (batchRequest.Memories.Count > MaxBatchSize)
        {
            return Response.BadRequest($"Batch size exceeds maximum of {MaxBatchSize}");
        }

        var results = new List<BatchCreateResult>();
        var successCount = 0;
        var failureCount = 0;

        foreach (var memoryRequest in batchRequest.Memories)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(memoryRequest.Title) || string.IsNullOrWhiteSpace(memoryRequest.Summary))
                {
                    results.Add(new BatchCreateResult
                    {
                        Success = false,
                        Error = "Title and summary are required"
                    });
                    failureCount++;
                    continue;
                }

                var entity = MemoryNodeEntity.FromCreateRequest(memoryRequest);
                
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
                
                await _repository!.SaveAsync(entity, cancellationToken);

                results.Add(new BatchCreateResult
                {
                    Success = true,
                    Id = entity.Id
                });
                successCount++;
            }
            catch (Exception ex)
            {
                results.Add(new BatchCreateResult
                {
                    Success = false,
                    Error = ex.Message
                });
                failureCount++;
            }
        }

        return Response.Ok(new BatchCreateResponse
        {
            TotalRequested = batchRequest.Memories.Count,
            SuccessCount = successCount,
            FailureCount = failureCount,
            Results = results
        });
    }

    private async Task<Response> HandleBatchUpdateAsync(Request request, CancellationToken cancellationToken)
    {
        var batchRequest = request.GetBodyAs<BatchUpdateRequest>();
        if (batchRequest?.Updates is null || batchRequest.Updates.Count == 0)
        {
            return Response.BadRequest("At least one update is required");
        }

        if (batchRequest.Updates.Count > MaxBatchSize)
        {
            return Response.BadRequest($"Batch size exceeds maximum of {MaxBatchSize}");
        }

        var results = new List<BatchUpdateResult>();
        var successCount = 0;
        var failureCount = 0;

        foreach (var update in batchRequest.Updates)
        {
            try
            {
                if (!Guid.TryParse(update.Id, out var id))
                {
                    results.Add(new BatchUpdateResult
                    {
                        Id = update.Id,
                        Success = false,
                        Error = "Invalid ID format"
                    });
                    failureCount++;
                    continue;
                }

                var existing = await _repository!.GetAsync(id, cancellationToken);
                if (existing is null)
                {
                    results.Add(new BatchUpdateResult
                    {
                        Id = update.Id,
                        Success = false,
                        Error = "Memory not found"
                    });
                    failureCount++;
                    continue;
                }

                // Apply updates
                if (update.Title is not null) existing.Title = update.Title;
                if (update.Summary is not null) existing.Summary = update.Summary;
                if (update.Content is not null) existing.Content = update.Content;
                if (update.Tags is not null) existing.Tags = update.Tags.ToList();

                await _repository.SaveAsync(existing, cancellationToken);

                results.Add(new BatchUpdateResult
                {
                    Id = update.Id,
                    Success = true
                });
                successCount++;
            }
            catch (Exception ex)
            {
                results.Add(new BatchUpdateResult
                {
                    Id = update.Id,
                    Success = false,
                    Error = ex.Message
                });
                failureCount++;
            }
        }

        return Response.Ok(new BatchUpdateResponse
        {
            TotalRequested = batchRequest.Updates.Count,
            SuccessCount = successCount,
            FailureCount = failureCount,
            Results = results
        });
    }

    private async Task<Response> HandleBatchDeleteAsync(Request request, CancellationToken cancellationToken)
    {
        var batchRequest = request.GetBodyAs<BatchDeleteRequest>();
        if (batchRequest?.Ids is null || batchRequest.Ids.Count == 0)
        {
            return Response.BadRequest("At least one ID is required");
        }

        if (batchRequest.Ids.Count > MaxBatchSize)
        {
            return Response.BadRequest($"Batch size exceeds maximum of {MaxBatchSize}");
        }

        var results = new List<BatchDeleteResult>();
        var successCount = 0;
        var failureCount = 0;

        foreach (var idStr in batchRequest.Ids)
        {
            try
            {
                if (!Guid.TryParse(idStr, out var id))
                {
                    results.Add(new BatchDeleteResult
                    {
                        Id = idStr,
                        Success = false,
                        Error = "Invalid ID format"
                    });
                    failureCount++;
                    continue;
                }

                var deleted = await _repository!.DeleteAsync(id, cancellationToken);

                results.Add(new BatchDeleteResult
                {
                    Id = idStr,
                    Success = deleted,
                    Error = deleted ? null : "Memory not found"
                });

                if (deleted) successCount++;
                else failureCount++;
            }
            catch (Exception ex)
            {
                results.Add(new BatchDeleteResult
                {
                    Id = idStr,
                    Success = false,
                    Error = ex.Message
                });
                failureCount++;
            }
        }

        return Response.Ok(new BatchDeleteResponse
        {
            TotalRequested = batchRequest.Ids.Count,
            SuccessCount = successCount,
            FailureCount = failureCount,
            Results = results
        });
    }

    private async Task<Response> HandleBatchSearchAsync(Request request, CancellationToken cancellationToken)
    {
        if (_searchService is null)
        {
            return Response.InternalServerError("Search service not available");
        }

        var batchRequest = request.GetBodyAs<BatchSearchRequest>();
        if (batchRequest?.Queries is null || batchRequest.Queries.Count == 0)
        {
            return Response.BadRequest("At least one query is required");
        }

        if (batchRequest.Queries.Count > MaxBatchSize)
        {
            return Response.BadRequest($"Batch size exceeds maximum of {MaxBatchSize}");
        }

        var results = new List<BatchSearchResult>();

        foreach (var query in batchRequest.Queries)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query.Query))
                {
                    results.Add(new BatchSearchResult
                    {
                        Query = query.Query ?? "",
                        Success = false,
                        Error = "Query is required"
                    });
                    continue;
                }

                var topN = query.TopN > 0 ? Math.Min(query.TopN, 100) : 5;
                var scored = await _searchService.SearchAsync(query.Query, topN, query.Tags, cancellationToken);

                results.Add(new BatchSearchResult
                {
                    Query = query.Query,
                    Success = true,
                    Results = scored.Select(s => new SearchResult
                    {
                        Id = s.Memory.Id,
                        Title = s.Memory.Title,
                        Summary = s.Memory.Summary,
                        Score = s.Score,
                        Tags = s.Memory.Tags
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                results.Add(new BatchSearchResult
                {
                    Query = query.Query ?? "",
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        return Response.Ok(new BatchSearchResponse
        {
            TotalQueries = batchRequest.Queries.Count,
            Results = results
        });
    }
}

// Request/Response models for batch operations

public record BatchCreateRequest
{
    public List<CreateMemoryRequest> Memories { get; init; } = [];
}

public record BatchCreateResponse
{
    public int TotalRequested { get; init; }
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public List<BatchCreateResult> Results { get; init; } = [];
}

public record BatchCreateResult
{
    public bool Success { get; init; }
    public Guid? Id { get; init; }
    public string? Error { get; init; }
}

public record BatchUpdateRequest
{
    public List<BatchUpdateItem> Updates { get; init; } = [];
}

public record BatchUpdateItem
{
    public string Id { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public string? Content { get; init; }
    public List<string>? Tags { get; init; }
}

public record BatchUpdateResponse
{
    public int TotalRequested { get; init; }
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public List<BatchUpdateResult> Results { get; init; } = [];
}

public record BatchUpdateResult
{
    public string Id { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? Error { get; init; }
}

public record BatchDeleteRequest
{
    public List<string> Ids { get; init; } = [];
}

public record BatchDeleteResponse
{
    public int TotalRequested { get; init; }
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public List<BatchDeleteResult> Results { get; init; } = [];
}

public record BatchDeleteResult
{
    public string Id { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? Error { get; init; }
}

public record BatchSearchRequest
{
    public List<SearchQuery> Queries { get; init; } = [];
}

public record SearchQuery
{
    public string? Query { get; init; }
    public int TopN { get; init; } = 5;
    public List<string>? Tags { get; init; }
}

public record BatchSearchResponse
{
    public int TotalQueries { get; init; }
    public List<BatchSearchResult> Results { get; init; } = [];
}

public record BatchSearchResult
{
    public string Query { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? Error { get; init; }
    public List<SearchResult>? Results { get; init; }
}
