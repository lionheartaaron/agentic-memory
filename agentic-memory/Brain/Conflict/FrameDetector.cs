using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Search;
using AgenticMemory.Brain.Slots;

namespace AgenticMemory.Brain.Conflict;

/// <summary>
/// Proposes the contradiction the other two detectors are structurally blind to: two affirmative
/// statements, neither carrying a registered slot, that fill the same claim with different values.
///
/// <see cref="SupersedeGate"/> only rules where both sides assert the same registered predicate;
/// everything else it returns as "no shared structured slot". <see cref="PolarityDetector"/> only
/// fires on opposite polarity — a statement and its denial. That leaves a quadrant neither covers,
/// and it is the ordinary way people contradict themselves: "my car is a blue Corolla" against "my
/// car is a red Civic", "I work night shifts" against "I work day shifts". No slot, no negation,
/// both stored, both active, both returned — after which the companion picks one at random.
///
/// Unlike the other two, this <em>proposes and does not decide</em>, and the distinction is the
/// whole design. The shape it matches is genuinely ambiguous: "I have a dog called Salt" and "I
/// have a cat called Pepper" are the same shape as the car pair and are both true. Wording cannot
/// separate "a different value for one attribute" from "a second fact about one topic" — that needs
/// something that knows what cars and pets are. So this narrows thousands of stored memories to the
/// handful worth asking about, and the caller's model settles it. Nothing is ever superseded on this
/// evidence, and nothing is recorded as a contradiction until something has actually judged it.
///
/// The bar is set by which mistake is recoverable. Proposing a pair that turns out to be fine costs
/// one adjudication and nothing else. Missing one leaves her believing two incompatible things
/// forever, which is the failure this exists to end — but proposing indiscriminately would spend a
/// model call per candidate per write, so the prefilter has to be genuinely narrow rather than
/// merely cautious.
/// </summary>
public static class FrameDetector
{
    /// <summary>
    /// Raw cosine below which two memories are not talking about the same thing closely enough
    /// to be rival answers.
    ///
    /// Higher than <see cref="PolarityDetector.MinSimilarity"/> on purpose. That detector has to
    /// tolerate the way embeddings mangle negation — "no allergies" sits 0.50 from "allergic to
    /// bears" — and earns the slack back by requiring opposite polarity, which is a strong signal
    /// on its own. Here both sides are affirmative and the only evidence is that the two occupy the
    /// same claim, so the similarity has to carry more of the weight.
    /// </summary>
    public const double MinSimilarity = 0.80;

    /// <summary>
    /// How much of the shorter statement has to be shared before it counts as one frame.
    ///
    /// Low, and it has to be. A substitution is mostly <em>value</em> — "their car is a blue
    /// Corolla" against "their car is a red Civic" reduces to {car, blue, corolla} against {car,
    /// red, civic}, so the frame they share is one word in three. Anything at or above a half
    /// rejects the very shape this exists to catch, because the more completely the value is
    /// replaced the less of the sentence survives to overlap.
    ///
    /// Which means this is not the load-bearing test. <see cref="MinSimilarity"/> is; this only
    /// stops a long statement pairing with a short one on a single incidental word.
    /// </summary>
    public const double MinFrameOverlap = 0.25;

    /// <summary>
    /// The bar when there are no embeddings, where wording is the only evidence there is.
    ///
    /// Deliberately steep enough that most real substitutions will not clear it. Without a vector
    /// there is nothing to say the two statements are about the same thing at all, and proposing on
    /// a shared word alone would spend a model call on any two sentences that happened to rhyme.
    /// </summary>
    public const double MinFrameOverlapWithoutVector = 0.6;

    /// <summary>
    /// True when the two look like one value substituted for another, and something should be
    /// asked to decide whether they actually conflict.
    /// </summary>
    /// <param name="similarity">Raw cosine between the two, or null when no vector was available.</param>
    /// <param name="frame">The words the two statements share — what the disagreement is about.</param>
    public static bool IsSubstitutionCandidate(
        MemoryNodeEntity incoming, MemoryNodeEntity existing,
        PolarityProfile incomingProfile, PolarityProfile existingProfile,
        double? similarity, out string frame)
    {
        frame = "";

        if (incoming.Id == existing.Id) return false;
        if (!string.Equals(incoming.UserId, existing.UserId, StringComparison.Ordinal)) return false;

        // Same subject and same audience, or the two are not rival answers to anything. A fact
        // about the user and a fact about their sister may read almost identically.
        if (!string.Equals(incoming.SubjectRef, existing.SubjectRef, StringComparison.OrdinalIgnoreCase)) return false;
        if (incoming.Visibility != existing.Visibility) return false;
        if (!incoming.CompanionIds.ToHashSet(StringComparer.Ordinal).SetEquals(existing.CompanionIds)) return false;

        // Opposite polarity is the other detector's case. Leaving it out is not tidiness: a
        // statement and its denial would otherwise be recorded twice under two different kinds.
        if (incomingProfile.Negated != existingProfile.Negated) return false;

        // A registered slot on both sides means the gate had jurisdiction and has already ruled —
        // including its deliberate finding that a multi-valued slot coexists. A second pet does not
        // delete the first, and this must not be the thing that quietly reopens that.
        if (SlotRegistry.Normalize(incoming.Predicate) is not null &&
            SlotRegistry.Normalize(existing.Predicate) is not null) return false;

        var a = incomingProfile.Skeleton;
        var b = existingProfile.Skeleton;
        if (a.Count == 0 || b.Count == 0) return false;

        // What both say: the frame. Topic words are taken out because the slot name and the subject
        // are shared by every memory about this person, and counting them would manufacture an
        // overlap between two statements that have nothing else in common.
        var shared = new HashSet<string>(a, StringComparer.Ordinal);
        shared.IntersectWith(b);
        shared.ExceptWith(incomingProfile.Topic);
        shared.ExceptWith(existingProfile.Topic);
        shared.ExceptWith(TextAnalysis.UniversalPlaceholders);
        if (shared.Count == 0) return false;

        // What only one of them says: the values. A substitution has one on *both* sides. When one
        // skeleton contains the other this is elaboration rather than disagreement — "my car is
        // blue" beside "my car is a blue Corolla with alloys" — and there is nothing to adjudicate.
        var incomingOnly = Only(a, b, incomingProfile);
        var existingOnly = Only(b, a, existingProfile);
        if (incomingOnly.Count == 0 || existingOnly.Count == 0) return false;

        var overlap = (double)shared.Count / Math.Min(a.Count, b.Count);
        if (overlap < MinFrameOverlap) return false;

        // Where embeddings exist they decide whether this is one claim at all. Where they do not,
        // the wording is the whole evidence and has to clear a higher bar on its own — the same
        // trade the polarity detector makes, for the same reason.
        if (similarity is { } cosine)
        {
            if (cosine < MinSimilarity) return false;
        }
        else if (overlap < MinFrameOverlapWithoutVector)
        {
            return false;
        }

        frame = string.Join(" ", shared.OrderBy(w => w, StringComparer.Ordinal));
        return true;
    }

    /// <summary>Content words on one side and not the other, less anything that names the topic.</summary>
    private static HashSet<string> Only(
        HashSet<string> mine, HashSet<string> theirs, PolarityProfile profile)
    {
        var only = new HashSet<string>(mine, StringComparer.Ordinal);
        only.ExceptWith(theirs);
        only.ExceptWith(profile.Topic);
        only.ExceptWith(TextAnalysis.UniversalPlaceholders);
        return only;
    }
}
