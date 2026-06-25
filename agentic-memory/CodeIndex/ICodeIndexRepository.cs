namespace AgenticMemory.CodeIndex;

public interface ICodeIndexRepository : IDisposable
{
    Task UpsertAsync(CodeIndexRecord record, CancellationToken ct = default);
    Task<CodeIndexRecord?> GetByPathAsync(string filePath, CancellationToken ct = default);
    Task<IReadOnlyList<CodeIndexRecord>> GetByProjectAsync(string projectId, CancellationToken ct = default);
    Task<IReadOnlyList<(CodeIndexRecord Record, float Score)>> SearchByEmbeddingAsync(
        float[] query, string? projectId, int topN, CancellationToken ct = default);
    Task<IReadOnlyList<CodeIndexRecord>> SearchLexicalAsync(
        string query, string? projectId, CancellationToken ct = default);
    Task<IReadOnlyList<CodeIndexRecord>> GetByIdsAsync(
        IReadOnlyList<string> ids, CancellationToken ct = default);
    Task MarkProjectStaleAsync(string projectId, CancellationToken ct = default);
    Task DeleteByPathAsync(string filePath, CancellationToken ct = default);
    Task<int> CountAsync(string projectId, CancellationToken ct = default);
    Task<(int indexed, int stale, int errored)> GetProjectStatsAsync(string projectId, CancellationToken ct = default);
}
