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
    }

    public Task<MemoryNodeEntity?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entity = _collection.FindById(id);
        return Task.FromResult(entity);
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

        // Pre-compute normalized content and trigrams
        node.ContentNormalized = $"{node.Title} {node.Summary} {node.Content}".ToLowerInvariant().Trim();
        node.Trigrams = Search.TrigramFuzzyMatcher.GenerateTrigramList(node.ContentNormalized);

        _collection.Upsert(node);
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

        // Get all candidates that share at least one trigram
        // Build an OR query for all trigrams in the query
        var trigramQueries = queryTrigrams.Select(t => Query.Contains("Trigrams", t)).ToArray();
        var candidates = _collection.Find(Query.Or(trigramQueries)).ToList();

        // If no trigram matches, try basic contains
        if (candidates.Count == 0)
        {
            candidates = _collection
                .Find(x => x.ContentNormalized.Contains(normalizedQuery))
                .ToList();
        }

        // Score candidates by trigram similarity and sort
        var scored = candidates
            .Select(c => new
            {
                Entity = c,
                FuzzyScore = Search.TrigramFuzzyMatcher.CalculateSimilarity(normalizedQuery, c.Trigrams),
                Strength = c.GetCurrentStrength()
            })
            .Where(x => x.FuzzyScore > 0.1 || x.Entity.ContentNormalized.Contains(normalizedQuery))
            .OrderByDescending(x => x.FuzzyScore * 0.7 + x.Strength * 0.3)
            .Take(limit)
            .Select(x => x.Entity)
            .ToList();

        return Task.FromResult<IReadOnlyList<MemoryNodeEntity>>(scored);
    }

    public Task<IReadOnlyList<MemoryNodeEntity>> SearchByTagsAsync(IEnumerable<string> tags, int limit = 10, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tagList = tags.Select(t => t.ToLowerInvariant()).ToList();
        if (tagList.Count == 0)
            return Task.FromResult<IReadOnlyList<MemoryNodeEntity>>([]);

        var results = _collection
            .Find(x => x.Tags.Any(t => tagList.Contains(t.ToLower())))
            .OrderByDescending(x => x.GetCurrentStrength())
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<MemoryNodeEntity>>(results);
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
