using System.Security.Cryptography;
using System.Text;
using AgenticMemory.Brain.Search;
using AgenticMemory.Persistence;
using LiteDB;

namespace AgenticMemory.CodeIndex;

public sealed class LiteDbCodeIndexRepository : ICodeIndexRepository
{
    private const string CollectionName     = "code_index";
    private const string SymRefCollection   = "symbol_references";
    private const string DomainFactCollection = "code_domain_facts";
    private const string ManifestCollection   = "project_manifests";
    private const string SymEmbedCollection   = "symbol_embeddings";

    private readonly ILiteCollection<CodeIndexRecord>       _col;
    private readonly ILiteCollection<SymbolReferenceRecord> _symRefCol;
    private readonly ILiteCollection<DomainFactRecord>      _domainCol;
    private readonly ILiteCollection<ProjectManifestRecord> _manifestCol;
    private readonly ILiteCollection<SymbolEmbeddingRecord> _symEmbedCol;

    public LiteDbCodeIndexRepository(SharedLiteDatabase sharedDb)
    {
        _col         = sharedDb.Database.GetCollection<CodeIndexRecord>(CollectionName);
        _symRefCol   = sharedDb.Database.GetCollection<SymbolReferenceRecord>(SymRefCollection);
        _domainCol   = sharedDb.Database.GetCollection<DomainFactRecord>(DomainFactCollection);
        _manifestCol = sharedDb.Database.GetCollection<ProjectManifestRecord>(ManifestCollection);
        _symEmbedCol = sharedDb.Database.GetCollection<SymbolEmbeddingRecord>(SymEmbedCollection);
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
        _col.EnsureIndex(x => x.SubProjectId);
        _col.EnsureIndex(x => x.SubProjectNamespace);

        _symRefCol.EnsureIndex(x => x.DefinedInFileId);
        _symRefCol.EnsureIndex(x => x.ProjectId);
        _symRefCol.EnsureIndex(x => x.SubProjectId);
        _symRefCol.EnsureIndex(x => x.SymbolName);

        _domainCol.EnsureIndex(x => x.FileId);
        _domainCol.EnsureIndex(x => x.ProjectId);
        _domainCol.EnsureIndex(x => x.Kind);

        _manifestCol.EnsureIndex(x => x.ProjectId);

        _symEmbedCol.EnsureIndex(x => x.FileId);
        _symEmbedCol.EnsureIndex(x => x.ProjectId);
        _symEmbedCol.EnsureIndex(x => x.SubProjectId);
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

    public Task<IReadOnlyList<CodeIndexRecord>> GetBySubProjectAsync(
        string subProjectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<CodeIndexRecord>>(
            _col.Find(x => x.SubProjectId == subProjectId)
                .OrderBy(x => x.RelativePath)
                .ToList());
    }

    public Task<IReadOnlyList<(CodeIndexRecord Record, float Score)>> SearchByEmbeddingAsync(
        float[] query, string? projectId, string? subProjectId, int topN, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        IEnumerable<CodeIndexRecord> candidates = subProjectId != null
            ? _col.Find(x => x.SubProjectId == subProjectId)
            : projectId != null
                ? _col.Find(x => x.ProjectId == projectId)
                : _col.FindAll();

        var scored = candidates
            .Where(x => x.Embedding != null)
            .Select(r => (Record: r, Score: VectorMath.CosineSimilarity(query, r.Embedding!)))
            .OrderByDescending(x => x.Score)
            .Take(topN)
            .ToList();

        return Task.FromResult<IReadOnlyList<(CodeIndexRecord, float)>>(scored);
    }

    public Task<IReadOnlyList<CodeIndexRecord>> SearchLexicalAsync(
        string query, string? projectId, string? subProjectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var q = query.ToLowerInvariant();

        IEnumerable<CodeIndexRecord> candidates = subProjectId != null
            ? _col.Find(x => x.SubProjectId == subProjectId)
            : projectId != null
                ? _col.Find(x => x.ProjectId == projectId)
                : _col.FindAll();

        // Candidate filter: filename contains, exact directory segment equals, or symbol contains.
        // Path check uses exact segment equality only — no full-path substring — so a workspace
        // root folder named "agentic-memory" does NOT pull in every file for query "memory".
        //
        // Sort tiers match SearchScorer structural scores so lexical rank 1 = strongest match:
        //   Tier 0 : exact filename stem or exact symbol name      (score ≥ 0.80)
        //   Tier 1 : filename contains                             (score = 0.65)
        //   Tier 2 : parent directory exact match    (distance=1)  (score = 0.50)
        //   Tier 3 : symbol contains                               (score = 0.30)
        //   Tier 4+: grandparent+ exact match        (distance≥2)  (score < 0.25, exponential)
        static int PathDistanceTier(string relativePath, string term)
        {
            var segs = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            for (var i = segs.Length - 2; i >= 0; i--)
            {
                if (!segs[i].Equals(term, StringComparison.OrdinalIgnoreCase)) continue;
                var distance = (segs.Length - 2) - i; // 0 = parent, 1 = grandparent, …
                return 2 + distance; // tier 2 for parent, 4+ for grandparent and beyond
            }
            return int.MaxValue;
        }

        return Task.FromResult<IReadOnlyList<CodeIndexRecord>>(
            candidates
                .Where(r =>
                    r.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    PathDistanceTier(r.RelativePath, q) < int.MaxValue ||
                    (r.SymbolsText ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r =>
                {
                    var stem = Path.GetFileNameWithoutExtension(r.FileName);
                    if (stem.Equals(q, StringComparison.OrdinalIgnoreCase)) return 0;
                    if (r.Symbols.Any(s => s.Name.Equals(q, StringComparison.OrdinalIgnoreCase))) return 0;
                    if (r.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)) return 1;
                    var pathTier = PathDistanceTier(r.RelativePath, q);
                    if (pathTier == 2) return 2;    // parent dir  → ranks above symbol contains
                    if (pathTier < int.MaxValue) return pathTier + 1; // grandparent+ → after symbol contains
                    return 3;                        // symbol contains
                })
                .ToList());
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

    public Task MarkSubProjectStaleAsync(string subProjectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var r in _col.Find(x => x.SubProjectId == subProjectId).ToList())
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

    public Task<(int indexed, int stale, int errored)> GetSubProjectStatsAsync(
        string subProjectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var all = _col.Find(x => x.SubProjectId == subProjectId).ToList();
        return Task.FromResult((all.Count, all.Count(x => x.IsStale), all.Count(x => x.IngestionError != null)));
    }

    public Task<IReadOnlyList<CodeIndexRecord>> GetStaleFilesAsync(
        string projectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<CodeIndexRecord>>(
            _col.Find(x => x.ProjectId == projectId && x.IsStale)
                .OrderBy(x => x.RelativePath)
                .ToList());
    }

    public Task<IReadOnlyList<CodeIndexRecord>> GetErrorFilesAsync(
        string projectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<CodeIndexRecord>>(
            _col.Find(x => x.ProjectId == projectId && x.IngestionError != null)
                .OrderBy(x => x.RelativePath)
                .ToList());
    }

    public Task DeleteByProjectAsync(string projectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _col.DeleteMany(x => x.ProjectId == projectId);
        return Task.CompletedTask;
    }

    public Task DeleteAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _col.DeleteAll();
        return Task.CompletedTask;
    }

    // ── Symbol reference graph ────────────────────────────────────────────────

    public Task UpsertSymbolReferenceAsync(SymbolReferenceRecord record, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _symRefCol.Upsert(record);
        return Task.CompletedTask;
    }

    public Task<SymbolReferenceRecord?> GetSymbolReferenceAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<SymbolReferenceRecord?>(_symRefCol.FindById(id));
    }

    public Task<IReadOnlyList<SymbolReferenceRecord>> GetDefinedInFileAsync(string fileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SymbolReferenceRecord>>(
            _symRefCol.Find(r => r.DefinedInFileId == fileId).ToList());
    }

    public Task<IReadOnlyList<SymbolReferenceRecord>> GetUsedByFileAsync(string fileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // LiteDB cannot index into embedded arrays — filter in memory.
        var results = _symRefCol.FindAll()
            .Where(r => r.UsedBy.Any(u => u.FileId == fileId))
            .ToList();
        return Task.FromResult<IReadOnlyList<SymbolReferenceRecord>>(results);
    }

    public Task<IReadOnlyList<SymbolReferenceRecord>> SearchSymbolsAsync(
        string query, string? projectId, string? subProjectId,
        bool publicOnly = false, string[]? kinds = null,
        int minFanIn = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        IEnumerable<SymbolReferenceRecord> candidates = subProjectId != null
            ? _symRefCol.Find(r => r.SubProjectId == subProjectId)
            : projectId != null
                ? _symRefCol.Find(r => r.ProjectId == projectId)
                : _symRefCol.FindAll();

        if (!string.IsNullOrWhiteSpace(query))
            candidates = candidates.Where(r => r.SymbolName.Contains(query, StringComparison.OrdinalIgnoreCase));

        if (publicOnly)
            candidates = candidates.Where(r => r.Accessibility is "public" or "exported" or "internal");

        if (kinds?.Length > 0)
        {
            var kindSet = new HashSet<string>(kinds, StringComparer.OrdinalIgnoreCase);
            candidates = candidates.Where(r => kindSet.Contains(r.SymbolKind));
        }

        if (minFanIn > 0)
            candidates = candidates.Where(r => r.UsedBy.Count >= minFanIn);

        return Task.FromResult<IReadOnlyList<SymbolReferenceRecord>>(
            candidates.OrderByDescending(r => r.UsedBy.Count).ToList());
    }

    public Task<IReadOnlyList<SymbolReferenceRecord>> GetHotSymbolsAsync(
        string projectId, int topN = 20, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var results = _symRefCol.Find(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.UsedBy.Count)
            .Take(topN)
            .ToList();
        return Task.FromResult<IReadOnlyList<SymbolReferenceRecord>>(results);
    }

    public Task<int> CountSymbolReferencesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_symRefCol.Count());
    }

    public Task DeleteSymbolReferencesForFileAsync(string fileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Delete all records where this file defines symbols
        _symRefCol.DeleteMany(r => r.DefinedInFileId == fileId);

        // Remove this fileId from UsedBy arrays in all other records
        var affected = _symRefCol.FindAll()
            .Where(r => r.UsedBy.Any(u => u.FileId == fileId))
            .ToList();

        foreach (var rec in affected)
        {
            rec.UsedBy.RemoveAll(u => u.FileId == fileId);
            _symRefCol.Update(rec);
        }

        return Task.CompletedTask;
    }

    public Task DeleteSymbolReferencesForProjectAsync(string projectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _symRefCol.DeleteMany(r => r.ProjectId == projectId);
        return Task.CompletedTask;
    }

    public Task DeleteAllSymbolReferencesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _symRefCol.DeleteAll();
        return Task.CompletedTask;
    }

    // ── Domain facts ──────────────────────────────────────────────────────────

    public Task UpsertDomainFactsForFileAsync(
        string fileId, IReadOnlyList<DomainFactRecord> facts, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Replace-by-file: clear this file's prior facts, then insert the fresh set (idempotent re-ingest).
        _domainCol.DeleteMany(x => x.FileId == fileId);
        if (facts.Count > 0) _domainCol.InsertBulk(facts);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DomainFactRecord>> GetDomainFactsByProjectAsync(
        string projectId, string? kind = null, string? subProjectId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IEnumerable<DomainFactRecord> q = _domainCol.Find(x => x.ProjectId == projectId);
        if (!string.IsNullOrEmpty(subProjectId)) q = q.Where(x => x.SubProjectId == subProjectId);
        if (!string.IsNullOrEmpty(kind))         q = q.Where(x => x.Kind == kind);
        return Task.FromResult<IReadOnlyList<DomainFactRecord>>(
            q.OrderBy(x => x.Kind).ThenBy(x => x.Route ?? x.Name).ToList());
    }

    public Task<IReadOnlyList<DomainFactRecord>> GetDomainFactsByFileAsync(
        string fileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DomainFactRecord>>(
            _domainCol.Find(x => x.FileId == fileId).ToList());
    }

    public Task DeleteDomainFactsForFileAsync(string fileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _domainCol.DeleteMany(x => x.FileId == fileId);
        return Task.CompletedTask;
    }

    public Task DeleteDomainFactsForProjectAsync(string projectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _domainCol.DeleteMany(x => x.ProjectId == projectId);
        return Task.CompletedTask;
    }

    public Task DeleteAllDomainFactsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _domainCol.DeleteAll();
        return Task.CompletedTask;
    }

    // ── Project manifests ─────────────────────────────────────────────────────

    public Task UpsertProjectManifestsAsync(
        string projectId, IReadOnlyList<ProjectManifestRecord> manifests, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _manifestCol.DeleteMany(x => x.ProjectId == projectId);
        if (manifests.Count > 0) _manifestCol.InsertBulk(manifests);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProjectManifestRecord>> GetProjectManifestsAsync(
        string projectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ProjectManifestRecord>>(
            _manifestCol.Find(x => x.ProjectId == projectId).ToList());
    }

    public Task DeleteProjectManifestsForProjectAsync(string projectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _manifestCol.DeleteMany(x => x.ProjectId == projectId);
        return Task.CompletedTask;
    }

    public Task DeleteAllProjectManifestsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _manifestCol.DeleteAll();
        return Task.CompletedTask;
    }

    // ── Per-symbol embeddings ─────────────────────────────────────────────────

    public Task UpsertSymbolEmbeddingsForFileAsync(
        string fileId, IReadOnlyList<SymbolEmbeddingRecord> embeddings, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _symEmbedCol.DeleteMany(x => x.FileId == fileId);
        if (embeddings.Count > 0) _symEmbedCol.InsertBulk(embeddings);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<(SymbolEmbeddingRecord Record, float Score)>> SearchSymbolEmbeddingsAsync(
        float[] query, string? projectId, string? subProjectId, int topN, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // MANDATORY prefilter before the unindexed O(d) cosine scan (the symbol set dwarfs the file set).
        IEnumerable<SymbolEmbeddingRecord> candidates = subProjectId != null
            ? _symEmbedCol.Find(x => x.SubProjectId == subProjectId)
            : projectId != null
                ? _symEmbedCol.Find(x => x.ProjectId == projectId)
                : _symEmbedCol.FindAll();

        var scored = candidates
            .Where(x => x.Vector != null && x.Dim == query.Length)
            .Select(r => (Record: r, Score: VectorMath.CosineSimilarity(query, r.Vector!)))
            .OrderByDescending(x => x.Score)
            .Take(topN)
            .ToList();

        return Task.FromResult<IReadOnlyList<(SymbolEmbeddingRecord, float)>>(scored);
    }

    public Task DeleteSymbolEmbeddingsForFileAsync(string fileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _symEmbedCol.DeleteMany(x => x.FileId == fileId);
        return Task.CompletedTask;
    }

    public Task DeleteSymbolEmbeddingsForProjectAsync(string projectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _symEmbedCol.DeleteMany(x => x.ProjectId == projectId);
        return Task.CompletedTask;
    }

    public Task DeleteAllSymbolEmbeddingsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _symEmbedCol.DeleteAll();
        return Task.CompletedTask;
    }

    public void Dispose() { /* lifetime managed by SharedLiteDatabase singleton */ }
}
