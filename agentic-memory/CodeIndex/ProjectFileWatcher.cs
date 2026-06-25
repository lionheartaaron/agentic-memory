using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Configuration;

namespace AgenticMemory.CodeIndex;

/// <summary>
/// BackgroundService that subscribes to ActiveProjectService and manages a FileSystemWatcher
/// for the active project. On activation it runs a staleness scan then watches for live changes.
/// </summary>
public sealed class ProjectFileWatcher : BackgroundService
{
    private readonly ActiveProjectService _activeProject;
    private readonly StalenessScanner _scanner;
    private readonly IIngestionQueue _queue;
    private readonly ICodeIndexRepository _repository;
    private readonly WorkerStatusTracker _statusTracker;
    private readonly IKeyValueStore _kv;
    private readonly CodeIndexSettings _settings;
    private readonly ILogger<ProjectFileWatcher> _logger;

    private FileSystemWatcher? _fsWatcher;
    private readonly object _watcherLock = new();
    private readonly Dictionary<string, DateTime> _debounceMap = new();
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(2);

    public ProjectFileWatcher(
        ActiveProjectService activeProject,
        StalenessScanner scanner,
        IIngestionQueue queue,
        ICodeIndexRepository repository,
        WorkerStatusTracker statusTracker,
        IKeyValueStore kv,
        CodeIndexSettings settings,
        ILogger<ProjectFileWatcher> logger)
    {
        _activeProject = activeProject;
        _scanner = scanner;
        _queue = queue;
        _repository = repository;
        _statusTracker = statusTracker;
        _kv = kv;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _activeProject.ActiveProjectChanged += OnActiveProjectChanged;

        // Resume the project that was active before restart
        if (!string.IsNullOrEmpty(_activeProject.ActiveProjectId))
        {
            var root = GetProjectRoot(_activeProject.ActiveProjectId);
            if (root != null)
                await ActivateAsync(_activeProject.ActiveProjectId, root, stoppingToken);
        }

        await Task.Delay(Timeout.Infinite, stoppingToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        _activeProject.ActiveProjectChanged -= OnActiveProjectChanged;
        StopWatcher();
    }

    private void OnActiveProjectChanged(string? projectId)
    {
        StopWatcher();
        _queue.Clear();

        if (string.IsNullOrEmpty(projectId))
        {
            _statusTracker.SetActive(null, null);
            return;
        }

        var root = GetProjectRoot(projectId);
        if (root == null)
        {
            _logger.LogWarning("Active project {Id} not found in project store", projectId);
            return;
        }

        _ = Task.Run(() => ActivateAsync(projectId, root, CancellationToken.None));
    }

    private async Task ActivateAsync(string projectId, string root, CancellationToken ct)
    {
        var name = GetProjectName(projectId) ?? projectId;
        _statusTracker.SetActive(projectId, name);

        _logger.LogInformation("Activating project {Name} at {Root}", name, root);

        try
        {
            var (queued, alreadyCurrent) = await _scanner.ScanAsync(projectId, root, ct);
            var total = queued + alreadyCurrent;
            _statusTracker.SetTotalIndexable(total);
            _logger.LogInformation("Scan done: {T} total, {Q} queued, {C} current", total, queued, alreadyCurrent);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Staleness scan failed for {Root}", root);
        }

        StartWatcher(projectId, root);
    }

    private void StartWatcher(string projectId, string root)
    {
        lock (_watcherLock)
        {
            StopWatcherUnsafe();
            if (!Directory.Exists(root)) return;

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
            };

            watcher.Changed += (_, e) => OnFileEvent(e.FullPath, projectId, root);
            watcher.Created += (_, e) => OnFileEvent(e.FullPath, projectId, root);
            watcher.Deleted += (_, e) => OnFileDeleted(e.FullPath);
            watcher.Renamed += (_, e) => { OnFileDeleted(e.OldFullPath); OnFileEvent(e.FullPath, projectId, root); };

            _fsWatcher = watcher;
            _logger.LogInformation("FileSystemWatcher started for {Root}", root);
        }
    }

    private void OnFileEvent(string filePath, string projectId, string projectRoot)
    {
        if (!_settings.IndexedExtensions.Contains(
                Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase)) return;

        lock (_debounceMap)
        {
            var now = DateTime.UtcNow;
            if (_debounceMap.TryGetValue(filePath, out var last) && now - last < DebounceWindow) return;
            _debounceMap[filePath] = now;
        }

        if (projectId != _activeProject.ActiveProjectId) return;

        _queue.TryEnqueue(new IngestionJob(filePath, projectId, projectRoot, Force: true));
    }

    private void OnFileDeleted(string filePath)
    {
        _ = Task.Run(async () =>
        {
            try { await _repository.DeleteByPathAsync(filePath); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to remove deleted file from index: {File}", filePath); }
        });
    }

    private void StopWatcher()
    {
        lock (_watcherLock) { StopWatcherUnsafe(); }
    }

    private void StopWatcherUnsafe()
    {
        if (_fsWatcher is null) return;
        _fsWatcher.EnableRaisingEvents = false;
        _fsWatcher.Dispose();
        _fsWatcher = null;
    }

    private string? GetProjectRoot(string projectId) => LookupProject(projectId, "RootPath");
    private string? GetProjectName(string projectId) => LookupProject(projectId, "Name");

    private string? LookupProject(string projectId, string field)
    {
        var json = _kv.Get("projects");
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("Id", out var id) && id.GetString() == projectId &&
                    el.TryGetProperty(field, out var val))
                    return val.GetString();
            }
        }
        catch { }
        return null;
    }
}
