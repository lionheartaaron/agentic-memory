using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Maintenance;
using AgenticMemory.Http.Models;

namespace AgenticMemory.Http.Handlers;

/// <summary>
/// Administrative operations
/// </summary>
public class AdminHandler : IHandler
{
    private readonly DateTime _serverStartTime = DateTime.UtcNow;
    private readonly IMemoryRepository? _repository;
    private readonly IMaintenanceService? _maintenanceService;

    public AdminHandler(IMemoryRepository? repository = null, IMaintenanceService? maintenanceService = null)
    {
        _repository = repository;
        _maintenanceService = maintenanceService;
    }

    public Task<Response> HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var path = request.Path;

        if (path == "/api/admin/stats" && request.Method == Models.HttpMethod.GET)
        {
            return GetStatsAsync(cancellationToken);
        }

        if (path == "/api/admin/consolidate" && request.Method == Models.HttpMethod.POST)
        {
            return RunConsolidationAsync(request, cancellationToken);
        }

        if (path == "/api/admin/prune" && request.Method == Models.HttpMethod.POST)
        {
            return RunPruneAsync(request, cancellationToken);
        }

        if (path == "/api/admin/reindex" && request.Method == Models.HttpMethod.POST)
        {
            return RunReindexAsync(cancellationToken);
        }

        if (path == "/api/admin/compact" && request.Method == Models.HttpMethod.POST)
        {
            return RunCompactAsync(cancellationToken);
        }

        if (path == "/api/admin/maintenance/status" && request.Method == Models.HttpMethod.GET)
        {
            return GetMaintenanceStatusAsync(cancellationToken);
        }

        if (path == "/api/admin/health" && request.Method == Models.HttpMethod.GET)
        {
            return GetHealthAsync(cancellationToken);
        }

        return Task.FromResult(Response.NotFound("Admin endpoint not found"));
    }

    private async Task<Response> GetStatsAsync(CancellationToken cancellationToken)
    {
        var uptime = DateTime.UtcNow - _serverStartTime;

        // Use real stats from repository if available
        if (_repository is not null)
        {
            var repoStats = await _repository.GetStatsAsync(cancellationToken);

            var stats = new ServerStats
            {
                TotalNodes = repoStats.TotalNodes,
                AverageReinforcementScore = repoStats.AverageStrength,
                LastConsolidation = null,
                MemoryUsageMb = GC.GetTotalMemory(false) / 1024.0 / 1024.0,
                UptimeSeconds = uptime.TotalSeconds,
                ServerVersion = "1.0.0",
                DotNetVersion = Environment.Version.ToString()
            };

            return Response.Ok(stats);
        }

        // Fallback to placeholder stats
        var fallbackStats = new ServerStats
        {
            TotalNodes = 0,
            AverageReinforcementScore = 0.0,
            LastConsolidation = null,
            MemoryUsageMb = GC.GetTotalMemory(false) / 1024.0 / 1024.0,
            UptimeSeconds = uptime.TotalSeconds,
            ServerVersion = "1.0.0",
            DotNetVersion = Environment.Version.ToString()
        };

        return Response.Ok(fallbackStats);
    }

    private async Task<Response> RunConsolidationAsync(Request request, CancellationToken cancellationToken)
    {
        if (_maintenanceService is null)
        {
            var notConfigured = new AdminConsolidationResult
            {
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                NodesPruned = 0,
                NodesMerged = 0,
                NodesReinforced = 0,
                Success = false,
                Message = "Maintenance service not configured"
            };
            return Response.Ok(notConfigured);
        }

        // Parse optional similarity threshold from request
        var threshold = 0.8;
        if (request.Body is { Length: > 0 })
        {
            try
            {
                var body = request.GetBodyAs<ConsolidateRequest>();
                if (body?.SimilarityThreshold > 0)
                    threshold = body.SimilarityThreshold;
            }
            catch { /* Use default */ }
        }

        var result = await _maintenanceService.ConsolidateMemoriesAsync(threshold, cancellationToken);

        var apiResult = new AdminConsolidationResult
        {
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            NodesPruned = 0,
            NodesMerged = result.MemoriesMerged,
            NodesReinforced = result.MemoriesMerged,
            Success = result.Success,
            Message = result.ErrorMessage ?? $"Analyzed {result.MemoriesAnalyzed} memories, found {result.ClustersFound} clusters, archived {result.MemoriesArchived}"
        };

        return Response.Ok(apiResult);
    }

    private async Task<Response> RunPruneAsync(Request request, CancellationToken cancellationToken)
    {
        if (_maintenanceService is null)
        {
            return Response.Ok(new PruneResult
            {
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                Success = false,
                Message = "Maintenance service not configured"
            });
        }

        // Parse optional threshold from request
        var threshold = 0.1;
        if (request.Body is { Length: > 0 })
        {
            try
            {
                var body = request.GetBodyAs<PruneRequest>();
                if (body?.Threshold > 0)
                    threshold = body.Threshold;
            }
            catch { /* Use default */ }
        }

        var result = await _maintenanceService.ApplyDecayAsync(threshold, cancellationToken);

        return Response.Ok(new PruneResult
        {
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            MemoriesProcessed = result.MemoriesProcessed,
            MemoriesPruned = result.MemoriesPruned,
            AverageStrengthBefore = result.AverageStrengthBefore,
            AverageStrengthAfter = result.AverageStrengthAfter,
            Success = result.Success,
            Message = result.ErrorMessage
        });
    }

    private async Task<Response> RunReindexAsync(CancellationToken cancellationToken)
    {
        if (_maintenanceService is null)
        {
            return Response.Ok(new ReindexApiResult
            {
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                Success = false,
                Message = "Maintenance service not configured"
            });
        }

        var result = await _maintenanceService.ReindexAsync(cancellationToken);

        return Response.Ok(new ReindexApiResult
        {
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            MemoriesReindexed = result.MemoriesReindexed,
            EmbeddingsGenerated = result.EmbeddingsGenerated,
            TrigramsGenerated = result.TrigramsGenerated,
            Success = result.Success,
            Message = result.ErrorMessage
        });
    }

    private async Task<Response> RunCompactAsync(CancellationToken cancellationToken)
    {
        if (_maintenanceService is null)
        {
            return Response.Ok(new CompactApiResult
            {
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                Success = false,
                Message = "Maintenance service not configured"
            });
        }

        var result = await _maintenanceService.CompactDatabaseAsync(cancellationToken);

        return Response.Ok(new CompactApiResult
        {
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            SizeBeforeBytes = result.SizeBeforeBytes,
            SizeAfterBytes = result.SizeAfterBytes,
            SpaceSavedPercent = result.SpaceSavedPercent,
            Success = result.Success,
            Message = result.ErrorMessage
        });
    }

    private Task<Response> GetMaintenanceStatusAsync(CancellationToken cancellationToken)
    {
        if (_maintenanceService is null)
        {
            return Task.FromResult(Response.Ok(new MaintenanceStatusResponse
            {
                IsConfigured = false,
                IsRunning = false,
                Message = "Maintenance service not configured"
            }));
        }

        var status = _maintenanceService.GetStatus();

        return Task.FromResult(Response.Ok(new MaintenanceStatusResponse
        {
            IsConfigured = true,
            LastDecayRun = status.LastDecayRun,
            LastConsolidationRun = status.LastConsolidationRun,
            LastReindexRun = status.LastReindexRun,
            LastCompactRun = status.LastCompactRun,
            IsRunning = status.IsRunning,
            CurrentOperation = status.CurrentOperation
        }));
    }

    private Task<Response> GetHealthAsync(CancellationToken cancellationToken)
    {
        var checks = new Dictionary<string, HealthCheckResult>();
        var overallStatus = "healthy";

        // Memory repository check
        if (_repository is not null)
        {
            try
            {
                var _ = _repository.GetStatsAsync(cancellationToken).GetAwaiter().GetResult();
                checks["memory_repository"] = new() { Status = "healthy", Message = "OK" };
            }
            catch (Exception ex)
            {
                checks["memory_repository"] = new() { Status = "unhealthy", Message = ex.Message };
                overallStatus = "unhealthy";
            }
        }
        else
        {
            checks["memory_repository"] = new() { Status = "not_configured", Message = "Repository not configured" };
        }

        // Maintenance service check
        if (_maintenanceService is not null)
        {
            var status = _maintenanceService.GetStatus();
            checks["maintenance_service"] = new()
            {
                Status = "healthy",
                Message = status.IsRunning ? $"Running: {status.CurrentOperation}" : "Idle"
            };
        }
        else
        {
            checks["maintenance_service"] = new() { Status = "not_configured", Message = "Maintenance service not configured" };
        }

        var health = new HealthCheck
        {
            Status = overallStatus,
            Timestamp = DateTime.UtcNow,
            Checks = checks
        };

        return Task.FromResult(Response.Ok(health));
    }
}

public record ServerStats
{
    public int TotalNodes { get; init; }
    public double AverageReinforcementScore { get; init; }
    public DateTime? LastConsolidation { get; init; }
    public double MemoryUsageMb { get; init; }
    public double UptimeSeconds { get; init; }
    public string ServerVersion { get; init; } = string.Empty;
    public string DotNetVersion { get; init; } = string.Empty;
}

public record AdminConsolidationResult
{
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public int NodesPruned { get; init; }
    public int NodesMerged { get; init; }
    public int NodesReinforced { get; init; }
    public bool Success { get; init; }
    public string? Message { get; init; }
}

public record PruneResult
{
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public int MemoriesProcessed { get; init; }
    public int MemoriesPruned { get; init; }
    public double AverageStrengthBefore { get; init; }
    public double AverageStrengthAfter { get; init; }
    public bool Success { get; init; }
    public string? Message { get; init; }
}

public record ReindexApiResult
{
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public int MemoriesReindexed { get; init; }
    public int EmbeddingsGenerated { get; init; }
    public int TrigramsGenerated { get; init; }
    public bool Success { get; init; }
    public string? Message { get; init; }
}

public record CompactApiResult
{
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public long SizeBeforeBytes { get; init; }
    public long SizeAfterBytes { get; init; }
    public double SpaceSavedPercent { get; init; }
    public bool Success { get; init; }
    public string? Message { get; init; }
}

public record MaintenanceStatusResponse
{
    public bool IsConfigured { get; init; }
    public DateTime? LastDecayRun { get; init; }
    public DateTime? LastConsolidationRun { get; init; }
    public DateTime? LastReindexRun { get; init; }
    public DateTime? LastCompactRun { get; init; }
    public bool IsRunning { get; init; }
    public string? CurrentOperation { get; init; }
    public string? Message { get; init; }
}

public record ConsolidateRequest
{
    public double SimilarityThreshold { get; init; }
}

public record PruneRequest
{
    public double Threshold { get; init; }
}

public record HealthCheck
{
    public string Status { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public Dictionary<string, HealthCheckResult> Checks { get; init; } = [];
}

public record HealthCheckResult
{
    public string Status { get; init; } = string.Empty;
    public string? Message { get; init; }
}
