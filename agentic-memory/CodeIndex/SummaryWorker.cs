using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Configuration;

namespace AgenticMemory.CodeIndex;

/// <summary>
/// Generates LLM summaries for indexed files on a dedicated low-priority thread.
/// Summaries are display-only and used to improve embedding quality.
/// </summary>
public sealed class SummaryWorker : DedicatedWorker<SummaryJob>, ISummaryQueue
{
    private readonly ICodeIndexRepository    _repository;
    private readonly IGenerativeModelService _generative;
    private readonly GenerationSettings      _generationSettings;
    private readonly IEmbeddingService       _embedding;
    private readonly WorkerStatusTracker     _statusTracker;
    private readonly ILogger<SummaryWorker>  _logger;

    protected override string WorkerName => "SummaryWorker";

    private const string SummarySystemPrompt =
        "You are a code indexing assistant. Your output becomes an embedding vector used for semantic search. " +
        "A developer will search the codebase by describing what they need; your summary must contain the " +
        "specific terms that match their query.\n\n" +
        "Rules:\n" +
        "- Write exactly 1–2 prose sentences. No bullet points. No hyphens as list markers. " +
        "No labeled sections. No line breaks within the output.\n" +
        "- Under 60 words. Hard limit.\n" +
        "- Lead with the file's specific role: name the actual framework, library, domain entity, or protocol.\n" +
        "- Use the proper nouns in the structural summary: endpoint paths, domain type names, library names, protocol names.\n" +
        "- Do not write 'component rendering', 'event handling', or 'state management' as standalone concepts.";

    public SummaryWorker(
        ICodeIndexRepository    repository,
        IGenerativeModelService generative,
        GenerationSettings      generationSettings,
        IEmbeddingService       embedding,
        WorkerStatusTracker     statusTracker,
        ILogger<SummaryWorker>  logger)
    {
        _repository         = repository;
        _generative         = generative;
        _generationSettings = generationSettings;
        _embedding          = embedding;
        _statusTracker      = statusTracker;
        _logger             = logger;
    }

    // ── ISummaryQueue ─────────────────────────────────────────────────────────

    public bool TryEnqueue(SummaryJob job)
    {
        if (!TryWrite(job)) return false;
        _statusTracker.SetSummaryQueueDepth(QueueDepth);
        var display = job.RelativePath ?? Path.GetFileName(job.FilePath);
        _statusTracker.TrackSummaryEnqueue(new QueuedSummaryEntry(job.FilePath, display));
        return true;
    }

    // ── Worker hooks ──────────────────────────────────────────────────────────

    protected override void OnWorkerStarted() =>
        _logger.LogInformation("SummaryWorker started");

    protected override void OnWorkerStopped() =>
        _logger.LogInformation("SummaryWorker stopped");

    protected override void OnAfterJob(SummaryJob job)
    {
        _statusTracker.SetSummaryQueueDepth(QueueDepth);
        _statusTracker.TrackSummaryDequeue(job.FilePath);
        _statusTracker.SetSummaryProcessing(false);
    }

    protected override void OnJobError(SummaryJob job, Exception ex) =>
        _logger.LogWarning(ex, "Summary generation failed: {File}", Path.GetFileName(job.FilePath));

    // ── Core job execution (runs on dedicated thread) ─────────────────────────

    protected override void Execute(SummaryJob job, CancellationToken ct)
    {
        if (!_generative.IsAvailable) return;

        var display = job.RelativePath ?? Path.GetFileName(job.FilePath);
        _statusTracker.SetSummaryProcessing(true, display);

        var snap = _repository.GetByPathAsync(job.FilePath, ct).GetAwaiter().GetResult();
        if (snap is null || !string.IsNullOrWhiteSpace(snap.LlmSummary)) return;
        if (string.IsNullOrWhiteSpace(snap.ExtractedContext)) return;

        var prompt  = _generationSettings.TruncateIfNeeded($"Describe this file:\n\n{snap.ExtractedContext}");
        var summary = EnforceWordLimit(_generative.Generate(SummarySystemPrompt, prompt), 65);

        // Reload to avoid clobbering fields changed during generation
        var fresh = _repository.GetByPathAsync(job.FilePath, ct).GetAwaiter().GetResult();
        if (fresh is null || !string.IsNullOrWhiteSpace(fresh.LlmSummary)) return;
        fresh.LlmSummary = summary;

        if (_embedding.IsAvailable)
        {
            try
            {
                var embedText = FileIngestionService.BuildEmbedText(fresh);
                fresh.Embedding = _embedding.GetEmbeddingAsync(embedText, ct).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Re-embed after summary failed: {File}", Path.GetFileName(job.FilePath));
            }
        }

        _repository.UpsertAsync(fresh, ct).GetAwaiter().GetResult();
        _logger.LogDebug("Summary + re-embed done: {File}", Path.GetFileName(job.FilePath));
    }

    private static string EnforceWordLimit(string text, int maxWords)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= maxWords) return text;
        for (var i = maxWords - 1; i >= maxWords - 10 && i >= 0; i--)
        {
            if (words[i].EndsWith('.') || words[i].EndsWith('!') || words[i].EndsWith('?'))
                return string.Join(' ', words[..(i + 1)]);
        }
        return string.Join(' ', words[..maxWords]) + ".";
    }
}
