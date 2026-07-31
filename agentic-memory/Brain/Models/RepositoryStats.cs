namespace AgenticMemory.Brain.Models;

public record RepositoryStats
{
    /// <summary>Every memory in scope, in any state.</summary>
    public int TotalNodes { get; init; }

    public int ActiveNodes { get; init; }
    public int SupersededNodes { get; init; }
    public int ArchivedNodes { get; init; }

    /// <summary>Tombstoned by user request, awaiting purge.</summary>
    public int ForgottenNodes { get; init; }

    /// <summary>Averaged over active memories only.</summary>
    public double AverageStrength { get; init; }

    /// <summary>Low-strength memories. Informational only — strength no longer affects retention.</summary>
    public int WeakMemoriesCount { get; init; }

    /// <summary>Contradictions awaiting a decision.</summary>
    public int OpenConflicts { get; init; }

    public DateTime? OldestMemory { get; init; }
    public DateTime? NewestMemory { get; init; }
    public long DatabaseSizeBytes { get; init; }
}
