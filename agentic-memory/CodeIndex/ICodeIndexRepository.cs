namespace AgenticMemory.CodeIndex;

public interface ICodeIndexRepository : IDisposable
{
    Task UpsertAsync(CodeIndexRecord record, CancellationToken ct = default);
    Task<CodeIndexRecord?> GetByPathAsync(string filePath, CancellationToken ct = default);
    Task<IReadOnlyList<CodeIndexRecord>> GetByProjectAsync(string projectId, CancellationToken ct = default);
    Task<IReadOnlyList<CodeIndexRecord>> GetBySubProjectAsync(string subProjectId, CancellationToken ct = default);
    Task<IReadOnlyList<(CodeIndexRecord Record, float Score)>> SearchByEmbeddingAsync(
        float[] query, string? projectId, string? subProjectId, int topN, CancellationToken ct = default);
    Task<IReadOnlyList<CodeIndexRecord>> SearchLexicalAsync(
        string query, string? projectId, string? subProjectId, CancellationToken ct = default);
    Task<IReadOnlyList<CodeIndexRecord>> GetByIdsAsync(
        IReadOnlyList<string> ids, CancellationToken ct = default);
    Task MarkProjectStaleAsync(string projectId, CancellationToken ct = default);
    Task MarkSubProjectStaleAsync(string subProjectId, CancellationToken ct = default);
    Task DeleteByPathAsync(string filePath, CancellationToken ct = default);
    Task<int> CountAsync(string projectId, CancellationToken ct = default);
    Task<(int indexed, int stale, int errored)> GetProjectStatsAsync(string projectId, CancellationToken ct = default);
    Task<(int indexed, int stale, int errored)> GetSubProjectStatsAsync(string subProjectId, CancellationToken ct = default);
    Task<IReadOnlyList<CodeIndexRecord>> GetStaleFilesAsync(string projectId, CancellationToken ct = default);
    Task<IReadOnlyList<CodeIndexRecord>> GetErrorFilesAsync(string projectId, CancellationToken ct = default);
    Task DeleteByProjectAsync(string projectId, CancellationToken ct = default);
    Task DeleteAllAsync(CancellationToken ct = default);

    // ── Symbol reference graph ────────────────────────────────────────────────
    Task UpsertSymbolReferenceAsync(SymbolReferenceRecord record, CancellationToken ct = default);
    Task<SymbolReferenceRecord?> GetSymbolReferenceAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<SymbolReferenceRecord>> GetDefinedInFileAsync(string fileId, CancellationToken ct = default);
    Task<IReadOnlyList<SymbolReferenceRecord>> GetUsedByFileAsync(string fileId, CancellationToken ct = default);
    Task<IReadOnlyList<SymbolReferenceRecord>> SearchSymbolsAsync(
        string query, string? projectId, string? subProjectId,
        bool publicOnly = false, string[]? kinds = null,
        int minFanIn = 0, CancellationToken ct = default);
    Task<IReadOnlyList<SymbolReferenceRecord>> GetHotSymbolsAsync(
        string projectId, int topN = 20, CancellationToken ct = default);
    Task<int> CountSymbolReferencesAsync(CancellationToken ct = default);
    Task DeleteSymbolReferencesForFileAsync(string fileId, CancellationToken ct = default);
    Task DeleteSymbolReferencesForProjectAsync(string projectId, CancellationToken ct = default);
    Task DeleteAllSymbolReferencesAsync(CancellationToken ct = default);

    // ── Domain facts (routes / DI / EF / TanStack / navigation — P1 promotion) ─
    Task UpsertDomainFactsForFileAsync(
        string fileId, IReadOnlyList<DomainFactRecord> facts, CancellationToken ct = default);
    Task<IReadOnlyList<DomainFactRecord>> GetDomainFactsByProjectAsync(
        string projectId, string? kind = null, string? subProjectId = null, CancellationToken ct = default);
    Task<IReadOnlyList<DomainFactRecord>> GetDomainFactsByFileAsync(
        string fileId, CancellationToken ct = default);
    Task DeleteDomainFactsForFileAsync(string fileId, CancellationToken ct = default);
    Task DeleteDomainFactsForProjectAsync(string projectId, CancellationToken ct = default);
    Task DeleteAllDomainFactsAsync(CancellationToken ct = default);

    // ── Project manifests (build/dependency graph — P0 / §4.1) ────────────────
    Task UpsertProjectManifestsAsync(
        string projectId, IReadOnlyList<ProjectManifestRecord> manifests, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectManifestRecord>> GetProjectManifestsAsync(
        string projectId, CancellationToken ct = default);
    Task DeleteProjectManifestsForProjectAsync(string projectId, CancellationToken ct = default);
    Task DeleteAllProjectManifestsAsync(CancellationToken ct = default);

    // ── Per-symbol embeddings (P3) ────────────────────────────────────────────
    Task UpsertSymbolEmbeddingsForFileAsync(
        string fileId, IReadOnlyList<SymbolEmbeddingRecord> embeddings, CancellationToken ct = default);
    Task<IReadOnlyList<(SymbolEmbeddingRecord Record, float Score)>> SearchSymbolEmbeddingsAsync(
        float[] query, string? projectId, string? subProjectId, int topN, CancellationToken ct = default);
    Task DeleteSymbolEmbeddingsForFileAsync(string fileId, CancellationToken ct = default);
    Task DeleteSymbolEmbeddingsForProjectAsync(string projectId, CancellationToken ct = default);
    Task DeleteAllSymbolEmbeddingsAsync(CancellationToken ct = default);
}
