using AgenticMemory.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Brain.Maintenance;

/// <summary>
/// Background service that periodically runs maintenance tasks like decay and consolidation
/// </summary>
public class MaintenanceBackgroundService : IHostedService, IAsyncDisposable
{
    private readonly IMaintenanceService _maintenanceService;
    private readonly MaintenanceSettings _settings;
    private readonly ILogger<MaintenanceBackgroundService>? _logger;

    private CancellationTokenSource? _cts;
    private Task? _decayTask;
    private Task? _consolidationTask;
    private bool _isRunning;

    public MaintenanceBackgroundService(
        IMaintenanceService maintenanceService,
        MaintenanceSettings settings,
        ILogger<MaintenanceBackgroundService>? logger = null)
    {
        _maintenanceService = maintenanceService;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Whether the background service is currently running
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Start the background maintenance tasks (IHostedService implementation)
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop the background maintenance tasks (IHostedService implementation)
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return StopInternalAsync();
    }

    /// <summary>
    /// Start the background maintenance tasks
    /// </summary>
    public void Start()
    {
        if (_isRunning)
        {
            _logger?.LogWarning("Maintenance background service is already running");
            return;
        }

        _cts = new CancellationTokenSource();
        _isRunning = true;

        if (_settings.DecayEnabled)
        {
            _decayTask = RunDecayLoopAsync(_cts.Token);
            _logger?.LogInformation(
                "Decay background task started. Interval: {Interval}h, Prune threshold: {Threshold}",
                _settings.DecayIntervalHours, _settings.PruneThreshold);
        }
        else
        {
            _logger?.LogInformation("Decay background task is disabled");
        }

        if (_settings.ConsolidationEnabled)
        {
            _consolidationTask = RunConsolidationLoopAsync(_cts.Token);
            _logger?.LogInformation(
                "Consolidation background task started. Interval: {Interval}h, Similarity threshold: {Threshold}",
                _settings.ConsolidationIntervalHours, _settings.SimilarityThreshold);
        }
        else
        {
            _logger?.LogInformation("Consolidation background task is disabled");
        }
    }

    /// <summary>
    /// Stop the background maintenance tasks
    /// </summary>
    public async Task StopInternalAsync()
    {
        if (!_isRunning)
            return;

        _logger?.LogInformation("Stopping maintenance background service...");

        _cts?.Cancel();

        var tasks = new List<Task>();
        if (_decayTask != null) tasks.Add(_decayTask);
        if (_consolidationTask != null) tasks.Add(_consolidationTask);

        if (tasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            catch (TimeoutException)
            {
                _logger?.LogWarning("Timeout waiting for maintenance tasks to stop");
            }
        }

        _isRunning = false;
        _logger?.LogInformation("Maintenance background service stopped");
    }

    private async Task RunDecayLoopAsync(CancellationToken cancellationToken)
    {
        // Initial delay before first run
        await Task.Delay(TimeSpan.FromMinutes(_settings.InitialDelayMinutes), cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _logger?.LogDebug("Running scheduled decay operation");
                var result = await _maintenanceService.ApplyDecayAsync(_settings.PruneThreshold, cancellationToken);

                if (result.Success)
                {
                    _logger?.LogInformation(
                        "Scheduled decay completed: {Processed} processed, {Pruned} pruned",
                        result.MemoriesProcessed, result.MemoriesPruned);
                }
                else
                {
                    _logger?.LogWarning("Scheduled decay failed: {Error}", result.ErrorMessage);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unhandled error in decay loop");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(_settings.DecayIntervalHours), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunConsolidationLoopAsync(CancellationToken cancellationToken)
    {
        // Initial delay before first run (offset from decay)
        await Task.Delay(TimeSpan.FromMinutes(_settings.InitialDelayMinutes + 5), cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _logger?.LogDebug("Running scheduled consolidation operation");
                var result = await _maintenanceService.ConsolidateMemoriesAsync(_settings.SimilarityThreshold, cancellationToken);

                if (result.Success)
                {
                    _logger?.LogInformation(
                        "Scheduled consolidation completed: {Analyzed} analyzed, {Clusters} clusters, {Archived} archived",
                        result.MemoriesAnalyzed, result.ClustersFound, result.MemoriesArchived);
                }
                else
                {
                    _logger?.LogWarning("Scheduled consolidation failed: {Error}", result.ErrorMessage);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unhandled error in consolidation loop");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(_settings.ConsolidationIntervalHours), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopInternalAsync();
        _cts?.Dispose();
    }
}
