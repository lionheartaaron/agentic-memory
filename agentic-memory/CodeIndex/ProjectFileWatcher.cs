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
    private readonly ExcludedFolderMatcher _excluded;
    private readonly ILogger<ProjectFileWatcher> _logger;

    private FileSystemWatcher? _fsWatcher;
    private readonly object _watcherLock = new();
    private readonly Dictionary<string, DateTime> _debounceMap = new();
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(2);

    // Written under _watcherLock before the watcher starts; read on FSW callback threads.
    private volatile IReadOnlyList<SubProjectRecord> _activeSubProjects =
        Array.Empty<SubProjectRecord>();

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
        _excluded = new ExcludedFolderMatcher(settings.ExcludePatterns);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _activeProject.ActiveProjectChanged += OnActiveProjectChanged;

        // Resume the workspace that was active before restart
        if (!string.IsNullOrEmpty(_activeProject.ActiveProjectId))
        {
            var workspace = GetWorkspace(_activeProject.ActiveProjectId);
            if (workspace != null)
                await ActivateAsync(workspace, stoppingToken);
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

        var workspace = GetWorkspace(projectId);
        if (workspace == null)
        {
            _logger.LogWarning("Active workspace {Id} not found in store", projectId);
            return;
        }

        _ = Task.Run(() => ActivateAsync(workspace, CancellationToken.None));
    }

    private async Task ActivateAsync(WorkspaceRecord workspace, CancellationToken ct)
    {
        _statusTracker.SetActive(workspace.Id, workspace.Name);
        _logger.LogInformation("Activating workspace {Name} at {Root}", workspace.Name, workspace.RootPath);

        try
        {
            var (queued, alreadyCurrent) = await _scanner.ScanWorkspaceAsync(
                workspace.Id, workspace.RootPath, workspace.SubProjects, ct);
            var total = queued + alreadyCurrent;
            _statusTracker.SetTotalIndexable(total);
            _logger.LogInformation("Scan done: {T} total, {Q} queued, {C} current", total, queued, alreadyCurrent);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Staleness scan failed for {Root}", workspace.RootPath);
        }

        StartWatcher(workspace.Id, workspace.RootPath, workspace.SubProjects);
    }

    private void StartWatcher(string workspaceId, string root, IReadOnlyList<SubProjectRecord> subProjects)
    {
        lock (_watcherLock)
        {
            StopWatcherUnsafe();
            if (!Directory.Exists(root)) return;

            _activeSubProjects = subProjects;

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
            };

            watcher.Changed += (_, e) => OnFileEvent(e.FullPath, workspaceId, root);
            watcher.Created += (_, e) => OnFileEvent(e.FullPath, workspaceId, root);
            watcher.Deleted += (_, e) => OnFileDeleted(e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                OnFileDeleted(e.OldFullPath);
                OnFileEvent(e.FullPath, workspaceId, root);
            };

            _fsWatcher = watcher;
            _logger.LogInformation("FileSystemWatcher started for {Root}", root);
        }
    }

    private void OnFileEvent(string filePath, string workspaceId, string workspaceRoot)
    {
        if (!_settings.IndexedExtensions.Contains(
                Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase)) return;

        // Honor the configured ignore list on live events too — without this an `npm install`
        // floods the queue with node_modules files the initial scan would never have picked up.
        if (_excluded.IsExcluded(filePath, workspaceRoot)) return;

        lock (_debounceMap)
        {
            var now = DateTime.UtcNow;
            if (_debounceMap.TryGetValue(filePath, out var last) && now - last < DebounceWindow) return;
            _debounceMap[filePath] = now;
        }

        if (workspaceId != _activeProject.ActiveProjectId) return;

        // Longest-prefix match: file in react-dashboard/src/ → TypeScript sub-project
        var owner = _activeSubProjects
            .Where(sp => filePath.StartsWith(sp.RootPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(sp => sp.RootPath.Length)
            .FirstOrDefault();

        _queue.TryEnqueue(new IngestionJob(
            filePath,
            ProjectId:      workspaceId,
            ProjectRoot:    workspaceRoot,
            Force:          true,
            SubProjectId:   owner?.Id,
            SubProjectRoot: owner?.RootPath ?? workspaceRoot));
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

    private WorkspaceRecord? GetWorkspace(string workspaceId)
    {
        // Try the new workspaces key first, fall back to legacy projects key for migration period
        var json = _kv.Get("workspaces") ?? _kv.Get("projects");
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var workspaces = System.Text.Json.JsonSerializer.Deserialize<List<WorkspaceRecord>>(json);
            return workspaces?.Find(w => w.Id == workspaceId);
        }
        catch { }

        // Legacy fallback: reconstruct a minimal WorkspaceRecord from the old projects format
        try
        {
            var legacyJson = _kv.Get("projects");
            if (string.IsNullOrEmpty(legacyJson)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(legacyJson);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("Id", out var id) && id.GetString() == workspaceId)
                {
                    var name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? workspaceId : workspaceId;
                    var root = el.TryGetProperty("RootPath", out var r) ? r.GetString() ?? "" : "";
                    var created = el.TryGetProperty("CreatedAt", out var c) ? c.GetString() ?? "" : "";
                    return new WorkspaceRecord(workspaceId, name, root, created, []);
                }
            }
        }
        catch { }

        return null;
    }
}
