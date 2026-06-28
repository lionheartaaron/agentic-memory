namespace AgenticMemory.Brain.Models;

public record RepositoryStats
{
    public int TotalNodes { get; init; }
    public double AverageStrength { get; init; }
    public int WeakMemoriesCount { get; init; }
    public DateTime? OldestMemory { get; init; }
    public DateTime? NewestMemory { get; init; }
    public long DatabaseSizeBytes { get; init; }
}
