using System.Diagnostics;

namespace AgenticMemory.CodeIndex;

/// <summary>
/// Consumes IngestionJobs on a dedicated low-priority thread. Also implements IIngestionQueue so
/// callers can enqueue without coupling to the concrete type.
/// </summary>
public sealed class FileIngestionWorker : DedicatedWorker<IngestionJob>, IIngestionQueue
{
    private readonly FileIngestionService _ingestionService;
    private readonly ICodeIndexRepository _repository;
    private readonly ActiveProjectService _activeProject;
    private readonly WorkerStatusTracker  _statusTracker;
    private readonly ILogger<FileIngestionWorker> _logger;

    protected override string WorkerName => "FileIngestionWorker";

    public FileIngestionWorker(
        FileIngestionService ingestionService,
        ICodeIndexRepository repository,
        ActiveProjectService activeProject,
        WorkerStatusTracker  statusTracker,
        ILogger<FileIngestionWorker> logger)
    {
        _ingestionService = ingestionService;
        _repository       = repository;
        _activeProject    = activeProject;
        _statusTracker    = statusTracker;
        _logger           = logger;
    }

    // ── IIngestionQueue ───────────────────────────────────────────────────────

    public bool TryEnqueue(IngestionJob job)
    {
        if (!TryWrite(job)) return false;
        _statusTracker.SetQueueDepth(QueueDepth);
        var rel = job.ProjectRoot != null
            ? Path.GetRelativePath(job.ProjectRoot, job.FilePath)
            : Path.GetFileName(job.FilePath);
        _statusTracker.TrackIngestionEnqueue(new QueuedIngestionEntry(job.FilePath, rel));
        return true;
    }

    public int Depth => QueueDepth;

    public void Clear()
    {
        DrainQueue();
        _statusTracker.SetQueueDepth(0);
        _statusTracker.SetProcessing(false);
        _statusTracker.ClearIngestionQueue();
    }

    // ── Worker hooks ──────────────────────────────────────────────────────────

    protected override void OnWorkerStarted() =>
        _logger.LogInformation("FileIngestionWorker started");

    protected override void OnWorkerStopped() =>
        _logger.LogInformation("FileIngestionWorker stopped");

    protected override void OnBeforeJob(IngestionJob job, int remaining)
    {
        _statusTracker.SetQueueDepth(remaining);
        _statusTracker.TrackIngestionDequeue(job.FilePath);
    }

    protected override void OnAfterJob(IngestionJob job)
    {
        _statusTracker.SetQueueDepth(QueueDepth);
        _statusTracker.SetProcessing(QueueDepth > 0, null);
    }

    protected override void OnJobError(IngestionJob job, Exception ex)
    {
        _statusTracker.RecordError(new RecentErrorEntry(job.FilePath, ex.Message, DateTime.UtcNow));
        _logger.LogWarning(ex, "Ingestion failed: {File}", Path.GetFileName(job.FilePath));
    }

    // ── Core job execution (runs on dedicated thread) ─────────────────────────

    protected override void Execute(IngestionJob job, CancellationToken ct)
    {
        if (job.ProjectId != _activeProject.ActiveProjectId)
        {
            _logger.LogDebug("Dropping job for inactive project: {File}", Path.GetFileName(job.FilePath));
            return;
        }

        var relPath = job.ProjectRoot != null
            ? Path.GetRelativePath(job.ProjectRoot, job.FilePath)
            : Path.GetFileName(job.FilePath);
        _statusTracker.SetProcessing(true, relPath);

        var sw   = Stopwatch.StartNew();
        var isNew = _repository.GetByPathAsync(job.FilePath, ct).GetAwaiter().GetResult() is null;

        var record = _ingestionService.IngestAsync(
            job.FilePath, job.ProjectId, job.ProjectRoot, job.Force,
            job.SubProjectId, job.SubProjectRoot, ct).GetAwaiter().GetResult();

        sw.Stop();
        _statusTracker.RecordJob(new RecentJobEntry(
            RelativePath: record.RelativePath,
            Language:     record.Language,
            SymbolCount:  record.Symbols.Count,
            DurationMs:   sw.ElapsedMilliseconds,
            IndexedAt:    record.IndexedAt,
            WasNew:       isNew));
    }
}
