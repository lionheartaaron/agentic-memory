namespace AgenticMemory.CodeIndex;

public record IngestionJob(
    string FilePath,
    string ProjectId,
    string? ProjectRoot = null,
    bool Force = false);

public interface IIngestionQueue
{
    bool TryEnqueue(IngestionJob job);
    int Depth { get; }
    void Clear();
}
