using System.Security.Cryptography;
using AgenticMemory.Brain.Interfaces;

namespace AgenticMemory.CodeIndex;

/// <summary>
/// 5-step ingestion pipeline: hash → extract context → get symbols → embed → upsert.
/// LLM summary generation is handled separately by SummaryWorker at low priority.
/// </summary>
public sealed class FileIngestionService
{
    private readonly CodeIndexService _codeIndex;
    private readonly ICodeIndexRepository _repository;
    private readonly IEmbeddingService _embedding;
    private readonly ISummaryQueue _summaryQueue;
    private readonly ILogger<FileIngestionService> _logger;

    public FileIngestionService(
        CodeIndexService codeIndex,
        ICodeIndexRepository repository,
        IEmbeddingService embedding,
        ISummaryQueue summaryQueue,
        ILogger<FileIngestionService> logger)
    {
        _codeIndex = codeIndex;
        _repository = repository;
        _embedding = embedding;
        _summaryQueue = summaryQueue;
        _logger = logger;
    }

    public async Task<CodeIndexRecord> IngestAsync(
        string filePath,
        string projectId,
        string? projectRoot = null,
        bool force = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        // Step 1: read bytes and compute content hash
        byte[] fileBytes;
        try { fileBytes = await File.ReadAllBytesAsync(filePath, ct); }
        catch (Exception ex) { throw new IOException($"Cannot read {filePath}: {ex.Message}", ex); }

        var contentHash = Convert.ToHexString(SHA256.HashData(fileBytes));
        var fileModifiedAt = File.GetLastWriteTimeUtc(filePath);

        // Step 2: skip if unchanged (hash match + not stale + not forced)
        if (!force)
        {
            var existing = await _repository.GetByPathAsync(filePath, ct);
            if (existing is not null && existing.ContentHash == contentHash && !existing.IsStale)
            {
                _logger.LogDebug("Skipping unchanged: {File}", Path.GetFileName(filePath));
                return existing;
            }
        }

        var relativePath = projectRoot != null
            ? Path.GetRelativePath(projectRoot, filePath)
            : Path.GetFileName(filePath);

        var record = new CodeIndexRecord
        {
            Id = LiteDbCodeIndexRepository.ComputeId(filePath),
            ProjectId = projectId,
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            RelativePath = relativePath,
            Language = DetectLanguage(filePath),
            ContentHash = contentHash,
            FileModifiedAt = fileModifiedAt,
            IndexedAt = DateTime.UtcNow,
            IsStale = false,
        };

        // Steps 3 & 4: extract context + symbols via the compiler provider
        string extractedContext;
        try
        {
            extractedContext = await _codeIndex.ExtractContextAsync(filePath, ct);
            record.ExtractedContext = extractedContext;

            var provider = _codeIndex.GetProvider(filePath);
            if (provider is not null)
            {
                record.ProviderType = provider.ProviderType;
                var symbols = await _codeIndex.GetSymbolsAsync(filePath, ct);
                record.Symbols = symbols.Select(s => new SymbolRecord
                {
                    Name = s.Name,
                    Kind = s.Kind,
                    Type = s.Type,
                    Accessibility = s.Accessibility,
                    Line = s.Line,
                }).ToList();
                record.SymbolsText = string.Join(" ", record.Symbols.Select(s => s.Name.ToLowerInvariant()));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Context extraction failed for {File}", Path.GetFileName(filePath));
            record.IngestionError = $"Context extraction: {ex.Message}";
            await _repository.UpsertAsync(record, ct);
            return record;
        }

        // Step 5: embed identifier document (file name + path + symbol names)
        var textToEmbed = BuildEmbedText(record);

        if (_embedding.IsAvailable && !string.IsNullOrWhiteSpace(textToEmbed))
        {
            try { record.Embedding = await _embedding.GetEmbeddingAsync(textToEmbed, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Embedding failed for {File}", Path.GetFileName(filePath));
            }
        }

        // Step 6: persist
        await _repository.UpsertAsync(record, ct);

        // Enqueue LLM summary generation at low priority (display-only, not needed for search)
        _summaryQueue.TryEnqueue(new SummaryJob(record.Id, filePath));

        return record;
    }

    private static string BuildEmbedText(CodeIndexRecord r)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(r.FileName).Append(" [").Append(r.Language).AppendLine("]");
        sb.AppendLine(r.RelativePath);

        if (r.Symbols.Count > 0)
        {
            var ordered = r.Symbols
                .OrderBy(s => s.Accessibility == "public" ? 0 : s.Accessibility == "internal" ? 1 : 2)
                .Select(s => s.Name);

            var symbolLine = string.Join(", ", ordered);
            if (symbolLine.Length > 300) symbolLine = symbolLine[..300];
            sb.Append(symbolLine);
        }

        var result = sb.ToString();
        return result.Length > 400 ? result[..400] : result;
    }

    private static string DetectLanguage(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".cs"           => "csharp",
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" => "javascript",
            ".py"           => "python",
            ".go"           => "go",
            ".rs"           => "rust",
            _               => "unknown"
        };

}
