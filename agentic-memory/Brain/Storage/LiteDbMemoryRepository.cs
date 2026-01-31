using System.Text;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using LiteDB;

namespace AgenticMemory.Brain.Storage;

/// <summary>
/// LiteDB implementation of IMemoryRepository with fuzzy search support
/// </summary>
public class LiteDbMemoryRepository : IMemoryRepository
{
    private const string CollectionName = "memories";
    private const double WeakMemoryThreshold = 0.3;

    private readonly LiteDatabase _database;
    private readonly ILiteCollection<MemoryNodeEntity> _collection;
    private bool _disposed;

    public LiteDbMemoryRepository(string databasePath)
    {
        // Ensure directory exists
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Use shared connection mode for multi-threaded access
        var connectionString = new ConnectionString
        {
            Filename = databasePath,
            Connection = ConnectionType.Shared
        };

        _database = new LiteDatabase(connectionString);
        _collection = _database.GetCollection<MemoryNodeEntity>(CollectionName);

        EnsureIndexes();
    }

    /// <summary>
    /// Removes unpaired surrogate characters from a string that would cause LiteDB encoding errors.
    /// Valid surrogate pairs (emojis) are preserved.
    /// </summary>
    private static string SanitizeForLiteDb(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? string.Empty;

        var sb = new StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];

            if (char.IsHighSurrogate(c))
            {
                // Check if there's a valid low surrogate following
                if (i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
                {
                    // Valid surrogate pair - keep both characters
                    sb.Append(c);
                    sb.Append(input[i + 1]);
                    i++; // Skip the low surrogate since we've already added it
                }
                // else: orphaned high surrogate - skip it
            }
            else if (char.IsLowSurrogate(c))
            {
                // Orphaned low surrogate - skip it
            }
            else
            {
                // Normal character
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Sanitizes all string properties of a MemoryNodeEntity for safe storage in LiteDB.
    /// </summary>
    private static void SanitizeEntity(MemoryNodeEntity node)
    {
        node.Title = SanitizeForLiteDb(node.Title);
        node.Summary = SanitizeForLiteDb(node.Summary);
        node.Content = SanitizeForLiteDb(node.Content);
        node.ContentNormalized = SanitizeForLiteDb(node.ContentNormalized);

        // Sanitize tags
        for (int i = 0; i < node.Tags.Count; i++)
        {
            node.Tags[i] = SanitizeForLiteDb(node.Tags[i]);
        }

        // Sanitize trigrams
        for (int i = 0; i < node.Trigrams.Count; i++)
        {
            node.Trigrams[i] = SanitizeForLiteDb(node.Trigrams[i]);
        }
    }



    private void EnsureIndexes()
    {
        // Index on normalized content for text search
        _collection.EnsureIndex(x => x.ContentNormalized);

        // Index on tags for tag-based filtering
        _collection.EnsureIndex(x => x.Tags);

        // Index on trigrams for fuzzy search
        _collection.EnsureIndex(x => x.Trigrams);

        // Index on strength-related fields for pruning queries
        _collection.EnsureIndex(x => x.BaseStrength);
        _collection.EnsureIndex(x => x.LastAccessedAt);

        // Index on creation date
        _collection.EnsureIndex(x => x.CreatedAt);

        // Index for temporal validity (conflict resolution)
        _collection.EnsureIndex(x => x.ValidUntil);
    }

    public Task<MemoryNodeEntity?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MemoryNodeEntity? entity = _collection.FindById(id);
        return Task.FromResult<MemoryNodeEntity?>(entity);
    }

    public Task<IReadOnlyList<MemoryNodeEntity>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entities = _collection.FindAll().ToList();
        return Task.FromResult<IReadOnlyList<MemoryNodeEntity>>(entities);
    }

    public Task SaveAsync(MemoryNodeEntity node, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Pre-compute normalized content and trigrams for search
        // Only use title, summary, and tags for indexing to avoid LiteDB's 1023-byte index key limit
        // Full content is stored but not indexed
        var tagsText = node.Tags.Count > 0 ? " " + string.Join(" ", node.Tags) : "";
        var searchableText = $"{node.Title} {node.Summary}{tagsText}".ToLowerInvariant().Trim();
        
        // Limit searchable text to prevent index key size issues
        const int maxIndexableLength = 800; // Leave room for trigram overhead
        if (searchableText.Length > maxIndexableLength)
        {
            searchableText = searchableText[..maxIndexableLength];
        }
        
        node.ContentNormalized = searchableText;
        node.Trigrams = Search.TrigramFuzzyMatcher.GenerateTrigramList(searchableText);

        // Sanitize all string properties to remove unpaired surrogates that LiteDB can't handle
        SanitizeEntity(node);

        // Use explicit Update/Insert instead of Upsert for more reliable behavior
        var existing = _collection.FindById(node.Id);
        if (existing != null)
        {
            _collection.Update(node);
        }
        else
        {
            _collection.Insert(node);
        }
        
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deleted = _collection.Delete(id);
        return Task.FromResult(deleted);
    }

    public Task<IReadOnlyList<MemoryNodeEntity>> SearchByTextAsync(string query, int limit = 10, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<MemoryNodeEntity>>([]);

        var normalizedQuery = query.ToLowerInvariant().Trim();
        var queryTrigrams = Search.TrigramFuzzyMatcher.GenerateTrigrams(normalizedQuery);

        // Also extract individual words for better matching
        var queryWords = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3)
            .ToList();

        // Get all candidates that share at least one trigram
        // Build an OR query for all trigrams in the query
        var trigramQueries = queryTrigrams.Select(t => Query.Contains("Trigrams", t)).ToArray();
        
        List<MemoryNodeEntity> candidates;
        if (trigramQueries.Length > 0)
        {
            candidates = _collection.Find(Query.Or(trigramQueries)).ToList();
        }
        else
        {
            candidates = [];
        }

        // If no trigram matches, try basic contains on content
        if (candidates.Count == 0)
        {
            candidates = _collection
                .Find(x => x.ContentNormalized.Contains(normalizedQuery))
                .ToList();
        }

        // Also search for individual words if query has multiple words
        if (candidates.Count < limit && queryWords.Count > 1)
        {
            foreach (var word in queryWords)
            {
                var wordTrigrams = Search.TrigramFuzzyMatcher.GenerateTrigrams(word);
                var wordQueries = wordTrigrams.Select(t => Query.Contains("Trigrams", t)).ToArray();
                if (wordQueries.Length > 0)
                {
                    var wordMatches = _collection.Find(Query.Or(wordQueries)).ToList();
                    candidates = candidates.Union(wordMatches).ToList();
                }
            }
        }

        // Also check tag matches directly
        var tagMatches = _collection.FindAll()
            .Where(x => x.Tags.Any(t => 
                normalizedQuery.Contains(t.ToLowerInvariant()) || 
                t.ToLowerInvariant().Contains(normalizedQuery) ||
                queryWords.Any(w => t.ToLowerInvariant().Contains(w) || w.Contains(t.ToLowerInvariant()))))
            .ToList();
        candidates = candidates.Union(tagMatches).ToList();

        // Score candidates by trigram similarity, word overlap, and sort
        var scored = candidates
            .Select(c => new
            {
                Entity = c,
                FuzzyScore = Search.TrigramFuzzyMatcher.CalculateSimilarity(normalizedQuery, c.Trigrams),
                WordOverlap = CalculateWordOverlap(queryWords, c.ContentNormalized),
                ExactMatch = c.ContentNormalized.Contains(normalizedQuery) ? 0.5 : 0.0,
                TagMatch = c.Tags.Any(t => queryWords.Any(w => t.ToLowerInvariant().Contains(w))) ? 0.3 : 0.0,
                Strength = c.GetCurrentStrength()
            })
            .Where(x => x.FuzzyScore > 0.05 || x.WordOverlap > 0 || x.ExactMatch > 0 || x.TagMatch > 0)
            .OrderByDescending(x => x.ExactMatch + x.FuzzyScore * 0.5 + x.WordOverlap * 0.3 + x.TagMatch + x.Strength * 0.2)
            .Take(limit)
            .Select(x => x.Entity)
            .ToList();

        return Task.FromResult<IReadOnlyList<MemoryNodeEntity>>(scored);
    }

    private static double CalculateWordOverlap(List<string> queryWords, string content)
    {
        if (queryWords.Count == 0) return 0;
        var matches = queryWords.Count(w => content.Contains(w));
        return (double)matches / queryWords.Count;
    }

    public Task<IReadOnlyList<MemoryNodeEntity>> SearchByTagsAsync(IEnumerable<string> tags, int limit = 10, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tagList = tags.Select(t => t.ToLowerInvariant()).ToList();
        if (tagList.Count == 0)
            return Task.FromResult<IReadOnlyList<MemoryNodeEntity>>([]);

        // Fetch all and filter in memory for reliable tag matching
        // LiteDB's Query.Contains on arrays can be unreliable for exact element matching
        var candidates = _collection.FindAll()
            .Where(x => x.Tags.Any(entityTag => 
                tagList.Any(searchTag => entityTag.Equals(searchTag, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.GetCurrentStrength())
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<MemoryNodeEntity>>(candidates);
    }

    public Task<IReadOnlyList<MemoryNodeEntity>> GetWeakMemoriesAsync(double strengthThreshold, int limit = 100, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // We need to calculate current strength which involves decay,
        // so we fetch candidates and filter in memory
        var candidates = _collection.FindAll()
            .Where(x => x.GetCurrentStrength() < strengthThreshold)
            .OrderBy(x => x.GetCurrentStrength())
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<MemoryNodeEntity>>(candidates);
    }

    public Task<RepositoryStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var allNodes = _collection.FindAll().ToList();
        var totalCount = allNodes.Count;

        if (totalCount == 0)
        {
            return Task.FromResult(new RepositoryStats
            {
                TotalNodes = 0,
                AverageStrength = 0,
                WeakMemoriesCount = 0,
                OldestMemory = null,
                NewestMemory = null,
                DatabaseSizeBytes = GetDatabaseSize()
            });
        }

        var strengths = allNodes.Select(x => x.GetCurrentStrength()).ToList();

        var stats = new RepositoryStats
        {
            TotalNodes = totalCount,
            AverageStrength = strengths.Average(),
            WeakMemoriesCount = strengths.Count(s => s < WeakMemoryThreshold),
            OldestMemory = allNodes.Min(x => x.CreatedAt),
            NewestMemory = allNodes.Max(x => x.CreatedAt),
            DatabaseSizeBytes = GetDatabaseSize()
        };

        return Task.FromResult(stats);
    }

    public Task ReinforceAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entity = _collection.FindById(id);
        if (entity is not null)
        {
            entity.Reinforce();
            _collection.Update(entity);
        }

        return Task.CompletedTask;
    }

    public Task<int> PruneWeakMemoriesAsync(double strengthThreshold, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var weakMemories = _collection.FindAll()
            .Where(x => x.GetCurrentStrength() < strengthThreshold)
            .Select(x => x.Id)
            .ToList();

        foreach (var id in weakMemories)
        {
            _collection.Delete(id);
        }

        return Task.FromResult(weakMemories.Count);
    }

    public Task CompactAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _database.Rebuild();
        return Task.CompletedTask;
    }

    private long GetDatabaseSize()
    {
        try
        {
            var dbPath = _database.GetCollection("$dump").FindAll().FirstOrDefault()?["filename"]?.AsString;
            if (!string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
            {
                return new FileInfo(dbPath).Length;
            }
        }
        catch
        {
            // Ignore errors getting file size
        }
        return 0;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _database.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
