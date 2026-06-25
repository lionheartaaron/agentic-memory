using System.Diagnostics;
using System.Threading.Channels;

namespace AgenticMemory.CodeIndex;

/// <summary>
/// BackgroundService that consumes IngestionJobs from a bounded Channel.
/// Also implements IIngestionQueue so callers can enqueue without a circular dependency.
/// Jobs for projects other than the current active project are silently dropped.
/// </summary>
public sealed class FileIngestionWorker : BackgroundService, IIngestionQueue
{
    private readonly Channel<IngestionJob> _channel;
    private readonly FileIngestionService _ingestionService;
    private readonly ICodeIndexRepository _repository;
    private readonly ActiveProjectService _activeProject;
    private readonly WorkerStatusTracker _statusTracker;
    private readonly ILogger<FileIngestionWorker> _logger;

    private int _depth;

    public FileIngestionWorker(
        FileIngestionService ingestionService,
        ICodeIndexRepository repository,
        ActiveProjectService activeProject,
        WorkerStatusTracker statusTracker,
        ILogger<FileIngestionWorker> logger)
    {
        _ingestionService = ingestionService;
        _repository = repository;
        _activeProject = activeProject;
        _statusTracker = statusTracker;
        _logger = logger;

        _channel = Channel.CreateBounded<IngestionJob>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true,
        });
    }

    // ── IIngestionQueue ───────────────────────────────────────────────────────

    public bool TryEnqueue(IngestionJob job)
    {
        if (_channel.Writer.TryWrite(job))
        {
            Interlocked.Increment(ref _depth);
            _statusTracker.SetQueueDepth(_depth);
            return true;
        }
        return false;
    }

    public int Depth => _depth;

    public void Clear()
    {
        while (_channel.Reader.TryRead(out _))
            Interlocked.Decrement(ref _depth);

        Interlocked.Exchange(ref _depth, 0);
        _statusTracker.SetQueueDepth(0);
        _statusTracker.SetProcessing(false);
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FileIngestionWorker started");

        await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            var remaining = Interlocked.Decrement(ref _depth);
            _statusTracker.SetQueueDepth(Math.Max(0, remaining));

            // Drop jobs for the wrong project
            if (job.ProjectId != _activeProject.ActiveProjectId)
            {
                _logger.LogDebug("Dropping job for inactive project: {File}", Path.GetFileName(job.FilePath));
                continue;
            }

            var sw = Stopwatch.StartNew();
            _statusTracker.SetProcessing(true, Path.GetFileName(job.FilePath));

            try
            {
                var isNew = await _repository.GetByPathAsync(job.FilePath, stoppingToken) is null;
                var record = await _ingestionService.IngestAsync(
                    job.FilePath, job.ProjectId, job.ProjectRoot, job.Force, stoppingToken);

                sw.Stop();
                _statusTracker.RecordJob(new RecentJobEntry(
                    RelativePath: record.RelativePath,
                    Language: record.Language,
                    SymbolCount: record.Symbols.Count,
                    DurationMs: sw.ElapsedMilliseconds,
                    IndexedAt: record.IndexedAt,
                    WasNew: isNew));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _statusTracker.RecordError(new RecentErrorEntry(
                    RelativePath: job.FilePath,
                    Error: ex.Message,
                    OccurredAt: DateTime.UtcNow));
                _logger.LogWarning(ex, "Ingestion failed: {File}", Path.GetFileName(job.FilePath));
            }
            finally
            {
                var depth = _depth;
                _statusTracker.SetProcessing(depth > 0, null);
            }
        }

        _logger.LogInformation("FileIngestionWorker stopped");
    }
}
