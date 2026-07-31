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
/// The ordinary test is deliberately narrow, because a false positive here is worse than a miss: it
/// would raise a contradiction between two memories that merely discuss the same subject. Three
/// conditions must hold together — near-identical embeddings, near-identical wording once polarity
/// words are stripped, and opposite polarity. Nothing is ever superseded on this evidence; the
/// contradiction is recorded for a human or the companion to resolve.
///
/// One case is exempt from the similarity half of that, and has to be. A statement that a set is
/// empty shares almost no wording with a statement naming a member of it, and sentence embeddings
/// are actively misleading about negation: "no allergies" measured 0.50 against "allergic to bears",
/// further apart than plenty of pairs with nothing in common. Demanding that the two look alike is
/// what let "I am not allergic to anything" and "I am allergic to bears" coexist as unrelated facts,
/// so <see cref="AssertsEmptySet"/> decides that case on meaning instead. It is the one shape where
/// a replacement is safe rather than merely likely, because the memory being replaced asserted that
/// there was nothing to lose.
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

        var affirmed = incomingNegated ? existing : incoming;
        var denied   = incomingNegated ? incoming : existing;

        // A claim that the set is empty is contradicted by any single value in it, and needs no
        // similarity evidence at all: "not allergic to anything" and "allergic to bears" share only
        // the word "allergic", and their embeddings measured 0.50 apart — further than many pairs
        // that have nothing to do with each other. Requiring the two to look alike is exactly what
        // let this pair through as unrelated facts.
        if (AssertsEmptySet(denied, affirmed))
        {
            reason = "one side claims there are none at all, which the other contradicts by naming one";
            return true;
        }

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

        reason = $"'{denied.Title}' denies what '{affirmed.Title}' asserts";
        return true;
    }

    /// <summary>
    /// True when <paramref name="memory"/> claims a set is empty rather than denying one value:
    /// "no allergies", "not allergic to anything", "no known allergies".
    ///
    /// Judged against <paramref name="other"/>, because emptiness is only meaningful relative to the
    /// set in question and the two statements between them are the only description of it available.
    /// Whatever they share is the topic; anything left over on this side has to be a placeholder, or
    /// this is an ordinary denial of a specific value and must not be treated as a retraction.
    /// </summary>
    public static bool AssertsEmptySet(MemoryNodeEntity memory, MemoryNodeEntity other)
    {
        var statement = Statement(memory);
        if (!TextAnalysis.ContainsNegation(statement)) return false;

        // No content words at all is not a claim that the set is empty, it is a memory that says
        // nothing. Without this the test passes vacuously and any value would supersede it.
        var skeleton = TextAnalysis.ContentSkeleton(statement);
        if (skeleton.Count == 0) return false;

        var topic = TextAnalysis.ContentSkeleton(Statement(other)).ToHashSet(StringComparer.Ordinal);
        topic.UnionWith(TopicWords(memory));
        topic.UnionWith(TopicWords(other));

        return TextAnalysis.NamesNoSpecificValue(skeleton, topic);
    }

    /// <summary>The slot and subject name the topic too, in words the sentences may never use.</summary>
    private static IEnumerable<string> TopicWords(MemoryNodeEntity memory) =>
        TextAnalysis.ContentSkeleton(
            $"{memory.Predicate?.Replace('_', ' ')} {memory.SubjectRef?.Replace(':', ' ')}");

    /// <summary>The sentence a memory asserts: its title and summary read as one claim.</summary>
    public static string Statement(MemoryNodeEntity memory) =>
        string.IsNullOrWhiteSpace(memory.Summary) ? memory.Title : $"{memory.Title}. {memory.Summary}";
}

/// <summary>
/// One memory reduced to what the topic test needs, tokenized once.
///
/// It exists because the incoming memory is compared against every active memory in scope, and
/// re-deriving its own skeleton inside that loop makes a write cost two tokenizations per memory
/// the user has ever stored rather than one.
/// </summary>
public sealed class PolarityProfile
{
    private PolarityProfile(bool negated, HashSet<string> skeleton, HashSet<string> topic)
    {
        Negated  = negated;
        Skeleton = skeleton;
        Topic    = topic;
    }

    public bool Negated { get; }

    /// <summary>Content words with the polarity markers taken out.</summary>
    public HashSet<string> Skeleton { get; }

    /// <summary>What the slot and subject call the topic, in words the sentence may never use.</summary>
    public HashSet<string> Topic { get; }

    public static PolarityProfile For(MemoryNodeEntity memory)
    {
        var statement = PolarityDetector.Statement(memory);

        return new PolarityProfile(
            TextAnalysis.ContainsNegation(statement),
            TextAnalysis.ContentSkeleton(statement).ToHashSet(StringComparer.Ordinal),
            TextAnalysis.ContentSkeleton(
                $"{memory.Predicate?.Replace('_', ' ')} {memory.SubjectRef?.Replace(':', ' ')}")
                .ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Whether a pair is worth comparing at all: opposite polarity, and at least one content word in
    /// common beyond the subject that every memory in the store already shares.
    ///
    /// Deliberately looser than <see cref="PolarityDetector.IsPolarityContradiction"/>, which makes
    /// the decision. It exists because the alternative — proposing candidates by embedding distance —
    /// cannot see polarity pairs at all: "no allergies" measured 0.50 against "allergic to bears",
    /// further apart than many pairs with nothing to do with each other, so the one memory the new
    /// fact contradicted was reliably filtered out before anything compared them.
    /// </summary>
    public bool SharesTopicWithOppositePolarity(PolarityProfile other)
    {
        if (Negated == other.Negated) return false;

        var shared = Skeleton.ToHashSet(StringComparer.Ordinal);
        shared.IntersectWith(other.Skeleton);
        shared.ExceptWith(Topic);
        shared.ExceptWith(other.Topic);
        shared.ExceptWith(TextAnalysis.UniversalPlaceholders);

        return shared.Count > 0;
    }
}
