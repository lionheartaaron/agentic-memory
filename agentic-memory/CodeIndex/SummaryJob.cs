namespace AgenticMemory.CodeIndex;

public record SummaryJob(string RecordId, string FilePath, string? RelativePath = null);

public interface ISummaryQueue
{
    bool TryEnqueue(SummaryJob job);
}
