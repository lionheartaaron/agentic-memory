namespace AgenticMemory.CodeIndex;

/// <summary>
/// Builds and maintains the symbol reference graph on a dedicated low-priority thread.
///
/// WHEN IT RUNS: Reference analysis only starts when ingestion is fully idle. Running it while
/// ingestion is active would produce incomplete/stale graphs (files not yet indexed can't appear
/// in the UsedBy lists). The worker polls IsIngestionIdle and sleeps until the queue is drained.
///
/// HOW IT'S FAST: CSharpRoslynProvider.FindAllReferencesAsync uses the pre-built inverted index
/// in ProjectIndex — an O(symbolNames.Count) dictionary lookup rather than an O(files × identifiers)
/// AST traversal. The index is built once during RegisterProjectAsync and updated incrementally
/// whenever a file is re-parsed (GetOrParseSyntaxTree).
///
/// WHEN A FILE CHANGES: FileIngestionService enqueues a ReferenceJob only when SymbolsText
/// changes (public API surface changed). ProjectIndex.GetOrParseSyntaxTree has already updated
/// the reference index for that file's outgoing references. The reference worker's job is to
/// write the per-symbol UsedBy/DependsOn metadata to the DB so the dashboard can show it.
/// </summary>
public sealed class ReferenceIndexWorker : DedicatedWorker<ReferenceJob>, IReferenceQueue
{
    private readonly ICodeIndexRepository _repository;
    private readonly CodeIndexService     _codeIndex;
    private readonly WorkerStatusTracker  _statusTracker;
    private readonly ILogger<ReferenceIndexWorker> _logger;

    // Tracks file IDs in the channel to skip duplicate enqueues.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _queued
        = new(StringComparer.Ordinal);

    protected override string WorkerName => "ReferenceIndexWorker";

    public ReferenceIndexWorker(
        ICodeIndexRepository repository,
        CodeIndexService     codeIndex,
        WorkerStatusTracker  statusTracker,
        ILogger<ReferenceIndexWorker> logger)
    {
        _repository    = repository;
        _codeIndex     = codeIndex;
        _statusTracker = statusTracker;
        _logger        = logger;
    }

    // ── IReferenceQueue ───────────────────────────────────────────────────────

    public bool TryEnqueue(ReferenceJob job)
    {
        // Non-delete jobs are deduplicated: if the file is already queued, the existing job
        // is equivalent (symbols haven't changed again yet) so we can skip re-enqueuing.
        if (!job.IsDelete && !_queued.TryAdd(job.FileId, 0))
            return true;

        if (!TryWrite(job))
        {
            _queued.TryRemove(job.FileId, out _);
            return false;
        }
        _statusTracker.SetReferenceQueueDepth(QueueDepth);
        return true;
    }

    public bool TryEnqueueDelete(string fileId)
        => TryEnqueue(new ReferenceJob(fileId, "", "", IsDelete: true));

    public int Depth => QueueDepth;

    public void Clear()
    {
        DrainQueue();
        _queued.Clear();
        _statusTracker.SetReferenceQueueDepth(0);
        _statusTracker.SetReferenceProcessing(false);
    }

    // ── Worker hooks ──────────────────────────────────────────────────────────

    protected override void OnWorkerStarted() =>
        _logger.LogInformation("ReferenceIndexWorker started");

    protected override void OnWorkerStopped() =>
        _logger.LogInformation("ReferenceIndexWorker stopped");

    protected override void OnDrained(ReferenceJob job) =>
        _queued.TryRemove(job.FileId, out _);

    protected override void OnBeforeJob(ReferenceJob job, int remaining)
    {
        _queued.TryRemove(job.FileId, out _);
        _statusTracker.SetReferenceQueueDepth(remaining);
    }

    protected override void OnAfterJob(ReferenceJob job) =>
        _statusTracker.SetReferenceProcessing(false);

    protected override void OnJobError(ReferenceJob job, Exception ex) =>
        _logger.LogWarning(ex, "Reference analysis failed for {FileId}", job.FileId);

    // ── Core job execution (runs on dedicated thread) ─────────────────────────

    protected override void Execute(ReferenceJob job, CancellationToken ct)
    {
        // Wait until ingestion is fully idle before doing reference analysis.
        // Reference graphs built while files are still being indexed are incomplete —
        // files not yet ingested can't appear in UsedBy lists.
        // ManualResetEventSlim.Wait suspends this OS thread without spinning and without
        // holding any lock — the Kestrel thread pool is completely unaffected.
        _statusTracker.IngestionIdleEvent.Wait(ct);

        ct.ThrowIfCancellationRequested();

        // Per-job timeout: 45 s is generous for even large files.
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        jobCts.CancelAfter(TimeSpan.FromSeconds(45));

        try
        {
            if (job.IsDelete)
                ProcessDelete(job.FileId, jobCts.Token);
            else
                ProcessFile(job, jobCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Reference analysis timed out (45 s) for {FileId}", job.FileId);
        }
    }

    // ── Delete processing ─────────────────────────────────────────────────────

    private void ProcessDelete(string fileId, CancellationToken ct)
    {
        _statusTracker.SetReferenceProcessing(true, $"[delete] {fileId[..Math.Min(8, fileId.Length)]}");

        _repository.DeleteSymbolReferencesForFileAsync(fileId, ct).GetAwaiter().GetResult();
        _repository.DeleteDomainFactsForFileAsync(fileId, ct).GetAwaiter().GetResult();
        _repository.DeleteSymbolEmbeddingsForFileAsync(fileId, ct).GetAwaiter().GetResult();

        var affectedRefs = _repository.GetUsedByFileAsync(fileId, ct).GetAwaiter().GetResult();
        foreach (var symRef in affectedRefs)
        {
            ct.ThrowIfCancellationRequested();

            var dependingRecord =
                _repository.GetByPathAsync(symRef.DefinedInFileId, ct).GetAwaiter().GetResult()
                ?? _repository.GetByIdsAsync([symRef.DefinedInFileId], ct).GetAwaiter().GetResult().FirstOrDefault();

            if (dependingRecord is null) continue;

            var remaining = _repository.GetUsedByFileAsync(dependingRecord.Id, ct).GetAwaiter().GetResult();
            dependingRecord.DependsOnFileIds = remaining
                .Select(r => r.DefinedInFileId)
                .Distinct()
                .Where(id => id != dependingRecord.Id)
                .ToList();
            dependingRecord.FanOut = dependingRecord.DependsOnFileIds.Count;
            _repository.UpsertAsync(dependingRecord, ct).GetAwaiter().GetResult();
        }
    }

    // ── File reference processing ─────────────────────────────────────────────

    private void ProcessFile(ReferenceJob job, CancellationToken ct)
    {
        var record = _repository.GetByPathAsync(job.FilePath, ct).GetAwaiter().GetResult();
        if (record is null)
        {
            _logger.LogDebug("ReferenceJob skipped — record not found: {FileId}", job.FileId);
            return;
        }

        // One record per distinct public symbol NAME. The reference index is name-keyed and tracks
        // references to the first declaration of each name (SymbolEqualityComparer against
        // declaredSymbols[name]), so a second same-named symbol has no distinct usage data — and the
        // record id {fileId}::{name} would otherwise overwrite it (last-wins). Taking the first
        // declaration (record.Symbols is in declaration order) matches the index's own semantics.
        var publicSymbols = record.Symbols
            .Where(s => s.Accessibility is "public" or "internal" or "exported")
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        if (publicSymbols.Count == 0)
        {
            _logger.LogDebug("ReferenceJob skipped — no public symbols: {File}", record.RelativePath);
            return;
        }

        _statusTracker.SetReferenceProcessing(true, record.RelativePath);
        _logger.LogDebug("Analyzing references for {File} ({Count} public symbols)",
            record.RelativePath, publicSymbols.Count);

        // Query the pre-built reference index — O(symbolNames.Count) dictionary lookups.
        // ProjectIndex already keeps this current via incremental updates in GetOrParseSyntaxTree.
        // A constructor is reached by instantiating its TYPE (`new Foo()` resolves the identifier
        // "Foo" to the type, never to a "Foo()" method), so we look it up under the type's simple name.
        var symbolNames = publicSymbols.Select(LookupName).Distinct().ToList();
        var allRefs     = _codeIndex.FindAllReferencesAsync(record.FilePath, symbolNames, ct)
                                    .GetAwaiter().GetResult();

        // Batch-load all referenced file records in one DB round-trip.
        var allRefIds = allRefs.Values
            .SelectMany(refs => refs.Select(r => LiteDbCodeIndexRepository.ComputeId(r.FilePath)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var refRecordList = _repository.GetByIdsAsync(allRefIds, ct).GetAwaiter().GetResult();
        // Key by Id (content-hash of the lowercased canonical path) so the lookup is
        // slash-agnostic: ComputeId normalises both '/' and '\' via Path.GetFullPath,
        // while the raw FilePath on Windows uses backslashes but the TS provider
        // emits forward-slash paths from its LanguageServiceHost — the two never matched.
        var idToRecord = refRecordList.ToDictionary(r => r.Id, StringComparer.Ordinal);

        // Write one SymbolReferenceRecord per public symbol.
        // Collect all consumer file IDs across symbols for the back-propagation step below.
        var allConsumerIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sym in publicSymbols)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var bucket = allRefs.TryGetValue(LookupName(sym), out var r) ? r : [];
                var isCtor = sym.Kind == "constructor";

                // Attribute each site to the EXACT symbol it resolved to (TargetDocId), so overloads
                // and same-named members on other types don't steal each other's references. A null
                // DocId on either side (e.g. TypeScript) means "match by name" — the legacy behaviour.
                // A constructor instead counts ALL references to its type as reachability evidence
                // (direct `new`, a `typeof`, or a DI generic arg all keep the constructor alive).
                var reachableRefs = isCtor
                    ? bucket
                    : bucket.Where(x => sym.SymbolDocId is null || x.TargetDocId is null
                                        || x.TargetDocId == sym.SymbolDocId).ToList();

                // The displayed/graph usage sites are the cross-file ones (self-edges are noise). For a
                // constructor those are the instantiation sites — "who constructs this".
                var displayRefs = isCtor ? reachableRefs.Where(x => x.Role == "new") : reachableRefs;

                // Keep the (site, referencing-record) pairs so we can roll up test-file usage.
                var sitePairs = displayRefs
                    .Select(refInfo => (refInfo, refRec: idToRecord.GetValueOrDefault(
                        LiteDbCodeIndexRepository.ComputeId(refInfo.FilePath))))
                    .Where(x => x.refRec is not null && x.refRec.Id != record.Id)
                    .ToList();

                var usageSites = sitePairs
                    .Select(x => new SymbolUsageSite
                    {
                        FileId            = x.refRec!.Id,
                        RelativePath      = x.refRec.RelativePath,
                        Line              = x.refInfo.Line,
                        Context           = TruncateContext(x.refInfo.Context ?? ""),
                        Role              = x.refInfo.Role,
                        EnclosingSymbolId = x.refInfo.EnclosingSymbolId,
                        EnclosingName     = x.refInfo.EnclosingName,
                    })
                    .ToList();

                foreach (var site in usageSites) allConsumerIds.Add(site.FileId);

                // P1 near-free rollups over UsedBy (dead-code + test linkage).
                var testedBy = sitePairs
                    .Where(x => x.refRec!.IsTestFile)
                    .Select(x => x.refRec!.Id)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                _repository.UpsertSymbolReferenceAsync(new SymbolReferenceRecord
                {
                    Id                    = $"{record.Id}::{sym.Name}",
                    SymbolName            = sym.Name,
                    SymbolKind            = sym.Kind,
                    Accessibility         = sym.Accessibility,
                    DefinedInFileId       = record.Id,
                    DefinedInRelativePath = record.RelativePath,
                    DefinedAtLine         = sym.Line,
                    ProjectId             = record.ProjectId,
                    SubProjectId          = string.IsNullOrEmpty(record.SubProjectId) ? null : record.SubProjectId,
                    UsedBy                = usageSites,
                    ExternalUseCount      = usageSites.Count,
                    TestedByFileIds       = testedBy,
                    UpdatedAt             = DateTime.UtcNow,
                }, ct).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reference write failed for {Symbol} in {File}", sym.Name, record.RelativePath);
            }
        }

        // Derive fan-in / fan-out from what we just wrote.
        var mySymRefs    = _repository.GetDefinedInFileAsync(record.Id, ct).GetAwaiter().GetResult();
        var usedByIds    = mySymRefs.SelectMany(r => r.UsedBy.Select(u => u.FileId)).Distinct().ToList();
        var dependsOnRefs = _repository.GetUsedByFileAsync(record.Id, ct).GetAwaiter().GetResult();
        var dependsOnIds  = dependsOnRefs.Select(r => r.DefinedInFileId).Distinct()
                                         .Where(id => id != record.Id).ToList();

        // Reload record to avoid clobbering concurrent ingestion writes.
        var fresh = _repository.GetByPathAsync(job.FilePath, ct).GetAwaiter().GetResult() ?? record;
        fresh.UsedByFileIds    = usedByIds;
        fresh.DependsOnFileIds = dependsOnIds;
        fresh.FanIn            = usedByIds.Count;
        fresh.FanOut           = dependsOnIds.Count;
        // For a test file, the production files it references are its test subjects.
        if (fresh.IsTestFile) fresh.TestSubjectFileIds = dependsOnIds;
        _repository.UpsertAsync(fresh, ct).GetAwaiter().GetResult();

        _logger.LogDebug("Reference analysis done: {File} fanIn={FanIn} fanOut={FanOut}",
            record.RelativePath, fresh.FanIn, fresh.FanOut);

        // Back-propagate DependsOnFileIds into consumer files that were processed earlier.
        // Because reference jobs run in ingestion order (alphabetical), a consumer like
        // api.ts is analyzed before its definition file types.ts, so api.ts.DependsOnFileIds
        // is set to [] at that point.  Now that we know types.ts is used by api.ts, update
        // api.ts so it reflects the dependency.
        foreach (var consumerId in allConsumerIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var consumer = idToRecord.GetValueOrDefault(consumerId)
                    ?? _repository.GetByIdsAsync([consumerId], ct).GetAwaiter().GetResult().FirstOrDefault();
                if (consumer is null || consumer.DependsOnFileIds.Contains(record.Id)) continue;

                // Reload to avoid clobbering in-progress changes
                var freshConsumer = _repository.GetByPathAsync(consumer.FilePath, ct).GetAwaiter().GetResult()
                    ?? consumer;
                if (freshConsumer.DependsOnFileIds.Contains(record.Id)) continue;

                freshConsumer.DependsOnFileIds = [.. freshConsumer.DependsOnFileIds, record.Id];
                freshConsumer.FanOut = freshConsumer.DependsOnFileIds.Distinct().Count();
                freshConsumer.DependsOnFileIds = freshConsumer.DependsOnFileIds.Distinct().ToList();
                _repository.UpsertAsync(freshConsumer, ct).GetAwaiter().GetResult();

                _logger.LogDebug("Updated DependsOn for consumer {File}: added {Def}",
                    freshConsumer.RelativePath, record.RelativePath);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Consumer DependsOn update failed for {FileId}", consumerId);
            }
        }
    }

    private static string TruncateContext(string context)
    {
        const int Max = 120;
        return context.Length <= Max ? context : context[..Max];
    }

    // The reference index is keyed by the identifier text used at the call site. A constructor is
    // recorded by GetSymbolsAsync as "Foo()" (CSharpRoslynProvider.GetDeclNameAndKind), but callers
    // write `new Foo()` whose identifier is the bare type name "Foo" — so we resolve constructor
    // references under the type's simple name.
    private static string LookupName(SymbolRecord s) =>
        s.Kind == "constructor" && s.Name.EndsWith("()", StringComparison.Ordinal)
            ? s.Name[..^2]
            : s.Name;
}
