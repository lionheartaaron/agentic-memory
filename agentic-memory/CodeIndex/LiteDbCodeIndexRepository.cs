using System.Security.Cryptography;
using System.Text;
using AgenticMemory.Brain.Search;
using LiteDB;

namespace AgenticMemory.CodeIndex;

public sealed class LiteDbCodeIndexRepository : ICodeIndexRepository
{
    private const string CollectionName = "code_index";

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<CodeIndexRecord> _col;
    private bool _disposed;

    public LiteDbCodeIndexRepository(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var mapper = new BsonMapper();
        mapper.Entity<CodeIndexRecord>().Id(x => x.Id);

        _db = new LiteDatabase(new ConnectionString
        {
            Filename = databasePath,
            Connection = ConnectionType.Shared,
        }, mapper);

        _col = _db.GetCollection<CodeIndexRecord>(CollectionName);
        EnsureIndexes();
    }

    /// <summary>Stable, path-addressable ID: SHA256 of the lowercased full path.</summary>
    public static string ComputeId(string filePath) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(filePath).ToLowerInvariant())));

    private void EnsureIndexes()
    {
        _col.EnsureIndex(x => x.ProjectId);
        _col.EnsureIndex(x => x.FilePath);
        _col.EnsureIndex(x => x.IsStale);
        _col.EnsureIndex(x => x.IndexedAt);
    }

    public Task UpsertAsync(CodeIndexRecord record, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _col.Upsert(record);
        return Task.CompletedTask;
    }

    public Task<CodeIndexRecord?> GetByPathAsync(string filePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var id = ComputeId(filePath);
        return Task.FromResult<CodeIndexRecord?>(_col.FindById(id));
    }

    public Task<IReadOnlyList<CodeIndexRecord>> GetByProjectAsync(string projectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var records = _col.Find(x => x.ProjectId == projectId)
            .OrderBy(x => x.RelativePath)
            .ToList();
        return Task.FromResult<IReadOnlyList<CodeIndexRecord>>(records);
    }

    public Task<IReadOnlyList<(CodeIndexRecord Record, float Score)>> SearchByEmbeddingAsync(
        float[] query, string? projectId, int topN, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var candidates = projectId != null
            ? _col.Find(x => x.ProjectId == projectId).Where(x => x.Embedding != null).ToList()
            : _col.FindAll().Where(x => x.Embedding != null).ToList();

        var scored = candidates
            .Select(r => (Record: r, Score: VectorMath.CosineSimilarity(query, r.Embedding!)))
            .OrderByDescending(x => x.Score)
            .Take(topN)
            .ToList();

        return Task.FromResult<IReadOnlyList<(CodeIndexRecord, float)>>(scored);
    }

    public Task<IReadOnlyList<CodeIndexRecord>> SearchLexicalAsync(
        string query, string? projectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var q = query.ToLowerInvariant();

        var candidates = projectId != null
            ? _col.Find(x => x.ProjectId == projectId).ToList()
            : _col.FindAll().ToList();

        var matches = candidates.Where(r =>
            r.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            r.RelativePath.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            (r.SymbolsText ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return Task.FromResult<IReadOnlyList<CodeIndexRecord>>(matches);
    }

    public Task<IReadOnlyList<CodeIndexRecord>> GetByIdsAsync(
        IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var set = ids.ToHashSet();
        var records = _col.Find(x => set.Contains(x.Id)).ToList();
        return Task.FromResult<IReadOnlyList<CodeIndexRecord>>(records);
    }

    public Task MarkProjectStaleAsync(string projectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var records = _col.Find(x => x.ProjectId == projectId).ToList();
        foreach (var r in records)
        {
            r.IsStale = true;
            _col.Update(r);
        }
        return Task.CompletedTask;
    }

    public Task DeleteByPathAsync(string filePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _col.Delete(ComputeId(filePath));
        return Task.CompletedTask;
    }

    public Task<int> CountAsync(string projectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_col.Count(x => x.ProjectId == projectId));
    }

    public Task<(int indexed, int stale, int errored)> GetProjectStatsAsync(
        string projectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var all = _col.Find(x => x.ProjectId == projectId).ToList();
        return Task.FromResult((all.Count, all.Count(x => x.IsStale), all.Count(x => x.IngestionError != null)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _db.Dispose();
    }
}
