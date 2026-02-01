using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Configuration;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Brain.Conflict;

/// <summary>
/// Service that handles memory storage with conflict detection and resolution.
/// Detects duplicates, supersedes highly similar content, and maintains temporal history.
/// Uses content similarity rather than tags to determine when memories should be superseded.
/// </summary>
public class ConflictAwareStorageService : IConflictAwareStorage
{
    private readonly IMemoryRepository _repository;
    private readonly ISearchService _searchService;
    private readonly IEmbeddingService? _embeddingService;
    private readonly ConflictSettings _settings;
    private readonly ILogger<ConflictAwareStorageService>? _logger;

    public ConflictAwareStorageService(
        IMemoryRepository repository,
        ISearchService searchService,
        IEmbeddingService? embeddingService,
        ConflictSettings settings,
        ILogger<ConflictAwareStorageService>? logger = null)
    {
        _repository = repository;
        _searchService = searchService;
        _embeddingService = embeddingService;
        _settings = settings;
        _logger = logger;
    }

    public async Task<StoreResult> StoreAsync(MemoryNodeEntity entity, CancellationToken cancellationToken = default)
    {
        // Generate embedding for the new memory if not already set
        if (entity.EmbeddingBytes is null && _embeddingService?.IsAvailable == true)
        {
            try
            {
                var textToEmbed = $"{entity.Title} {entity.Summary} {entity.Content}";
                var embedding = await _embeddingService.GetEmbeddingAsync(textToEmbed, cancellationToken);
                entity.SetEmbedding(embedding);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to generate embedding for new memory");
            }
        }

        // Search for similar memories
        var searchText = $"{entity.Title} {entity.Summary}";
        var similarMemories = await _searchService.SearchAsync(searchText, 10, cancellationToken: cancellationToken);

        // Check for duplicate (very high similarity - nearly identical content)
        var duplicate = similarMemories.FirstOrDefault(m => m.SemanticScore >= _settings.DuplicateSimilarityThreshold);
        if (duplicate is not null)
        {
            _logger?.LogInformation(
                "Duplicate detected: new memory '{NewTitle}' matches existing '{ExistingTitle}' (score: {Score:F2})",
                entity.Title, duplicate.Memory.Title, duplicate.SemanticScore);

            // Reinforce the existing memory instead of creating a duplicate
            await _repository.ReinforceAsync(duplicate.Memory.Id, cancellationToken);

            // Update the existing memory if new content adds value
            if (!string.IsNullOrWhiteSpace(entity.Content) && 
                entity.Content.Length > duplicate.Memory.Content.Length)
            {
                duplicate.Memory.Content = entity.Content;
                await _repository.SaveAsync(duplicate.Memory, cancellationToken);
            }

            return new StoreResult
            {
                Memory = duplicate.Memory,
                Action = StoreAction.ReinforcedExisting,
                Message = $"Similar memory already exists (similarity: {duplicate.SemanticScore:P0}). Reinforced existing memory '{duplicate.Memory.Title}' instead."
            };
        }

        // Check for highly similar content that should supersede existing memories
        // This is the key change: supersede based on content similarity, not tags
        var supersedeCandidates = similarMemories
            .Where(m => m.SemanticScore >= _settings.SupersedeSimilarityThreshold && 
                        m.SemanticScore < _settings.DuplicateSimilarityThreshold &&
                        m.Memory.IsCurrent) // Only supersede current (non-archived) memories
            .ToList();

        if (supersedeCandidates.Count > 0)
        {
            var supersededMemories = new List<MemoryNodeEntity>();

            foreach (var candidate in supersedeCandidates)
            {
                if (candidate.Memory.Id == entity.Id) continue; // Skip self if updating

                // Fetch fresh copy from repository to avoid race conditions with search reinforcement
                var oldMemory = await _repository.GetAsync(candidate.Memory.Id, cancellationToken);
                if (oldMemory is null || oldMemory.IsArchived) continue; // Already archived or deleted

                _logger?.LogInformation(
                    "Superseding memory '{OldTitle}' with new memory '{NewTitle}' (similarity: {Score:F2})",
                    oldMemory.Title, entity.Title, candidate.SemanticScore);

                // Mark the old memory as superseded
                oldMemory.ValidUntil = DateTime.UtcNow;
                oldMemory.SupersededBy = entity.Id;
                oldMemory.IsArchived = true;
                await _repository.SaveAsync(oldMemory, cancellationToken);

                supersededMemories.Add(oldMemory);
            }

            if (supersededMemories.Count > 0)
            {
                // Track which memories this one superseded
                entity.SupersededIds = supersededMemories.Select(m => m.Id).ToList();
                entity.ValidFrom = DateTime.UtcNow;

                await _repository.SaveAsync(entity, cancellationToken);

                var supersededTitles = string.Join(", ", supersededMemories.Select(m => $"'{m.Title}'"));
                return new StoreResult
                {
                    Memory = entity,
                    Action = StoreAction.StoredWithSupersede,
                    SupersededMemories = supersededMemories,
                    Message = $"Memory stored. Superseded {supersededMemories.Count} previous memor{(supersededMemories.Count == 1 ? "y" : "ies")}: {supersededTitles}."
                };
            }
        }

        // Check for related but non-conflicting memories (coexistence case)
        var similar = similarMemories.FirstOrDefault(m => m.SemanticScore >= _settings.CoexistSimilarityThreshold);
        if (similar is not null)
        {
            // Store as coexisting - both memories are valid but related
            entity.ValidFrom = DateTime.UtcNow;
            await _repository.SaveAsync(entity, cancellationToken);

            return new StoreResult
            {
                Memory = entity,
                Action = StoreAction.StoredCoexist,
                Message = $"Memory stored. Note: Related memory exists: '{similar.Memory.Title}' (similarity: {similar.SemanticScore:P0})."
            };
        }

        // No conflicts - store as new
        entity.ValidFrom = DateTime.UtcNow;
        await _repository.SaveAsync(entity, cancellationToken);

        return new StoreResult
        {
            Memory = entity,
            Action = StoreAction.StoredNew,
            Message = "Memory stored successfully."
        };
    }

    public async Task<IReadOnlyList<MemoryNodeEntity>> GetTagHistoryAsync(
        string tag,
        bool includeArchived = true,
        CancellationToken cancellationToken = default)
    {
        var allMemories = await _repository.GetAllAsync(cancellationToken);

        var tagMemories = allMemories
            .Where(m => m.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .Where(m => includeArchived || m.IsCurrent)
            .OrderByDescending(m => m.ValidFrom)
            .ToList();

        return tagMemories;
    }
}
