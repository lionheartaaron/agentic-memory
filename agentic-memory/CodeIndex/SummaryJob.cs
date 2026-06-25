namespace AgenticMemory.CodeIndex;

public record SummaryJob(string RecordId, string FilePath);

public interface ISummaryQueue
{
    bool TryEnqueue(SummaryJob job);
}
