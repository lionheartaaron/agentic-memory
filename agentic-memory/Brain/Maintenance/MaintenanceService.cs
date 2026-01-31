using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Search;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Brain.Maintenance;

/// <summary>
/// Implementation of memory maintenance operations including decay, consolidation, and reindexing
/// </summary>
public class MaintenanceService : IMaintenanceService
{
    private readonly IMemoryRepository _repository;
    private readonly IEmbeddingService? _embeddingService;
    private readonly ILogger<MaintenanceService>? _logger;

    private DateTime? _lastDecayRun;
    private DateTime? _lastConsolidationRun;
    private DateTime? _lastReindexRun;
    private DateTime? _lastCompactRun;
    private volatile bool _isRunning;
    private string? _currentOperation;

    public MaintenanceService(
        IMemoryRepository repository,
        IEmbeddingService? embeddingService = null,
        ILogger<MaintenanceService>? logger = null)
    {
        _repository = repository;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task<DecayResult> ApplyDecayAsync(double pruneThreshold = 0.1, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;

        if (_isRunning)
        {
            return new DecayResult
            {
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                Success = false,
                ErrorMessage = $"Another maintenance operation is in progress: {_currentOperation}"
            };
        }

        try
        {
            _isRunning = true;
            _currentOperation = "Decay";

            _logger?.LogInformation("Starting decay operation with prune threshold {Threshold}", pruneThreshold);

            // Get stats before decay
            var statsBefore = await _repository.GetStatsAsync(cancellationToken);
            var avgStrengthBefore = statsBefore.AverageStrength;

            // Get all memories and calculate their current strength
            var memories = await _repository.GetAllAsync(cancellationToken);
            var processed = 0;

            foreach (var memory in memories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Current strength is calculated dynamically via GetCurrentStrength()
                // We don't need to update the database for decay since it's time-based
                processed++;
            }

            // Prune memories that have decayed below threshold
            var pruned = await _repository.PruneWeakMemoriesAsync(pruneThreshold, cancellationToken);

            // Get stats after decay
            var statsAfter = await _repository.GetStatsAsync(cancellationToken);
            var avgStrengthAfter = statsAfter.AverageStrength;

            _lastDecayRun = DateTime.UtcNow;

            _logger?.LogInformation(
                "Decay operation completed. Processed: {Processed}, Pruned: {Pruned}, Avg strength: {Before:F3} -> {After:F3}",
                processed, pruned, avgStrengthBefore, avgStrengthAfter);

            return new DecayResult
            {
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                MemoriesProcessed = processed,
                MemoriesPruned = pruned,
                AverageStrengthBefore = avgStrengthBefore,
                AverageStrengthAfter = avgStrengthAfter,
                Success = true
            };
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Decay operation was cancelled");
            return new DecayResult
            {
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                Success = false,
                ErrorMessage = "Operation was cancelled"
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during decay operation");
            return new DecayResult
            {
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            _isRunning = false;
            _currentOperation = null;
        }
    }

    public async Task<ConsolidationResult> ConsolidateMemoriesAsync(double similarityThreshold = 0.8, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;

        if (_isRunning)
        {
            return new ConsolidationResult
            {
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                Success = false,
                ErrorMessage = $"Another maintenance operation is in progress: {_currentOperation}"
            };
        }

        try
        {
            _isRunning = true;
            _currentOperation = "Consolidation";

            _logger?.LogInformation("Starting consolidation with similarity threshold {Threshold}", similarityThreshold);

            var memories = (await _repository.GetAllAsync(cancellationToken))
                .Where(m => !m.IsArchived)
                .ToList();

            var analyzed = memories.Count;
            var merged = 0;
            var archived = 0;
            var clustersFound = 0;

            // Find similar memory clusters
            var processed = new HashSet<Guid>();
            var clusters = new List<List<MemoryNodeEntity>>();

            foreach (var memory in memories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (processed.Contains(memory.Id))
                    continue;

                var cluster = new List<MemoryNodeEntity> { memory };
                processed.Add(memory.Id);

                // Find similar memories
                foreach (var other in memories)
                {
                    if (processed.Contains(other.Id))
                        continue;

                    var similarity = CalculateSimilarity(memory, other);
                    if (similarity >= similarityThreshold)
                    {
                        cluster.Add(other);
                        processed.Add(other.Id);
                    }
                }

                if (cluster.Count > 1)
                {
                    clusters.Add(cluster);
                    clustersFound++;
                }
            }

            // Merge clusters - keep the strongest memory, archive others
            foreach (var cluster in clusters)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Sort by current strength descending
                var sorted = cluster.OrderByDescending(m => m.GetCurrentStrength()).ToList();
                var strongest = sorted[0];

                // Reinforce the strongest memory
                await _repository.ReinforceAsync(strongest.Id, cancellationToken);
                merged++;

                // Archive weaker duplicates
                foreach (var weaker in sorted.Skip(1))
                {
                    weaker.IsArchived = true;
                    weaker.SupersededBy = strongest.Id;
                    await _repository.SaveAsync(weaker, cancellationToken);
                    archived++;
                }
            }

            _lastConsolidationRun = DateTime.UtcNow;

            _logger?.LogInformation(
                "Consolidation completed. Analyzed: {Analyzed}, Clusters: {Clusters}, Merged: {Merged}, Archived: {Archived}",
                analyzed, clustersFound, merged, archived);

            return new ConsolidationResult
            {
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                MemoriesAnalyzed = analyzed,
                MemoriesMerged = merged,
                MemoriesArchived = archived,
                ClustersFound = clustersFound,
                Success = true
            };
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Consolidation operation was cancelled");
            return new ConsolidationResult
            {
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                Success = false,
                ErrorMessage = "Operation was cancelled"
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during consolidation operation");
            return new ConsolidationResult
            {
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            _isRunning = false;
            _currentOperation = null;
        }
    }

    public async Task<ReindexResult> ReindexAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;

        if (_isRunning)
        {
            return new ReindexResult
            {
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                Success = false,
                ErrorMessage = $"Another maintenance operation is in progress: {_currentOperation}"
            };
        }

        try
        {
            _isRunning = true;
            _currentOperation = "Reindex";

            _logger?.LogInformation("Starting reindex operation");

            var memories = await _repository.GetAllAsync(cancellationToken);
            var reindexed = 0;
            var embeddingsGenerated = 0;
            var trigramsGenerated = 0;

            foreach (var memory in memories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Regenerate trigrams
                var previousTrigramCount = memory.Trigrams.Count;
                memory.ContentNormalized = $"{memory.Title} {memory.Summary} {memory.Content}".ToLowerInvariant().Trim();
                memory.Trigrams = TrigramFuzzyMatcher.GenerateTrigramList(memory.ContentNormalized);
                trigramsGenerated += memory.Trigrams.Count;

                // Regenerate embeddings if service is available
                if (_embeddingService?.IsAvailable == true)
                {
                    try
                    {
                        var embedding = await _embeddingService.GetEmbeddingAsync(memory.ContentNormalized, cancellationToken);
                        memory.SetEmbedding(embedding);
                        embeddingsGenerated++;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to generate embedding for memory {Id}", memory.Id);
                    }
                }

                await _repository.SaveAsync(memory, cancellationToken);
                reindexed++;

                if (reindexed % 100 == 0)
                {
                    _logger?.LogDebug("Reindexed {Count} memories...", reindexed);
                }
            }

            _lastReindexRun = DateTime.UtcNow;

            _logger?.LogInformation(
                "Reindex completed. Memories: {Reindexed}, Embeddings: {Embeddings}, Trigrams: {Trigrams}",
                reindexed, embeddingsGenerated, trigramsGenerated);

            return new ReindexResult
            {
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                MemoriesReindexed = reindexed,
                EmbeddingsGenerated = embeddingsGenerated,
                TrigramsGenerated = trigramsGenerated,
                Success = true
            };
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Reindex operation was cancelled");
            return new ReindexResult
            {
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                Success = false,
                ErrorMessage = "Operation was cancelled"
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during reindex operation");
            return new ReindexResult
            {
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            _isRunning = false;
            _currentOperation = null;
        }
    }

    public async Task<CompactResult> CompactDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;

        if (_isRunning)
        {
            return new CompactResult
            {
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                Success = false,
                ErrorMessage = $"Another maintenance operation is in progress: {_currentOperation}"
            };
        }

        try
        {
            _isRunning = true;
            _currentOperation = "Compact";

            _logger?.LogInformation("Starting database compact operation");

            // Get size before
            var statsBefore = await _repository.GetStatsAsync(cancellationToken);
            var sizeBefore = statsBefore.DatabaseSizeBytes;

            // Compact
            await _repository.CompactAsync(cancellationToken);

            // Get size after
            var statsAfter = await _repository.GetStatsAsync(cancellationToken);
            var sizeAfter = statsAfter.DatabaseSizeBytes;

            var spaceSaved = sizeBefore > 0 ? (1.0 - (double)sizeAfter / sizeBefore) * 100 : 0;

            _lastCompactRun = DateTime.UtcNow;

            _logger?.LogInformation(
                "Compact completed. Size: {Before:N0} -> {After:N0} bytes ({Saved:F1}% saved)",
                sizeBefore, sizeAfter, spaceSaved);

            return new CompactResult
            {
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                SizeBeforeBytes = sizeBefore,
                SizeAfterBytes = sizeAfter,
                SpaceSavedPercent = spaceSaved,
                Success = true
            };
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Compact operation was cancelled");
            return new CompactResult
            {
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                Success = false,
                ErrorMessage = "Operation was cancelled"
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during compact operation");
            return new CompactResult
            {
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            _isRunning = false;
            _currentOperation = null;
        }
    }

    public MaintenanceStatus GetStatus()
    {
        return new MaintenanceStatus
        {
            LastDecayRun = _lastDecayRun,
            LastConsolidationRun = _lastConsolidationRun,
            LastReindexRun = _lastReindexRun,
            LastCompactRun = _lastCompactRun,
            IsRunning = _isRunning,
            CurrentOperation = _currentOperation
        };
    }

    /// <summary>
    /// Calculate similarity between two memories using trigrams and optional embeddings
    /// </summary>
    private double CalculateSimilarity(MemoryNodeEntity a, MemoryNodeEntity b)
    {
        // Trigram similarity (Jaccard coefficient)
        var trigramSimilarity = TrigramFuzzyMatcher.CalculateSimilarity(
            a.ContentNormalized,
            b.ContentNormalized);

        // If embeddings are available, use them for semantic similarity
        if (_embeddingService?.IsAvailable == true && a.EmbeddingBytes != null && b.EmbeddingBytes != null)
        {
            var embeddingA = a.GetEmbedding();
            var embeddingB = b.GetEmbedding();

            if (embeddingA != null && embeddingB != null)
            {
                var semanticSimilarity = VectorMath.CosineSimilarity(embeddingA, embeddingB);
                // Weighted average: 60% semantic, 40% trigram
                return semanticSimilarity * 0.6 + trigramSimilarity * 0.4;
            }
        }

        return trigramSimilarity;
    }
}
