namespace AgenticMemory.Brain.Models;

public record StoreResult
{
    public required MemoryNodeEntity Memory { get; init; }
    public required StoreAction Action { get; init; }

    /// <summary>Memories this one legally replaced. They are retained as history, not deleted.</summary>
    public IReadOnlyList<MemoryNodeEntity> SupersededMemories { get; init; } = [];

    /// <summary>Contradictions recorded rather than resolved. Both sides stay active.</summary>
    public IReadOnlyList<MemoryConflict> Conflicts { get; init; } = [];

    /// <summary>
    /// Pairs that look like one value replacing another, which nothing here is able to judge.
    ///
    /// Not conflicts — nothing has decided these disagree, and they are deliberately not recorded
    /// as such. Wording alone cannot tell "my car is a blue Corolla" against "my car is a red
    /// Civic" (one car, two claims) from "a dog called Salt" against "a cat called Pepper" (two
    /// pets, both true), and recording the second as a contradiction would have her querying
    /// something the user never got wrong.
    ///
    /// So they are handed back for the caller to settle with whatever brain it already has, and
    /// posted back to <c>POST /api/memory/conflicts</c> if the answer is yes. A caller that ignores
    /// this list gets exactly the old behaviour: both memories active, no contradiction raised.
    /// </summary>
    public IReadOnlyList<ContradictionCandidate> ContradictionCandidates { get; init; } = [];

    public required string Message { get; init; }
}

/// <summary>
/// Two memories that occupy the same claim with different values, as handed to whoever will decide
/// whether that is a contradiction.
///
/// Carries both statements in full rather than ids alone, because the decision is about the words:
/// a caller given two guids would have to fetch both back before it could ask anything, and the
/// question is answerable from the sentences by themselves.
/// </summary>
/// <param name="Frame">The words the two share — what the disagreement is about, if it is one.</param>
public sealed record ContradictionCandidate(
    Guid ExistingMemoryId,
    string ExistingStatement,
    Guid NewMemoryId,
    string NewStatement,
    double Similarity,
    string Frame);

public enum StoreAction
{
    StoredNew,
    StoredWithSupersede,
    ReinforcedExisting,
    StoredCoexist,

    /// <summary>Stored, and a contradiction was recorded for a companion or the user to settle.</summary>
    StoredWithConflict,
}
