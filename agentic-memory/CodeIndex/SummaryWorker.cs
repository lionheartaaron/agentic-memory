using System.Threading.Channels;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Configuration;

namespace AgenticMemory.CodeIndex;

/// <summary>
/// Generates LLM summaries for indexed files on a low-priority background thread.
/// Summaries are display-only; they are not used for embedding.
/// </summary>
public sealed class SummaryWorker : BackgroundService, ISummaryQueue
{
    private readonly Channel<SummaryJob> _channel;
    private readonly ICodeIndexRepository _repository;
    private readonly IGenerativeModelService _generative;
    private readonly GenerationSettings _generationSettings;
    private readonly WorkerStatusTracker _statusTracker;
    private readonly ILogger<SummaryWorker> _logger;

    private int _depth;

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
        ICodeIndexRepository repository,
        IGenerativeModelService generative,
        GenerationSettings generationSettings,
        WorkerStatusTracker statusTracker,
        ILogger<SummaryWorker> logger)
    {
        _repository = repository;
        _generative = generative;
        _generationSettings = generationSettings;
        _statusTracker = statusTracker;
        _logger = logger;

        _channel = Channel.CreateBounded<SummaryJob>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true,
        });
    }

    public bool TryEnqueue(SummaryJob job)
    {
        if (!_channel.Writer.TryWrite(job)) return false;
        _statusTracker.SetSummaryQueueDepth(Interlocked.Increment(ref _depth));
        return true;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { RunAsync(stoppingToken).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "SummaryWorker crashed");
            }
            finally { tcs.SetResult(); }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "SummaryWorker",
        };
        thread.Start();
        return tcs.Task;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("SummaryWorker started");

        await foreach (var job in _channel.Reader.ReadAllAsync(ct))
        {
            _statusTracker.SetSummaryQueueDepth(Math.Max(0, Interlocked.Decrement(ref _depth)));

            if (!_generative.IsAvailable) continue;

            try
            {
                // Load to get ExtractedContext for generation
                var snap = await _repository.GetByPathAsync(job.FilePath, ct);
                if (snap is null || !string.IsNullOrWhiteSpace(snap.LlmSummary)) continue;
                if (string.IsNullOrWhiteSpace(snap.ExtractedContext)) continue;

                var prompt = _generationSettings.TruncateIfNeeded($"Describe this file:\n\n{snap.ExtractedContext}");
                var summary = EnforceWordLimit(_generative.Generate(SummarySystemPrompt, prompt), 65);

                // Reload the freshest record before writing to avoid overwriting IsStale
                // or other fields that may have changed during generation
                var fresh = await _repository.GetByPathAsync(job.FilePath, ct);
                if (fresh is null || !string.IsNullOrWhiteSpace(fresh.LlmSummary)) continue;
                fresh.LlmSummary = summary;
                await _repository.UpsertAsync(fresh, ct);

                _logger.LogDebug("Summary generated: {File}", Path.GetFileName(job.FilePath));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Summary generation failed: {File}", Path.GetFileName(job.FilePath));
            }
        }

        _logger.LogInformation("SummaryWorker stopped");
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
