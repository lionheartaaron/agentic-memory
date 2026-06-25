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
    private readonly CodeIndexSettings _settings;
    private readonly ILogger<StalenessScanner> _logger;

    public StalenessScanner(
        ICodeIndexRepository repository,
        IIngestionQueue queue,
        CodeIndexSettings settings,
        ILogger<StalenessScanner> logger)
    {
        _repository = repository;
        _queue = queue;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Scans projectRoot, enqueues changed/new files, deletes removed files from the index.
    /// Returns (queued, alreadyCurrent).
    /// </summary>
    public async Task<(int queued, int current)> ScanAsync(
        string projectId, string projectRoot, CancellationToken ct = default)
    {
        _logger.LogInformation("Staleness scan: {ProjectId} at {Root}", projectId, projectRoot);

        var existingRecords = await _repository.GetByProjectAsync(projectId, ct);
        var existingByPath = existingRecords.ToDictionary(
            r => r.FilePath, StringComparer.OrdinalIgnoreCase);

        var diskFiles = EnumerateIndexableFiles(projectRoot).ToList();
        _logger.LogInformation("Found {Count} indexable files in {Root}", diskFiles.Count, projectRoot);

        int queued = 0, current = 0;

        foreach (var filePath in diskFiles)
        {
            ct.ThrowIfCancellationRequested();

            if (existingByPath.TryGetValue(filePath, out var record))
            {
                var diskModified = File.GetLastWriteTimeUtc(filePath);
                if (diskModified > record.FileModifiedAt.AddSeconds(1) || record.IsStale)
                {
                    record.IsStale = true;
                    await _repository.UpsertAsync(record, ct);
                    _queue.TryEnqueue(new IngestionJob(filePath, projectId, projectRoot, Force: true));
                    queued++;
                }
                else
                {
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
            }
        }

        _logger.LogInformation("Scan complete: {Q} queued, {C} current", queued, current);
        return (queued, current);
    }

    public IReadOnlyList<string> GetIndexableFiles(string projectRoot) =>
        EnumerateIndexableFiles(projectRoot).ToList();

    private IEnumerable<string> EnumerateIndexableFiles(string root)
    {
        var extensions = new HashSet<string>(_settings.IndexedExtensions, StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
        catch (UnauthorizedAccessException) { yield break; }

        int count = 0;
        foreach (var file in files)
        {
            if (count >= _settings.MaxFilesPerProject) break;
            if (!extensions.Contains(Path.GetExtension(file))) continue;
            if (IsExcluded(file, root)) continue;
            count++;
            yield return file;
        }
    }

    private bool IsExcluded(string filePath, string root)
    {
        var relative = Path.GetRelativePath(root, filePath);
        foreach (var pattern in _settings.ExcludePatterns)
        {
            var keyPart = pattern.Replace("**/", "").Replace("/**", "").Replace("**", "");
            if (relative.Contains(keyPart, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
