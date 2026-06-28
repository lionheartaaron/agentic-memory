using System.Collections.Concurrent;

namespace AgenticMemory.CodeIndex;

public record SubProjectStatusEntry(
    string SubProjectId,
    string Name,
    string Language,
    int IndexedFiles,
    int StaleFiles,
    int ErrorFiles);

public record QueuedIngestionEntry(string FilePath, string RelativePath);
public record QueuedSummaryEntry(string FilePath, string RelativePath);

public record WorkerStatusSnapshot(
    string? ActiveProjectId,
    string? ActiveProjectName,
    bool IsProcessing,
    string? CurrentFile,            // relative path being ingested right now, null when idle
    string? CurrentSummaryFile,     // relative path being summarized right now, null when idle
    int QueueDepth,
    int SummaryQueueDepth,
    int TotalIndexableFiles,
    int IndexedFiles,
    int StaleFiles,
    int ErrorFiles,
    IReadOnlyList<RecentJobEntry> RecentJobs,
    IReadOnlyList<RecentErrorEntry> RecentErrors,
    IReadOnlyList<SubProjectStatusEntry> SubProjectStatuses,
    IReadOnlyList<QueuedIngestionEntry> QueuedIngestions,
    IReadOnlyList<QueuedSummaryEntry> QueuedSummaries,
    // ── Reference analysis worker (appended at end to preserve positional order) ──
    string? CurrentReferenceFile,   // relative path being reference-analyzed, null when idle
    int ReferenceQueueDepth,
    int TotalSymbolReferences);     // live count from symbol_references collection

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
public sealed class WorkerStatusTracker : IDisposable
{
    private const int MaxJobs = 20;
    private const int MaxErrors = 10;

    /// <summary>
    /// Set when ingestion queue depth reaches zero AND no file is actively being ingested.
    /// ReferenceIndexWorker waits on this instead of spin-sleeping, so it never holds the
    /// thread (or a DB lock) while polling.
    /// </summary>
    public ManualResetEventSlim IngestionIdleEvent { get; } = new ManualResetEventSlim(initialState: true);

    private readonly ConcurrentQueue<RecentJobEntry> _recentJobs = new();
    private readonly ConcurrentQueue<RecentErrorEntry> _recentErrors = new();
    private readonly ConcurrentDictionary<string, QueuedIngestionEntry> _queuedIngestions = new();
    private readonly ConcurrentDictionary<string, QueuedSummaryEntry> _queuedSummaries = new();

    // Ingestion state
    private volatile int _queueDepth;
    private volatile bool _isProcessing;
    private volatile string? _currentFile;          // relative path being ingested

    // Summary state
    private volatile int _summaryQueueDepth;
    private volatile string? _currentSummaryFile;   // relative path being summarized

    // Reference analysis state
    private volatile int _referenceQueueDepth;
    private volatile string? _currentReferenceFile; // relative path being reference-analyzed

    private volatile string? _activeProjectId;
    private volatile string? _activeProjectName;
    private volatile int _totalIndexableFiles;

    public void SetActive(string? projectId, string? projectName)
    {
        _activeProjectId = projectId;
        _activeProjectName = projectName;
        if (projectId == null)
        {
            _isProcessing         = false;
            _currentFile          = null;
            _currentSummaryFile   = null;
            _currentReferenceFile = null;
            _totalIndexableFiles  = 0;
            _queueDepth           = 0;
            _referenceQueueDepth  = 0;
            _queuedIngestions.Clear();
            _queuedSummaries.Clear();
            IngestionIdleEvent.Set();
        }
    }

    public int  IngestionQueueDepth => _queueDepth;

    /// <summary>True when the ingestion queue is empty and no file is actively being ingested.</summary>
    public bool IsIngestionIdle => _queueDepth == 0 && !_isProcessing;

    public void SetTotalIndexable(int count) => _totalIndexableFiles = count;

    public void SetQueueDepth(int depth)
    {
        _queueDepth = depth;
        UpdateIngestionIdleEvent();
    }

    public void SetSummaryQueueDepth(int depth) => _summaryQueueDepth = depth;

    /// <param name="processing">True while a file is actively being ingested. Also true between
    /// items if the queue still has depth, so the badge stays "Indexing" during bursts.</param>
    /// <param name="relativePath">Relative path of the file currently being processed, or null.</param>
    public void SetProcessing(bool processing, string? relativePath = null)
    {
        _isProcessing = processing;
        _currentFile  = relativePath;
        UpdateIngestionIdleEvent();
    }

    private void UpdateIngestionIdleEvent()
    {
        if (_queueDepth == 0 && !_isProcessing)
            IngestionIdleEvent.Set();
        else
            IngestionIdleEvent.Reset();
    }

    public void SetSummaryProcessing(bool processing, string? relativePath = null)
    {
        _currentSummaryFile = processing ? relativePath : null;
    }

    public void SetReferenceQueueDepth(int depth) => _referenceQueueDepth = depth;

    public void SetReferenceProcessing(bool processing, string? relativePath = null)
    {
        _currentReferenceFile = processing ? relativePath : null;
    }

    public void TrackIngestionEnqueue(QueuedIngestionEntry entry) =>
        _queuedIngestions[entry.FilePath] = entry;

    public void TrackIngestionDequeue(string filePath) =>
        _queuedIngestions.TryRemove(filePath, out _);

    public void ClearIngestionQueue() => _queuedIngestions.Clear();

    public void TrackSummaryEnqueue(QueuedSummaryEntry entry) =>
        _queuedSummaries[entry.FilePath] = entry;

    public void TrackSummaryDequeue(string filePath) =>
        _queuedSummaries.TryRemove(filePath, out _);

    public void Reset()
    {
        _activeProjectId   = null;
        _activeProjectName = null;
        _isProcessing          = false;
        _currentFile           = null;
        _currentSummaryFile    = null;
        _currentReferenceFile  = null;
        _queueDepth            = 0;
        _summaryQueueDepth     = 0;
        _referenceQueueDepth   = 0;
        _totalIndexableFiles   = 0;
        _queuedIngestions.Clear();
        _queuedSummaries.Clear();
        while (_recentJobs.TryDequeue(out _)) { }
        while (_recentErrors.TryDequeue(out _)) { }
        IngestionIdleEvent.Set(); // no ingestion in progress after reset
    }

    public void Dispose() => IngestionIdleEvent.Dispose();

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

    public WorkerStatusSnapshot GetSnapshot(
        ICodeIndexRepository repository,
        WorkspaceRecord? activeWorkspace = null)
    {
        int indexed = 0, stale = 0, errored = 0;
        if (_activeProjectId != null)
        {
            try { (indexed, stale, errored) = repository.GetProjectStatsAsync(_activeProjectId).GetAwaiter().GetResult(); }
            catch { }
        }

        IReadOnlyList<SubProjectStatusEntry> subStatuses = activeWorkspace?.SubProjects
            .Select(sp =>
            {
                try
                {
                    var (idx, st, err) = repository.GetSubProjectStatsAsync(sp.Id).GetAwaiter().GetResult();
                    return new SubProjectStatusEntry(sp.Id, sp.Name, sp.Language, idx, st, err);
                }
                catch { return new SubProjectStatusEntry(sp.Id, sp.Name, sp.Language, 0, 0, 0); }
            })
            .ToList() ?? (IReadOnlyList<SubProjectStatusEntry>)[];

        // IsProcessing is true whenever any worker has remaining work
        var isProcessing = _isProcessing
            || _summaryQueueDepth   > 0 || _currentSummaryFile   != null
            || _referenceQueueDepth > 0 || _currentReferenceFile != null;

        int symRefCount = 0;
        try { symRefCount = repository.CountSymbolReferencesAsync().GetAwaiter().GetResult(); }
        catch { }

        return new WorkerStatusSnapshot(
            ActiveProjectId: _activeProjectId,
            ActiveProjectName: _activeProjectName,
            IsProcessing: isProcessing,
            CurrentFile: _currentFile,
            CurrentSummaryFile: _currentSummaryFile,
            QueueDepth: _queueDepth,
            SummaryQueueDepth: _summaryQueueDepth,
            TotalIndexableFiles: _totalIndexableFiles,
            IndexedFiles: indexed,
            StaleFiles: stale,
            ErrorFiles: errored,
            RecentJobs: _recentJobs.Reverse().ToList(),
            RecentErrors: _recentErrors.Reverse().ToList(),
            SubProjectStatuses: subStatuses,
            QueuedIngestions: _queuedIngestions.Values.ToList(),
            QueuedSummaries: _queuedSummaries.Values.ToList(),
            CurrentReferenceFile: _currentReferenceFile,
            ReferenceQueueDepth: _referenceQueueDepth,
            TotalSymbolReferences: symRefCount);
    }
}
