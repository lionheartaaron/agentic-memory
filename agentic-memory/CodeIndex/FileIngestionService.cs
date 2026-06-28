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
    private readonly IReferenceQueue _referenceQueue;
    private readonly ILogger<FileIngestionService> _logger;

    public FileIngestionService(
        CodeIndexService codeIndex,
        ICodeIndexRepository repository,
        IEmbeddingService embedding,
        ISummaryQueue summaryQueue,
        IReferenceQueue referenceQueue,
        ILogger<FileIngestionService> logger)
    {
        _codeIndex      = codeIndex;
        _repository     = repository;
        _embedding      = embedding;
        _summaryQueue   = summaryQueue;
        _referenceQueue = referenceQueue;
        _logger         = logger;
    }

    public async Task<CodeIndexRecord> IngestAsync(
        string filePath,
        string projectId,
        string? projectRoot = null,
        bool force = false,
        string? subProjectId = null,
        string? subProjectRoot = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        // Step 1: read bytes and compute content hash
        byte[] fileBytes;
        try { fileBytes = await File.ReadAllBytesAsync(filePath, ct); }
        catch (Exception ex) { throw new IOException($"Cannot read {filePath}: {ex.Message}", ex); }

        var contentHash    = Convert.ToHexString(SHA256.HashData(fileBytes));
        var fileModifiedAt = File.GetLastWriteTimeUtc(filePath);

        // Always read existing record — needed for both the skip check and the reference-change heuristic.
        var existing = await _repository.GetByPathAsync(filePath, ct);
        var oldSymbolsText = existing?.SymbolsText;

        // Step 2: skip if unchanged (hash match + not stale + not forced)
        if (!force && existing is not null && existing.ContentHash == contentHash && !existing.IsStale)
        {
            // Patch ownership fields when the file was previously indexed under a different sub-project
            // (e.g., workspace re-discovery assigns a sub-project ID to a previously un-owned file)
            if (!string.IsNullOrEmpty(subProjectId) && existing.SubProjectId != subProjectId)
            {
                existing.SubProjectId = subProjectId;
                existing.SubProjectNamespace = $"sub:{subProjectId}";
                var effectiveRootForPatch = subProjectRoot ?? projectRoot;
                if (effectiveRootForPatch != null)
                    existing.RelativePath = Path.GetRelativePath(effectiveRootForPatch, filePath);
                await _repository.UpsertAsync(existing, ct);
            }
            _logger.LogDebug("Skipping unchanged: {File}", Path.GetFileName(filePath));
            return existing;
        }

        // Prefer subProjectRoot so React files are relative to react-dashboard/, not the workspace root
        var effectiveRoot = subProjectRoot ?? projectRoot;
        var relativePath = effectiveRoot != null
            ? Path.GetRelativePath(effectiveRoot, filePath)
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
            SubProjectId = subProjectId ?? "",
            SubProjectNamespace = string.IsNullOrEmpty(subProjectId) ? "" : $"sub:{subProjectId}",
        };

        // Record whether TypeScript types could resolve at index time (node_modules present). Drives the
        // dashboard "indexed without node_modules" warning and the staleness scanner's auto-reindex.
        if (record.Language == "typescript")
            record.TypeScriptTypesResolved =
                TypeScript.TypeScriptLibResolver.HasResolvableTypes(effectiveRoot ?? Path.GetDirectoryName(filePath)!);

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
                    // P1 Tier 0 structured shape (shared POCO sub-objects — assign by reference)
                    EndLine                = s.EndLine,
                    ContainingTypeFullName = s.ContainingTypeFullName,
                    ContainingNamespace    = s.ContainingNamespace,
                    SymbolDocId            = s.SymbolDocId,
                    Parameters             = s.Parameters,
                    ReturnTypeUnwrapped    = s.ReturnTypeUnwrapped,
                    Modifiers              = s.Modifiers,
                    IsStatic               = s.IsStatic,
                    IsAbstract             = s.IsAbstract,
                    IsSealed               = s.IsSealed,
                    IsVirtual              = s.IsVirtual,
                    IsOverride             = s.IsOverride,
                    IsAsync                = s.IsAsync,
                    EnumMembers            = s.EnumMembers,
                    EnumUnderlyingType     = s.EnumUnderlyingType,
                    IsFlags                = s.IsFlags,
                    Attributes             = s.Attributes,
                    ConstantValue          = s.ConstantValue,
                    InitializerExpression  = s.InitializerExpression,
                    ImplementsIDisposable      = s.ImplementsIDisposable,
                    ImplementsIAsyncDisposable = s.ImplementsIAsyncDisposable,
                    IsBackgroundService        = s.IsBackgroundService,
                    HasStaticMutableState      = s.HasStaticMutableState,
                    // P2 intent & contracts
                    DocSummary                 = s.DocSummary,
                    DocRemarks                 = s.DocRemarks,
                    ParamDocs                  = s.ParamDocs,
                    ReturnsDoc                 = s.ReturnsDoc,
                    DocumentedExceptions       = s.DocumentedExceptions,
                    IsDeprecated               = s.IsDeprecated,
                    DeprecationMessage         = s.DeprecationMessage,
                    ValidationRules            = s.ValidationRules,
                    NlDescription              = s.NlDescription,
                    IsAwaitable                = s.IsAwaitable,
                    IsAsyncEnumerable          = s.IsAsyncEnumerable,
                    UsesLock                   = s.UsesLock,
                    BlocksOnAsync              = s.BlocksOnAsync,
                    UsesInterlocked            = s.UsesInterlocked,
                    // P4 type structure
                    TypeParameters             = s.TypeParameters,
                    BaseChain                  = s.BaseChain,
                    Interfaces                 = s.Interfaces,
                    OverriddenSymbolId         = s.OverriddenSymbolId,
                    // P5 behavioral
                    ThrownExceptions           = s.ThrownExceptions,
                }).ToList();
                record.SymbolsText = string.Join(" ", record.Symbols.Select(s => s.Name.ToLowerInvariant()));

                // Phase 4: semantic metadata (domain tags, imports, type hierarchy, diagnostics)
                try
                {
                    var meta = await provider.ExtractSemanticMetadataAsync(filePath, ct);
                    record.DomainTags        = meta.DomainTags;
                    record.Imports           = meta.Imports;
                    record.TypeHierarchy     = meta.TypeHierarchy;
                    record.DiagnosticSummary = meta.DiagnosticSummary;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Semantic metadata extraction failed for {File}; skipped", Path.GetFileName(filePath));
                }

                // P1 near-free: declared test-file convention (imports a known test framework, or lives
                // under a *Tests project / test path). Drives the test-linkage rollup in ReferenceIndexWorker.
                (record.IsTestFile, record.TestFramework) = DetectTestFramework(record);

                // P2: denormalized indexable rollups from the enriched symbols.
                record.HasValidation = record.Symbols.Any(s => s.ValidationRules.Count > 0);
                if (record.Symbols.Any(s => s.IsDeprecated) && !record.DomainTags.Contains("has-deprecated-api"))
                    record.DomainTags = [.. record.DomainTags, "has-deprecated-api"];

                // P6: architectural orientation. The first domain tag IS the file class
                // (both providers prepend it); entrypoint by Main symbol or conventional file name.
                record.ArchitecturalRole = record.DomainTags.Count > 0 ? record.DomainTags[0] : "generic";
                record.IsEntrypoint =
                    record.Symbols.Any(s => s.Name == "Main" && s.IsStatic) ||
                    record.FileName is "Program.cs" or "main.ts" or "main.tsx" or "index.ts" or "App.tsx";

                // P1 promotion: persist the framework-convention domain facts (routes / DI / EF /
                // TanStack cache graph / navigation) the provider already resolves from compiler data.
                try
                {
                    var domainFacts = await provider.ExtractDomainFactsAsync(filePath, ct);
                    var factRecords = domainFacts.Select((f, i) => new DomainFactRecord
                    {
                        Id           = $"{record.Id}::{f.Kind}::{i}",
                        FileId       = record.Id,
                        ProjectId    = record.ProjectId,
                        SubProjectId = string.IsNullOrEmpty(record.SubProjectId) ? null : record.SubProjectId,
                        Kind         = f.Kind,
                        Line         = f.Line,
                        Method       = f.Method,
                        Route        = f.Route,
                        Name         = f.Name,
                        TypeRef      = f.TypeRef,
                        OwnerType    = f.OwnerType,
                        Items        = f.Items,
                    }).ToList();
                    await _repository.UpsertDomainFactsForFileAsync(record.Id, factRecords, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Domain fact extraction failed for {File}; skipped", Path.GetFileName(filePath));
                }
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
        _summaryQueue.TryEnqueue(new SummaryJob(record.Id, filePath, record.RelativePath));

        // Enqueue reference analysis when public symbols changed (or on first index of this file).
        // Comparing SymbolsText is a cheap proxy: it changes iff the set/order of symbol names changed.
        if (record.SymbolsText != oldSymbolsText)
        {
            _referenceQueue.TryEnqueue(new ReferenceJob(
                record.Id, filePath, projectId, projectRoot, subProjectId));

            // P3: refresh per-symbol embeddings (the searchable API surface) when symbols changed.
            if (_embedding.IsAvailable)
                await EmbedSymbolsAsync(record, ct);
        }

        return record;
    }

    // P3: embed each public/exported/internal symbol over its deterministic NL descriptor, so semantic
    // search can localize to a symbol. Capped per file; best-effort (a failed embed skips that symbol).
    private async Task EmbedSymbolsAsync(CodeIndexRecord record, CancellationToken ct)
    {
        const int MaxSymbols = 80;
        var targets = record.Symbols
            .Where(s => s.Accessibility is "public" or "internal" or "exported")
            .Take(MaxSymbols)
            .ToList();

        if (targets.Count == 0)
        {
            await _repository.DeleteSymbolEmbeddingsForFileAsync(record.Id, ct);
            return;
        }

        var embeds = new List<SymbolEmbeddingRecord>(targets.Count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in targets)
        {
            ct.ThrowIfCancellationRequested();
            var text = BuildSymbolEmbedText(record, s);
            if (string.IsNullOrWhiteSpace(text)) continue;

            float[] vec;
            try { vec = await _embedding.GetEmbeddingAsync(text, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogDebug(ex, "Symbol embed failed for {Sym}", s.Name); continue; }

            // Unique within the file: include the containing type, then disambiguate any remaining
            // collision (same-name/arity overloads in one type) with a deterministic suffix — symbols
            // arrive in stable declaration order, so the suffix is stable across re-ingests.
            var baseId = $"{record.Id}::{s.ContainingTypeFullName ?? ""}::{s.Name}::{s.Parameters.Count}";
            var id = baseId;
            for (var dup = 1; !seenIds.Add(id); dup++) id = $"{baseId}#{dup}";

            embeds.Add(new SymbolEmbeddingRecord
            {
                Id             = id,
                ProjectId      = record.ProjectId,
                SubProjectId   = string.IsNullOrEmpty(record.SubProjectId) ? null : record.SubProjectId,
                FileId         = record.Id,
                RelativePath   = record.RelativePath,
                SymbolName     = s.Name,
                ContainingType = s.ContainingTypeFullName,
                Kind           = s.Kind,
                Line           = s.Line,
                EndLine        = s.EndLine,
                Vector         = vec,
                EmbedTextHash  = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))),
                ModelId        = "sbert-minilm-384",
                Dim            = vec.Length,
            });
        }

        await _repository.UpsertSymbolEmbeddingsForFileAsync(record.Id, embeds, ct);
    }

    private static string BuildSymbolEmbedText(CodeIndexRecord r, SymbolRecord s)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(s.ContainingTypeFullName)) sb.Append(s.ContainingTypeFullName).Append('.');
        sb.Append(s.Name).Append(' ').Append(s.Kind);
        if (!string.IsNullOrEmpty(s.NlDescription)) sb.Append(' ').Append(s.NlDescription);
        else if (!string.IsNullOrEmpty(s.Type))     sb.Append(" : ").Append(s.Type);
        if (!string.IsNullOrEmpty(s.DocSummary))     sb.Append(" — ").Append(s.DocSummary);
        var text = sb.ToString();
        return text.Length > 400 ? text[..400] : text;
    }

    internal static string BuildEmbedText(CodeIndexRecord r)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(r.FileName).Append(" [").Append(r.Language).AppendLine("]");
        sb.AppendLine(r.RelativePath);

        // Domain tags provide framework-level signal for semantic search
        if (r.DomainTags.Count > 0)
            sb.AppendLine(string.Join(" ", r.DomainTags));

        // LLM summary provides richer semantic signal; include it when available.
        // SummaryWorker re-embeds the record after the summary arrives.
        if (!string.IsNullOrWhiteSpace(r.LlmSummary))
        {
            var summary = r.LlmSummary.Length > 200 ? r.LlmSummary[..200] : r.LlmSummary;
            sb.AppendLine(summary);
        }

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
        return result.Length > 600 ? result[..600] : result;
    }

    // Declared §4.3 convention (not a compiler fact): recognises a test file by the test framework
    // it imports, falling back to a path/sub-project naming heuristic.
    private static (bool isTest, string? framework) DetectTestFramework(CodeIndexRecord r)
    {
        foreach (var imp in r.Imports)
        {
            if (imp.StartsWith("Xunit", StringComparison.Ordinal)) return (true, "xunit");
            if (imp.StartsWith("NUnit", StringComparison.Ordinal)) return (true, "nunit");
            if (imp.StartsWith("Microsoft.VisualStudio.TestTools", StringComparison.Ordinal)) return (true, "mstest");
            if (imp == "vitest") return (true, "vitest");
            if (imp.StartsWith("@jest", StringComparison.Ordinal) || imp == "jest") return (true, "jest");
        }

        var path = r.RelativePath.Replace('\\', '/');
        bool looksLikeTest =
            path.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/__tests__/", StringComparison.OrdinalIgnoreCase) ||
            r.FileName.Contains(".test.", StringComparison.OrdinalIgnoreCase) ||
            r.FileName.Contains(".spec.", StringComparison.OrdinalIgnoreCase) ||
            r.FileName.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase);
        return looksLikeTest ? (true, null) : (false, null);
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
