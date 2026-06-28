namespace AgenticMemory.Brain.Models;

public record ScoredMemory
{
    public required MemoryNodeEntity Memory { get; init; }
    public double Score { get; init; }
    public double FuzzyScore { get; init; }
    public double StrengthScore { get; init; }
    public double RecencyScore { get; init; }
    public double SemanticScore { get; init; }
}
