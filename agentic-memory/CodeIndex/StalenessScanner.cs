using AgenticMemory.Configuration;

namespace AgenticMemory.CodeIndex;

/// <summary>
/// Diffs the on-disk file tree against the stored CodeIndexRecords for a project.
/// New and modified files are enqueued for ingestion; deleted files are removed from the index.
/// </summary>
public sealed class StalenessScanner
{
    private readonly ICodeIndexRepository _repository;
    private readonly IIngestionQueue _queue;
    private readonly ISummaryQueue _summaryQueue;
    private readonly IReferenceQueue _referenceQueue;
    private readonly CodeIndexSettings _settings;
    private readonly ExcludedFolderMatcher _excluded;
    private readonly ILogger<StalenessScanner> _logger;

    public StalenessScanner(
        ICodeIndexRepository repository,
        IIngestionQueue queue,
        ISummaryQueue summaryQueue,
        IReferenceQueue referenceQueue,
        CodeIndexSettings settings,
        ILogger<StalenessScanner> logger)
    {
        _repository     = repository;
        _queue          = queue;
        _summaryQueue   = summaryQueue;
        _referenceQueue = referenceQueue;
        _settings       = settings;
        _excluded       = new ExcludedFolderMatcher(settings.ExcludePatterns);
        _logger         = logger;
    }

    /// <summary>
    /// Scans projectRoot, enqueues changed/new files, deletes removed files from the index.
    /// Returns (queued, alreadyCurrent).
    /// </summary>
    public async Task<(int queued, int current)> ScanAsync(
        string projectId, string projectRoot, CancellationToken ct = default)
    {
        _logger.LogInformation("Staleness scan: {ProjectId} at {Root}", projectId, projectRoot);

        await ExtractManifestsAsync(projectId, projectRoot, null, ct);

        var existingRecords = await _repository.GetByProjectAsync(projectId, ct);
        var existingByPath = existingRecords.ToDictionary(
            r => r.FilePath, StringComparer.OrdinalIgnoreCase);

        var diskFiles = EnumerateIndexableFiles(projectRoot).ToList();
        _logger.LogInformation("Found {Count} indexable files in {Root}", diskFiles.Count, projectRoot);

        var typesNowAvailable = TypeScript.TypeScriptLibResolver.HasResolvableTypes(projectRoot);
        int queued = 0, current = 0;

        foreach (var filePath in diskFiles)
        {
            ct.ThrowIfCancellationRequested();

            if (existingByPath.TryGetValue(filePath, out var record))
            {
                var diskModified = File.GetLastWriteTimeUtc(filePath);
                if (diskModified > record.FileModifiedAt.AddSeconds(1) || record.IsStale ||
                    (record.TypeScriptTypesResolved == false && typesNowAvailable))
                {
                    record.IsStale = true;
                    await _repository.UpsertAsync(record, ct);
                    _queue.TryEnqueue(new IngestionJob(filePath, projectId, projectRoot, Force: true));
                    queued++;
                }
                else
                {
                    // File is current; backfill LLM summary if it was never generated
                    if (string.IsNullOrWhiteSpace(record.LlmSummary) && !string.IsNullOrWhiteSpace(record.ExtractedContext))
                        _summaryQueue.TryEnqueue(new SummaryJob(record.Id, filePath, record.RelativePath));
                    current++;
                }
            }
            else
            {
                _queue.TryEnqueue(new IngestionJob(filePath, projectId, projectRoot, Force: false));
                queued++;
            }
        }

        // Remove index records for files no longer on disk
        var diskSet = new HashSet<string>(diskFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var record in existingRecords)
        {
            if (!diskSet.Contains(record.FilePath))
            {
                _logger.LogDebug("Removing deleted file from index: {File}", record.FileName);
                await _repository.DeleteByPathAsync(record.FilePath, ct);
                _referenceQueue.TryEnqueueDelete(record.Id);
            }
        }

        _logger.LogInformation("Scan complete: {Q} queued, {C} current", queued, current);
        return (queued, current);
    }

    /// <summary>
    /// Workspace-level scan: delegates to per-sub-project scans for correct diff scoping.
    /// Falls back to ScanAsync when no sub-projects are discovered.
    /// </summary>
    public async Task<(int queued, int current)> ScanWorkspaceAsync(
        string workspaceId,
        string workspaceRoot,
        IReadOnlyList<SubProjectRecord> subProjects,
        CancellationToken ct = default)
    {
        if (subProjects.Count == 0)
            return await ScanAsync(workspaceId, workspaceRoot, ct);

        // Capture manifests once for the whole workspace root (ManifestExtractor recurses into
        // every sub-project). Done here rather than per-sub-project so the project-scoped upsert
        // does not clobber sibling sub-projects' manifests.
        await ExtractManifestsAsync(workspaceId, workspaceRoot, null, ct);

        int totalQueued = 0, totalCurrent = 0;
        foreach (var sub in subProjects)
        {
            var (q, c) = await ScanSubProjectAsync(workspaceId, sub, ct);
            totalQueued  += q;
            totalCurrent += c;
        }
        return (totalQueued, totalCurrent);
    }

    /// <summary>
    /// Targeted reindex of a single sub-project by ID.
    /// </summary>
    public async Task<(int queued, int current)> ScanSubProjectByIdAsync(
        string workspaceId, string subProjectId,
        IReadOnlyList<SubProjectRecord> subProjects, CancellationToken ct = default)
    {
        var sub = subProjects.FirstOrDefault(s => s.Id == subProjectId)
            ?? throw new ArgumentException($"Sub-project {subProjectId} not found");
        return await ScanSubProjectAsync(workspaceId, sub, ct);
    }

    private async Task<(int queued, int current)> ScanSubProjectAsync(
        string workspaceId, SubProjectRecord sub, CancellationToken ct)
    {
        _logger.LogInformation("Scanning sub-project {Name} ({Type}) at {Root}",
            sub.Name, sub.Type, sub.RootPath);

        var existingRecords = await _repository.GetBySubProjectAsync(sub.Id, ct);
        var existingByPath  = existingRecords.ToDictionary(
            r => r.FilePath, StringComparer.OrdinalIgnoreCase);

        var diskFiles = EnumerateIndexableFiles(sub.RootPath).ToList();
        // If a TS sub-project was indexed type-less but node_modules is now present, re-index it with
        // full type resolution (auto-correct for the "indexed without node_modules" degraded state).
        var typesNowAvailable = TypeScript.TypeScriptLibResolver.HasResolvableTypes(sub.RootPath);
        int queued = 0, current = 0;

        foreach (var filePath in diskFiles)
        {
            ct.ThrowIfCancellationRequested();

            if (existingByPath.TryGetValue(filePath, out var record))
            {
                var diskModified = File.GetLastWriteTimeUtc(filePath);
                if (diskModified > record.FileModifiedAt.AddSeconds(1) || record.IsStale ||
                    (record.TypeScriptTypesResolved == false && typesNowAvailable))
                {
                    record.IsStale = true;
                    await _repository.UpsertAsync(record, ct);
                    _queue.TryEnqueue(new IngestionJob(
                        filePath, workspaceId, sub.RootPath, Force: true,
                        SubProjectId: sub.Id, SubProjectRoot: sub.RootPath));
                    queued++;
                }
                else
                {
                    // File is current; backfill LLM summary if it was never generated
                    if (string.IsNullOrWhiteSpace(record.LlmSummary) && !string.IsNullOrWhiteSpace(record.ExtractedContext))
                        _summaryQueue.TryEnqueue(new SummaryJob(record.Id, filePath, record.RelativePath));
                    current++;
                }
            }
            else
            {
                _queue.TryEnqueue(new IngestionJob(
                    filePath, workspaceId, sub.RootPath, Force: false,
                    SubProjectId: sub.Id, SubProjectRoot: sub.RootPath));
                queued++;
            }
        }

        var diskSet = new HashSet<string>(diskFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var record in existingRecords.Where(r => !diskSet.Contains(r.FilePath)))
        {
            _logger.LogDebug("Removing deleted file from index: {File}", record.FileName);
            await _repository.DeleteByPathAsync(record.FilePath, ct);
            _referenceQueue.TryEnqueueDelete(record.Id);
        }

        _logger.LogInformation("Sub-project scan done: {Q} queued, {C} current", queued, current);
        return (queued, current);
    }

    // Parse + persist the project's manifests (build/dependency graph). Best-effort.
    private async Task ExtractManifestsAsync(string projectId, string root, string? subProjectId, CancellationToken ct)
    {
        try
        {
            var manifests = ManifestExtractor.Extract(root, projectId, subProjectId, DateTime.UtcNow, _settings.ExcludePatterns);
            await _repository.UpsertProjectManifestsAsync(projectId, manifests, ct);
            _logger.LogInformation("Captured {Count} project manifest(s) for {ProjectId}", manifests.Count, projectId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manifest extraction failed for {ProjectId} at {Root}", projectId, root);
        }
    }

    public IReadOnlyList<string> GetIndexableFiles(string projectRoot) =>
        EnumerateIndexableFiles(projectRoot).ToList();

    private IEnumerable<string> EnumerateIndexableFiles(string root)
    {
        var extensions = new HashSet<string>(_settings.IndexedExtensions, StringComparer.OrdinalIgnoreCase);

        // IgnoreInaccessible skips unreadable directories (node_modules/.bin ACL, system dirs)
        // instead of throwing UnauthorizedAccessException mid-enumeration. RecurseSubdirectories
        // is set so we traverse the whole tree but only return the files we actually need.
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible    = true,
        };

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", opts); }
        catch (UnauthorizedAccessException) { yield break; }
        catch (DirectoryNotFoundException)  { yield break; }

        int count = 0;
        foreach (var file in files)
        {
            if (count >= _settings.MaxFilesPerProject) break;
            if (!extensions.Contains(Path.GetExtension(file))) continue;
            if (_excluded.IsExcluded(file, root)) continue;
            count++;
            yield return file;
        }
    }
}
