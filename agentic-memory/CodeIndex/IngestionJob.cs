namespace AgenticMemory.CodeIndex;

public record IngestionJob(
    string FilePath,
    string ProjectId,        // WorkspaceId
    string? ProjectRoot,     // Workspace root — kept as provider registration context
    bool Force,
    string? SubProjectId   = null,
    string? SubProjectRoot = null);

public interface IIngestionQueue
{
    bool TryEnqueue(IngestionJob job);
    int Depth { get; }
    void Clear();
}
