using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Search;

namespace AgenticMemory.Brain.Conflict;

/// <summary>
/// Catches the one contradiction the slot gate structurally cannot see: a statement and its denial.
///
/// <see cref="SupersedeGate"/> reasons about structured slots, so it only fires when both memories
/// assert the same registered predicate. "The user drinks coffee every morning" and "the user has
/// stopped drinking coffee" usually carry no predicate at all, and are therefore filed as two
/// unrelated facts that both stay active — after which retrieval faithfully returns both and the
/// companion has to pick one at random. That is exactly the "conflicting info" failure, arriving
/// through the door the slot registry does not cover.
///
/// The test is deliberately narrow, because a false positive here is worse than a miss: it would
/// raise a contradiction between two memories that merely discuss the same subject. Three conditions
/// must hold together — near-identical embeddings, near-identical wording once polarity words are
/// stripped, and opposite polarity. Nothing is ever superseded on this evidence; the contradiction is
/// recorded for a human or the companion to resolve.
/// </summary>
public static class PolarityDetector
{
    /// <summary>Raw cosine below which two memories are not close enough to be the same claim.</summary>
    public const double MinSimilarity = 0.70;

    /// <summary>Token overlap required between the two polarity-free skeletons.</summary>
    public const double MinSkeletonOverlap = 0.55;

    /// <summary>
    /// Returns true when <paramref name="incoming"/> denies what <paramref name="existing"/> asserts,
    /// or vice versa.
    /// </summary>
    /// <param name="similarity">Raw cosine between the two, or null when no vector was available.</param>
    public static bool IsPolarityContradiction(
        MemoryNodeEntity incoming, MemoryNodeEntity existing, double? similarity, out string reason)
    {
        reason = "";

        if (incoming.Id == existing.Id) return false;
        if (!string.Equals(incoming.UserId, existing.UserId, StringComparison.Ordinal)) return false;

        // Same subject and same audience, or the two are not talking about the same thing at all.
        if (!string.Equals(incoming.SubjectRef, existing.SubjectRef, StringComparison.OrdinalIgnoreCase)) return false;
        if (incoming.Visibility != existing.Visibility) return false;
        if (!incoming.CompanionIds.ToHashSet(StringComparer.Ordinal).SetEquals(existing.CompanionIds)) return false;

        // Don't second-guess the slot gate where it already has jurisdiction.
        var incomingSlot = incoming.Predicate;
        var existingSlot = existing.Predicate;
        if (incomingSlot is not null && existingSlot is not null &&
            !string.Equals(incomingSlot, existingSlot, StringComparison.OrdinalIgnoreCase))
            return false;

        var incomingText = Statement(incoming);
        var existingText = Statement(existing);

        var incomingNegated = TextAnalysis.ContainsNegation(incomingText);
        var existingNegated = TextAnalysis.ContainsNegation(existingText);

        // Opposite polarity, not merely "one of them mentions 'not'".
        if (incomingNegated == existingNegated) return false;

        // Same claim underneath: strip the polarity words and the sentences must be near-identical.
        var a = TextAnalysis.ContentSkeleton(incomingText).ToHashSet(StringComparer.Ordinal);
        var b = TextAnalysis.ContentSkeleton(existingText).ToHashSet(StringComparer.Ordinal);
        if (a.Count == 0 || b.Count == 0) return false;

        var overlap = (double)a.Intersect(b, StringComparer.Ordinal).Count() / Math.Min(a.Count, b.Count);
        if (overlap < MinSkeletonOverlap) return false;

        // Where embeddings exist they must agree. Where they do not, the wording test above is the
        // whole evidence, so it has to carry a higher bar on its own.
        if (similarity is { } cosine)
        {
            if (cosine < MinSimilarity) return false;
        }
        else if (overlap < 0.75)
        {
            return false;
        }

        var affirmed = incomingNegated ? existing : incoming;
        var denied   = incomingNegated ? incoming : existing;

        reason = $"'{denied.Title}' denies what '{affirmed.Title}' asserts";
        return true;
    }

    private static string Statement(MemoryNodeEntity memory) =>
        string.IsNullOrWhiteSpace(memory.Summary) ? memory.Title : $"{memory.Title}. {memory.Summary}";
}
