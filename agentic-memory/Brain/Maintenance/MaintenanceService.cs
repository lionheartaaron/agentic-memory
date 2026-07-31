using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Search;
using AgenticMemory.Brain.Storage;
using AgenticMemory.Configuration;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Brain.Maintenance;

/// <summary>
/// Background upkeep, consolidation and reindexing.
///
/// The defining change from the previous implementation: <b>nothing here deletes a memory</b>.
/// Decay used to call a prune that issued <c>Delete</c> against anything whose exponentially
/// decayed strength fell under a threshold — roughly 23 to 46 days after its last retrieval,
/// with the only escape being a pin flag that no production code path could set. Strength is now
/// purely a ranking signal, and ageing moves a memory to cold storage rather than destroying it.
/// </summary>
public sealed class MaintenanceService : IMaintenanceService
{
    private readonly IMemoryRepository _repository;
    private readonly IMemoryAdminStore _adminStore;
    private readonly IEmbeddingService? _embeddingService;
    private readonly IMemoryBackupService? _backups;
    private readonly MaintenanceSettings _settings;
    private readonly ILogger<MaintenanceService>? _logger;

    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private string? _currentOperation;
    private DateTime? _lastUpkeepRun, _lastConsolidationRun, _lastReindexRun, _lastCompactRun, _lastPurgeRun;

    public MaintenanceService(
        IMemoryRepository repository,
        IMemoryAdminStore adminStore,
        IEmbeddingService? embeddingService = null,
        MaintenanceSettings? settings = null,
        IMemoryBackupService? backups = null,
        ILogger<MaintenanceService>? logger = null)
    {
        _repository       = repository;
        _adminStore       = adminStore;
        _embeddingService = embeddingService;
        _backups          = backups;
        _settings         = settings ?? new MaintenanceSettings();
        _logger           = logger;
    }

    /// <summary>Compatibility constructor for callers that predate the backup service.</summary>
    public MaintenanceService(
        IMemoryRepository repository,
        IMemoryAdminStore adminStore,
        IEmbeddingService? embeddingService,
        MaintenanceSettings? settings,
        ILogger<MaintenanceService>? logger)
        : this(repository, adminStore, embeddingService, settings, null, logger) { }

    public bool IsRunning => _operationLock.CurrentCount == 0;

    public MaintenanceStatus GetStatus() => new()
    {
        LastUpkeepRun        = _lastUpkeepRun,
        LastConsolidationRun = _lastConsolidationRun,
        LastReindexRun       = _lastReindexRun,
        LastCompactRun       = _lastCompactRun,
        LastPurgeRun         = _lastPurgeRun,
        IsRunning            = IsRunning,
        CurrentOperation     = _currentOperation,
    };

    private async Task<T?> WithLockAsync<T>(string operation, Func<Task<T>> work) where T : class
    {
        if (!await _operationLock.WaitAsync(0))
            return null;

        try
        {
            _currentOperation = operation;
            return await work();
        }
        finally
        {
            _currentOperation = null;
            _operationLock.Release();
        }
    }

    // ── Upkeep ────────────────────────────────────────────────────────────────────────────────

    public async Task<UpkeepResult> RunUpkeepAsync(
        MaintenanceSettings? settings = null, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var config = settings ?? _settings;

        var result = await WithLockAsync("Upkeep", async () =>
        {
            try
            {
                var now         = DateTime.UtcNow;
                var coldCutoff  = now.AddDays(-Math.Max(1, config.ArchiveEpisodicAfterDays));
                var batch       = new MemoryWriteBatch();
                var processed   = 0;
                var expired     = 0;
                var archived    = 0;
                var strengthSum = 0.0;
                var strengthN   = 0;

                foreach (var memory in _adminStore.StreamAll(includeNonCurrent: false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    processed++;

                    strengthSum += memory.GetCurrentStrength();
                    strengthN++;

                    // Ephemeral context that has passed its expiry.
                    if (memory.ExpiresAt is { } expiresAt && now > expiresAt)
                    {
                        batch.ChangeState(memory.Id, MemoryState.Archived, detail: "expired");
                        expired++;
                        continue;
                    }

                    // Only episodic and ephemeral memories age. Facts, preferences, persona and
                    // affect are kept indefinitely regardless of how long since they were recalled.
                    if (!memory.Type.IsAgeable()) continue;
                    if (memory.IsPinned) continue;
                    if (memory.LastAccessedAt > coldCutoff) continue;

                    batch.ChangeState(memory.Id, MemoryState.Archived, detail: "aged to cold storage");
                    archived++;
                }

                if (!batch.IsEmpty)
                    await _repository.ExecuteAsync(batch, "maintenance:upkeep", cancellationToken);

                _lastUpkeepRun = DateTime.UtcNow;

                _logger?.LogInformation(
                    "Upkeep complete. Processed {Processed}, expired {Expired}, archived to cold {Archived}. No memories were deleted.",
                    processed, expired, archived);

                return new UpkeepResult
                {
                    StartedAt         = startedAt,
                    CompletedAt       = DateTime.UtcNow,
                    MemoriesProcessed = processed,
                    Expired           = expired,
                    ArchivedToCold    = archived,
                    AverageStrength   = strengthN > 0 ? strengthSum / strengthN : 0,
                    Success           = true,
                };
            }
            catch (OperationCanceledException)
            {
                return Failed<UpkeepResult>(startedAt, "Operation was cancelled");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during upkeep");
                return Failed<UpkeepResult>(startedAt, ex.Message);
            }
        });

        return result ?? Busy<UpkeepResult>(startedAt, _currentOperation);
    }

    // ── Consolidation ─────────────────────────────────────────────────────────────────────────

    public async Task<ConsolidationResult> ConsolidateMemoriesAsync(
        double similarityThreshold = 0.9, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;

        var result = await WithLockAsync("Consolidation", async () =>
        {
            try
            {
                // Merging is reversible in principle — the losers become Merged, not deleted — but it
                // rewrites the survivor and restates a whole cluster's history in one batch. Cheap to
                // guard, expensive to reconstruct by hand.
                var snapshot = await Snapshot("consolidation", cancellationToken);

                var memories = _adminStore.StreamAll(includeNonCurrent: false).ToList();
                var comparisons = 0;
                var clusters = new List<List<MemoryNodeEntity>>();
                var claimed = new HashSet<Guid>();

                // Blocking. Comparing every pair was O(N^2): at ten thousand memories that is fifty
                // million similarity computations, each a trigram set operation plus a 384-dimension
                // cosine, on a background timer. Candidates are instead restricted to memories that
                // share an exact compatibility key or land in the same random-projection bucket.
                foreach (var block in BuildBlocks(memories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (block.Count < 2) continue;

                    foreach (var seed in block)
                    {
                        if (claimed.Contains(seed.Id)) continue;

                        var cluster = new List<MemoryNodeEntity> { seed };
                        claimed.Add(seed.Id);

                        foreach (var other in block)
                        {
                            if (claimed.Contains(other.Id)) continue;
                            if (!AreMergeCompatible(seed, other)) continue;

                            comparisons++;
                            if (Similarity(seed, other) < similarityThreshold) continue;

                            cluster.Add(other);
                            claimed.Add(other.Id);
                        }

                        if (cluster.Count > 1) clusters.Add(cluster);
                    }
                }

                var batch = new MemoryWriteBatch();
                var archived = 0;

                foreach (var cluster in clusters)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var survivor = SelectSurvivor(cluster);

                    foreach (var duplicate in cluster.Where(m => m.Id != survivor.Id))
                    {
                        batch.ChangeState(duplicate.Id, MemoryState.Merged, survivor.Id, detail: "consolidated");
                        archived++;

                        if (!survivor.LinkedNodeIds.Contains(duplicate.Id))
                            survivor.LinkedNodeIds.Add(duplicate.Id);
                    }

                    batch.Upsert(survivor);
                }

                if (!batch.IsEmpty)
                    await _repository.ExecuteAsync(batch, "maintenance:consolidation", cancellationToken);

                _lastConsolidationRun = DateTime.UtcNow;

                _logger?.LogInformation(
                    "Consolidation complete. Analyzed {Analyzed} via {Comparisons} comparisons, {Clusters} clusters, {Archived} merged",
                    memories.Count, comparisons, clusters.Count, archived);

                return new ConsolidationResult
                {
                    StartedAt            = startedAt,
                    CompletedAt          = DateTime.UtcNow,
                    MemoriesAnalyzed     = memories.Count,
                    MemoriesMerged       = clusters.Count,
                    MemoriesArchived     = archived,
                    ClustersFound        = clusters.Count,
                    ComparisonsPerformed = comparisons,
                    SnapshotPath         = snapshot?.Path,
                    Success              = true,
                };
            }
            catch (OperationCanceledException)
            {
                return Failed<ConsolidationResult>(startedAt, "Operation was cancelled");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during consolidation");
                return Failed<ConsolidationResult>(startedAt, ex.Message);
            }
        });

        return result ?? Busy<ConsolidationResult>(startedAt, _currentOperation);
    }

    /// <summary>
    /// Consolidation must never merge across a privacy or subject boundary. Two memories are only
    /// comparable when they belong to the same user, describe the same subject, assert the same
    /// slot, and are visible to exactly the same set of companions.
    /// </summary>
    private static bool AreMergeCompatible(MemoryNodeEntity a, MemoryNodeEntity b)
    {
        if (!string.Equals(a.UserId, b.UserId, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.SubjectRef, b.SubjectRef, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(a.Predicate, b.Predicate, StringComparison.OrdinalIgnoreCase)) return false;
        if (a.Visibility != b.Visibility) return false;
        if (a.Type == MemoryType.Persona || b.Type == MemoryType.Persona) return false;

        return a.CompanionIds.ToHashSet(StringComparer.Ordinal).SetEquals(b.CompanionIds);
    }

    /// <summary>
    /// Keeps the most trustworthy and most current memory — never the "strongest".
    ///
    /// Selecting by accumulated strength meant an old, frequently-recalled but outdated fact beat
    /// the newer correct one, which actively manufactured stale answers. Correctness ordering for
    /// facts is provenance, then recency, then detail.
    /// </summary>
    private static MemoryNodeEntity SelectSurvivor(List<MemoryNodeEntity> cluster) =>
        cluster
            .OrderByDescending(m => m.IsPinned)
            .ThenByDescending(m => m.Source.TrustRank())
            .ThenByDescending(m => m.Confidence)
            .ThenByDescending(m => m.EventTime ?? m.ValidFrom)
            .ThenByDescending(m => m.Content.Length)
            .First();

    /// <summary>
    /// Groups memories into candidate blocks: an exact key for slotted memories, plus
    /// random-projection LSH buckets so that unslotted near-duplicates still meet.
    /// </summary>
    private static IEnumerable<List<MemoryNodeEntity>> BuildBlocks(List<MemoryNodeEntity> memories)
    {
        var exact = memories
            .Where(m => m.Predicate is not null)
            .GroupBy(m => $"{m.UserId}|{m.SubjectRef}|{m.Predicate}", StringComparer.Ordinal);

        foreach (var g in exact)
            yield return g.ToList();

        var unslotted = memories.Where(m => m.Predicate is null).ToList();
        if (unslotted.Count == 0) yield break;

        var dimensions = unslotted.FirstOrDefault(m => m.EmbeddingDim > 0)?.EmbeddingDim ?? 0;

        if (dimensions > 0)
        {
            var planes = RandomProjections(dimensions);

            foreach (var g in unslotted
                         .Where(m => m.EmbeddingDim == dimensions)
                         .GroupBy(m => Signature(m, planes)))
                yield return g.ToList();
        }

        // Anything without a usable vector still gets exact-text duplicate detection.
        foreach (var g in unslotted
                     .Where(m => m.EmbeddingDim != dimensions || dimensions == 0)
                     .GroupBy(m => $"{m.UserId}|{m.ContentNormalized}", StringComparer.Ordinal))
            yield return g.ToList();
    }

    private const int SignatureBits = 16;

    /// <summary>Deterministic hyperplanes, so blocking is stable between runs.</summary>
    private static float[][] RandomProjections(int dimensions)
    {
        var rng = new Random(20260730);
        var planes = new float[SignatureBits][];

        for (var i = 0; i < SignatureBits; i++)
        {
            var plane = new float[dimensions];
            for (var d = 0; d < dimensions; d++)
                plane[d] = (float)(rng.NextDouble() * 2 - 1);
            planes[i] = plane;
        }

        return planes;
    }

    private static int Signature(MemoryNodeEntity memory, float[][] planes)
    {
        var vector = memory.GetEmbedding();
        if (vector is null) return 0;

        var signature = 0;
        for (var i = 0; i < planes.Length; i++)
        {
            var dot = 0f;
            var plane = planes[i];
            for (var d = 0; d < vector.Length && d < plane.Length; d++)
                dot += vector[d] * plane[d];

            if (dot > 0) signature |= 1 << i;
        }

        return signature;
    }

    private double Similarity(MemoryNodeEntity a, MemoryNodeEntity b)
    {
        // Consistent scale throughout: raw cosine. Conflict handling and consolidation previously
        // used different mappings of the same measure against unrelated thresholds.
        var va = a.GetEmbedding();
        var vb = b.GetEmbedding();

        if (va is not null && vb is not null && VectorMath.TryCosineSimilarity(va, vb, out var cosine))
            return cosine;

        return TrigramFuzzyMatcher.CalculateSimilarity(a.ContentNormalized, b.ContentNormalized);
    }

    // ── Reindex ───────────────────────────────────────────────────────────────────────────────

    public async Task<ReindexResult> ReindexAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;

        var result = await WithLockAsync("Reindex", async () =>
        {
            try
            {
                var reindexed = 0;
                var generated = 0;
                var stale = 0;

                var available  = _embeddingService?.IsAvailable == true;
                var stamp      = available ? MemoryTextIndexer.BuildEmbeddingStamp(_embeddingService!.ModelId) : null;
                var dimensions = available ? _embeddingService!.Dimensions : 0;

                var pending = new MemoryWriteBatch();

                foreach (var memory in _adminStore.StreamAll())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (available)
                    {
                        var comparable = memory.HasComparableEmbedding(stamp, dimensions);
                        if (!comparable) stale++;

                        if (force || !comparable)
                        {
                            try
                            {
                                // One canonical recipe, shared with the store path. Previously the
                                // reindex embedded lowercased text while stores embedded original
                                // case, so a memory's vector depended on which path last ran.
                                var text = MemoryTextIndexer.BuildEmbeddingText(memory);
                                var vector = await _embeddingService!.GetEmbeddingAsync(text, cancellationToken);
                                memory.SetEmbedding(vector, stamp);
                                generated++;
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "Failed to embed memory {Id} during reindex", memory.Id);
                            }
                        }
                    }

                    // Derived text fields are recomputed by the save path itself, so they can no
                    // longer be calculated here and then silently overwritten on the way to disk.
                    pending.Upsert(memory);
                    reindexed++;

                    if (pending.Upserts.Count >= 200)
                    {
                        await _repository.ExecuteAsync(pending, "maintenance:reindex", cancellationToken);
                        pending = new MemoryWriteBatch();
                    }
                }

                if (!pending.IsEmpty)
                    await _repository.ExecuteAsync(pending, "maintenance:reindex", cancellationToken);

                _lastReindexRun = DateTime.UtcNow;

                _logger?.LogInformation(
                    "Reindex complete. {Reindexed} memories, {Generated} embeddings generated, {Stale} stale vectors replaced",
                    reindexed, generated, stale);

                return new ReindexResult
                {
                    StartedAt               = startedAt,
                    CompletedAt             = DateTime.UtcNow,
                    MemoriesReindexed       = reindexed,
                    EmbeddingsGenerated     = generated,
                    StaleEmbeddingsReplaced = stale,
                    Success                 = true,
                };
            }
            catch (OperationCanceledException)
            {
                return Failed<ReindexResult>(startedAt, "Operation was cancelled");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during reindex");
                return Failed<ReindexResult>(startedAt, ex.Message);
            }
        });

        return result ?? Busy<ReindexResult>(startedAt, _currentOperation);
    }

    // ── Purge and compact ─────────────────────────────────────────────────────────────────────

    public async Task<PurgeResult> PurgeForgottenAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;

        var result = await WithLockAsync("Purge", async () =>
        {
            try
            {
                // The one path that physically removes memories. Snapshot first: everything else in
                // this service is reversible, and this is not.
                var snapshot = await Snapshot("purge", cancellationToken);

                var purged = await _adminStore.PurgeForgottenAsync(retention, "maintenance:purge", cancellationToken);
                _lastPurgeRun = DateTime.UtcNow;

                return new PurgeResult
                {
                    StartedAt = startedAt, CompletedAt = DateTime.UtcNow,
                    MemoriesPurged = purged, SnapshotPath = snapshot?.Path, Success = true,
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during purge");
                return Failed<PurgeResult>(startedAt, ex.Message);
            }
        });

        return result ?? Busy<PurgeResult>(startedAt, _currentOperation);
    }

    public async Task<CompactResult> CompactDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;

        var result = await WithLockAsync("Compact", async () =>
        {
            try
            {
                // A rebuild reconstructs every page of the datafile in place. If it fails partway
                // there is nothing to fall back to but a copy taken beforehand.
                var snapshot = await Snapshot("compact", cancellationToken);

                var sizeBefore = _adminStore.GetDatabaseSizeBytes();
                await _adminStore.CompactAsync(cancellationToken);
                var sizeAfter = _adminStore.GetDatabaseSizeBytes();

                _lastCompactRun = DateTime.UtcNow;

                return new CompactResult
                {
                    StartedAt         = startedAt,
                    CompletedAt       = DateTime.UtcNow,
                    SizeBeforeBytes   = sizeBefore,
                    SizeAfterBytes    = sizeAfter,
                    SpaceSavedPercent = sizeBefore > 0 ? (1.0 - (double)sizeAfter / sizeBefore) * 100 : 0,
                    SnapshotPath      = snapshot?.Path,
                    Success           = true,
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during compact");
                return Failed<CompactResult>(startedAt, ex.Message);
            }
        });

        return result ?? Busy<CompactResult>(startedAt, _currentOperation);
    }

    /// <summary>
    /// Snapshot before an irreversible step. A failure to snapshot is logged and does not abort the
    /// operation: refusing to run maintenance because a disk is full would trade a recoverable risk
    /// for a store that never gets tidied at all.
    /// </summary>
    private async Task<BackupSnapshot?> Snapshot(string reason, CancellationToken cancellationToken)
    {
        if (_backups is null) return null;

        var snapshot = await _backups.CreateSnapshotAsync($"maintenance-{reason}", cancellationToken);

        if (snapshot is null && _settings.BackupBeforeDestructiveOperations)
            _logger?.LogWarning("Proceeding with {Reason} without a snapshot", reason);

        return snapshot;
    }

    // ── Result helpers ────────────────────────────────────────────────────────────────────────

    private static T Failed<T>(DateTime startedAt, string error) where T : class =>
        Build<T>(startedAt, false, error);

    private static T Busy<T>(DateTime startedAt, string? current) where T : class =>
        Build<T>(startedAt, false, $"Another maintenance operation is in progress: {current}");

    private static T Build<T>(DateTime startedAt, bool success, string? error) where T : class
    {
        var completedAt = DateTime.UtcNow;

        return (T)(object)(typeof(T) switch
        {
            var t when t == typeof(UpkeepResult) =>
                new UpkeepResult { StartedAt = startedAt, CompletedAt = completedAt, Success = success, ErrorMessage = error },
            var t when t == typeof(ConsolidationResult) =>
                new ConsolidationResult { StartedAt = startedAt, CompletedAt = completedAt, Success = success, ErrorMessage = error },
            var t when t == typeof(ReindexResult) =>
                new ReindexResult { StartedAt = startedAt, CompletedAt = completedAt, Success = success, ErrorMessage = error },
            var t when t == typeof(PurgeResult) =>
                new PurgeResult { StartedAt = startedAt, CompletedAt = completedAt, Success = success, ErrorMessage = error },
            var t when t == typeof(CompactResult) =>
                new CompactResult { StartedAt = startedAt, CompletedAt = completedAt, Success = success, ErrorMessage = error },
            _ => throw new NotSupportedException($"Unsupported maintenance result type {typeof(T).Name}"),
        });
    }
}
