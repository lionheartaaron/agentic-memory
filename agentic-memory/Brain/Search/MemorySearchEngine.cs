using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Brain.Search;

/// <summary>
/// Memory search engine with multi-signal scoring
/// Phase 1 scoring: Fuzzy (0.5), Strength (0.3), Recency (0.2)
/// Phase 2 scoring: Semantic (0.4), Fuzzy (0.3), Strength (0.2), Recency (0.1)
/// </summary>
public class MemorySearchEngine : ISearchService
{
    private readonly IMemoryRepository _repository;
    private readonly IEmbeddingService? _embeddingService;
    private readonly ILogger<MemorySearchEngine>? _logger;

    // Phase 2 scoring weights (with embeddings)
    private const double SemanticWeight = 0.4;
    private const double FuzzyWeightWithEmbeddings = 0.3;
    private const double StrengthWeightWithEmbeddings = 0.2;
    private const double RecencyWeightWithEmbeddings = 0.1;

    // Phase 1 scoring weights (fallback without embeddings)
    private const double FuzzyWeight = 0.5;
    private const double StrengthWeight = 0.3;
    private const double RecencyWeight = 0.2;

    // Maximum age in days for recency scoring (memories older than this get 0 recency score)
    private const double MaxAgeForRecencyDays = 365.0;

    /// <summary>
    /// Create a search engine without embedding support (Phase 1 scoring)
    /// </summary>
    public MemorySearchEngine(IMemoryRepository repository, ILogger<MemorySearchEngine>? logger = null)
    {
        _repository = repository;
        _embeddingService = null;
        _logger = logger;
    }

    /// <summary>
    /// Create a search engine with optional embedding support (Phase 2 scoring when available)
    /// </summary>
    public MemorySearchEngine(IMemoryRepository repository, IEmbeddingService? embeddingService, ILogger<MemorySearchEngine>? logger = null)
    {
        _repository = repository;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    /// <summary>
    /// Whether semantic search is available (embedding service is configured and ready)
    /// </summary>
    public bool SemanticSearchAvailable => _embeddingService?.IsAvailable ?? false;

    public async Task<IReadOnlyList<ScoredMemory>> SearchAsync(
        string query,
        int topN = 5,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        // Get candidates from repository
        var candidates = await _repository.SearchByTextAsync(query, topN * 3, cancellationToken);

        // If tags are specified, filter by tags
        if (tags?.Any() == true)
        {
            var tagList = tags.Select(t => t.ToLowerInvariant()).ToHashSet();
            candidates = candidates
                .Where(c => c.Tags.Any(t => tagList.Contains(t.ToLowerInvariant())))
                .ToList();
        }

        // Filter to only current memories (not archived and not superseded)
        candidates = candidates.Where(c => c.IsCurrent).ToList();

        // Prepare query data
        var normalizedQuery = query.ToLowerInvariant().Trim();
        var queryTrigrams = TrigramFuzzyMatcher.GenerateTrigrams(normalizedQuery);
        var now = DateTime.UtcNow;

        // Get query embedding if embedding service is available
        float[]? queryEmbedding = null;
        if (SemanticSearchAvailable)
        {
            try
            {
                queryEmbedding = await _embeddingService!.GetEmbeddingAsync(query, cancellationToken);
            }
            catch
            {
                // Fall back to non-semantic search if embedding fails
                queryEmbedding = null;
            }
        }

        // Score each candidate
        var scored = candidates
            .Select(c => ScoreMemory(c, normalizedQuery, queryTrigrams, now, queryEmbedding))
            .OrderByDescending(s => s.Score)
            .Take(topN)
            .ToList();

        // Reinforce accessed memories
        foreach (var result in scored)
        {
            try
            {
                await _repository.ReinforceAsync(result.Memory.Id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to reinforce memory {MemoryId}", result.Memory.Id);
            }
        }

        return scored;
    }

    private ScoredMemory ScoreMemory(
        MemoryNodeEntity memory,
        string normalizedQuery,
        HashSet<string> queryTrigrams,
        DateTime now,
        float[]? queryEmbedding)
    {
        // Extract query words for better matching
        var queryWords = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)
            .ToHashSet();

        // Calculate fuzzy score
        var fuzzyScore = CalculateFuzzyScore(memory, normalizedQuery, queryTrigrams, queryWords);

        // Calculate strength score (normalized to 0-1)
        var strengthScore = CalculateStrengthScore(memory);

        // Calculate recency score
        var recencyScore = CalculateRecencyScore(memory, now);

        // Calculate semantic score if embedding is available
        var semanticScore = 0.0;
        if (queryEmbedding is not null)
        {
            semanticScore = CalculateSemanticScore(memory, queryEmbedding);
        }

        // Combine scores with appropriate weights based on embedding availability
        double combinedScore;
        if (queryEmbedding is not null)
        {
            // Phase 2 scoring with semantic similarity
            combinedScore = (semanticScore * SemanticWeight) +
                           (fuzzyScore * FuzzyWeightWithEmbeddings) +
                           (strengthScore * StrengthWeightWithEmbeddings) +
                           (recencyScore * RecencyWeightWithEmbeddings);
        }
        else
        {
            // Phase 1 scoring without semantic similarity
            combinedScore = (fuzzyScore * FuzzyWeight) +
                           (strengthScore * StrengthWeight) +
                           (recencyScore * RecencyWeight);
        }

        return new ScoredMemory
        {
            Memory = memory,
            Score = combinedScore,
            FuzzyScore = fuzzyScore,
            StrengthScore = strengthScore,
            RecencyScore = recencyScore,
            SemanticScore = semanticScore
        };
    }

    private static double CalculateFuzzyScore(
        MemoryNodeEntity memory,
        string normalizedQuery,
        HashSet<string> queryTrigrams,
        HashSet<string> queryWords)
    {
        var scores = new List<double>();

        // Exact match in content gets highest score
        if (memory.ContentNormalized.Contains(normalizedQuery))
        {
            scores.Add(1.0);
        }

        // Title match gets high score
        var titleLower = memory.Title.ToLowerInvariant();
        if (titleLower.Contains(normalizedQuery))
        {
            scores.Add(0.95);
        }

        // Check if any query word matches a tag exactly or partially
        foreach (var tag in memory.Tags)
        {
            var tagLower = tag.ToLowerInvariant();
            if (normalizedQuery.Contains(tagLower) || tagLower.Contains(normalizedQuery))
            {
                scores.Add(0.9);
                break;
            }
            if (queryWords.Any(w => tagLower.Contains(w) || w.Contains(tagLower)))
            {
                scores.Add(0.8);
                break;
            }
        }

        // Check individual word matches in title
        var titleMatchCount = queryWords.Count(w => titleLower.Contains(w));
        if (titleMatchCount > 0 && queryWords.Count > 0)
        {
            scores.Add(0.7 * titleMatchCount / queryWords.Count);
        }

        // Check individual word matches in content
        var contentMatchCount = queryWords.Count(w => memory.ContentNormalized.Contains(w));
        if (contentMatchCount > 0 && queryWords.Count > 0)
        {
            scores.Add(0.5 * contentMatchCount / queryWords.Count);
        }

        // Trigram similarity (always calculated as baseline)
        var storedTrigrams = new HashSet<string>(memory.Trigrams, StringComparer.OrdinalIgnoreCase);
        var trigramScore = TrigramFuzzyMatcher.CalculateSimilarity(queryTrigrams, storedTrigrams);
        if (trigramScore > 0.05)
        {
            scores.Add(trigramScore * 0.6); // Scale down trigram scores
        }

        // Return the best score, or 0 if no matches
        return scores.Count > 0 ? scores.Max() : 0;
    }

    private static double CalculateStrengthScore(MemoryNodeEntity memory)
    {
        var strength = memory.GetCurrentStrength();

        // Normalize strength to 0-1 range using sigmoid-like function
        // Most memories will have strength between 0.5 and 3.0
        return Math.Min(1.0, strength / 2.0);
    }

    private static double CalculateRecencyScore(MemoryNodeEntity memory, DateTime now)
    {
        var ageInDays = (now - memory.LastAccessedAt).TotalDays;

        // Linear decay over MaxAgeForRecencyDays
        if (ageInDays >= MaxAgeForRecencyDays)
            return 0.0;

        return 1.0 - (ageInDays / MaxAgeForRecencyDays);
    }

    private static double CalculateSemanticScore(MemoryNodeEntity memory, float[] queryEmbedding)
    {
        var memoryEmbedding = memory.GetEmbedding();

        if (memoryEmbedding is null || memoryEmbedding.Length == 0)
            return 0.0;

        // Use normalized cosine similarity (0 to 1 range)
        return VectorMath.NormalizedCosineSimilarity(queryEmbedding, memoryEmbedding);
    }
}
