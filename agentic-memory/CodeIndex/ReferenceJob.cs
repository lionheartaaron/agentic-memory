namespace AgenticMemory.CodeIndex;

public record ReferenceJob(
    string FileId,
    string FilePath,
    string ProjectId,
    string? ProjectRoot  = null,
    string? SubProjectId = null,
    bool   IsDelete      = false);

public interface IReferenceQueue
{
    bool TryEnqueue(ReferenceJob job);
    bool TryEnqueueDelete(string fileId);   // tombstone — worker scrubs UsedBy arrays
    int  Depth { get; }
    void Clear();
}
