using System.Collections.Concurrent;

namespace AgenticMemory.CodeIndex;

public record WorkerStatusSnapshot(
    string? ActiveProjectId,
    string? ActiveProjectName,
    bool IsProcessing,
    string? CurrentFile,
    int QueueDepth,
    int SummaryQueueDepth,
    int TotalIndexableFiles,
    int IndexedFiles,
    int StaleFiles,
    int ErrorFiles,
    IReadOnlyList<RecentJobEntry> RecentJobs,
    IReadOnlyList<RecentErrorEntry> RecentErrors);

public record RecentJobEntry(
    string RelativePath,
    string Language,
    int SymbolCount,
    long DurationMs,
    DateTime IndexedAt,
    bool WasNew);

public record RecentErrorEntry(
    string RelativePath,
    string Error,
    DateTime OccurredAt);

/// <summary>
/// Thread-safe in-memory state for the worker, read by the status API endpoint.
/// </summary>
public sealed class WorkerStatusTracker
{
    private const int MaxJobs = 20;
    private const int MaxErrors = 10;

    private readonly ConcurrentQueue<RecentJobEntry> _recentJobs = new();
    private readonly ConcurrentQueue<RecentErrorEntry> _recentErrors = new();

    private volatile int _queueDepth;
    private volatile int _summaryQueueDepth;
    private volatile bool _isProcessing;
    private volatile string? _currentFile;
    private volatile string? _activeProjectId;
    private volatile string? _activeProjectName;
    private volatile int _totalIndexableFiles;

    public void SetActive(string? projectId, string? projectName)
    {
        _activeProjectId = projectId;
        _activeProjectName = projectName;
        if (projectId == null)
        {
            _isProcessing = false;
            _currentFile = null;
            _totalIndexableFiles = 0;
            _queueDepth = 0;
        }
    }

    public void SetTotalIndexable(int count) => _totalIndexableFiles = count;
    public void SetQueueDepth(int depth) => _queueDepth = depth;
    public void SetSummaryQueueDepth(int depth) => _summaryQueueDepth = depth;

    public void SetProcessing(bool processing, string? currentFile = null)
    {
        _isProcessing = processing;
        _currentFile = currentFile;
    }

    public void RecordJob(RecentJobEntry entry)
    {
        _recentJobs.Enqueue(entry);
        while (_recentJobs.Count > MaxJobs) _recentJobs.TryDequeue(out _);
    }

    public void RecordError(RecentErrorEntry entry)
    {
        _recentErrors.Enqueue(entry);
        while (_recentErrors.Count > MaxErrors) _recentErrors.TryDequeue(out _);
    }

    public WorkerStatusSnapshot GetSnapshot(ICodeIndexRepository repository)
    {
        int indexed = 0, stale = 0, errored = 0;
        if (_activeProjectId != null)
        {
            try { (indexed, stale, errored) = repository.GetProjectStatsAsync(_activeProjectId).GetAwaiter().GetResult(); }
            catch { }
        }

        return new WorkerStatusSnapshot(
            ActiveProjectId: _activeProjectId,
            ActiveProjectName: _activeProjectName,
            IsProcessing: _isProcessing,
            CurrentFile: _currentFile,
            QueueDepth: _queueDepth,
            SummaryQueueDepth: _summaryQueueDepth,
            TotalIndexableFiles: _totalIndexableFiles,
            IndexedFiles: indexed,
            StaleFiles: stale,
            ErrorFiles: errored,
            RecentJobs: _recentJobs.Reverse().ToList(),
            RecentErrors: _recentErrors.Reverse().ToList());
    }
}
