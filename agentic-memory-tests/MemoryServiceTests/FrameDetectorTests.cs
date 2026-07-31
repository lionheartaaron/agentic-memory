using AgenticMemory.Brain.Conflict;
using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Slots;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// The substitution prefilter.
///
/// The rule under test: this <em>proposes</em> and does not decide. The slot gate rules where a
/// registered predicate is on both sides and the polarity detector rules where one side denies the
/// other; between them sits the ordinary contradiction — two affirmative unslotted statements
/// filling one claim with different values — which nothing caught before and which coexisted
/// forever, both retrievable, leaving the companion to pick one at random.
///
/// So the bar here is deliberately not "is this a contradiction". It is "could this be one, closely
/// enough to be worth a model call". <see cref="ProposesThePairItCannotItselfDecide"/> is the test
/// that pins that distinction, and it asserts the detector fires on a pair that is *not* a
/// contradiction at all — because separating those two needs something that knows a person has one
/// car and can have two pets, and wording does not.
/// </summary>
public class FrameDetectorTests
{
    private const string User = "aaron";

    /// <summary>Cosine comfortably above the floor, for the cases not testing similarity.</summary>
    private const double Close = 0.9;

    private static MemoryNodeEntity Fact(
        string title, string summary, string? predicate = null, string? subject = null) =>
        new()
        {
            Id             = Guid.NewGuid(),
            UserId         = User,
            Title          = title,
            Summary        = summary,
            Content        = "",
            Tags           = [],
            Importance     = 0.5,
            Visibility     = MemoryVisibility.Global,
            CompanionIds   = [],
            SubjectRef     = SubjectRefs.Normalize(subject),
            Predicate      = SlotRegistry.Normalize(predicate),
            Type           = MemoryType.Semantic,
            Source         = MemorySource.UserStated,
            CreatedAt      = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            State          = MemoryState.Active,
        };

    private static bool Proposes(
        MemoryNodeEntity incoming, MemoryNodeEntity existing, double? similarity = Close) =>
        FrameDetector.IsSubstitutionCandidate(
            incoming, existing,
            PolarityProfile.For(incoming), PolarityProfile.For(existing),
            similarity, out _);

    // ── What it is for ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProposesOneClaimFilledTwoWays()
    {
        Assert.True(Proposes(
            Fact("Car", "Their car is a red Civic"),
            Fact("Car", "Their car is a blue Corolla")));
    }

    /// <summary>
    /// The frame the two share is one word in three here — the more completely a value is replaced
    /// the less of the sentence survives to overlap — which is why the overlap floor is a quarter
    /// and not a half. A stricter one rejects the case this whole file exists for.
    /// </summary>
    [Fact]
    public void ProposesEvenWhenAlmostAllTheWordingDiffers()
    {
        Assert.True(Proposes(
            Fact("Shift", "They work night shifts"),
            Fact("Shift", "They work day shifts")));
    }

    /// <summary>
    /// Fires on a pair that is not a contradiction, and that is correct.
    ///
    /// Two pets are the same shape in wording as one contested car. A prefilter that could tell
    /// them apart would not need an adjudicator, and one that refused to propose this would also
    /// refuse the car — the shapes are identical. Deciding is the model's job; this only narrows.
    /// </summary>
    [Fact]
    public void ProposesThePairItCannotItselfDecide()
    {
        Assert.True(Proposes(
            Fact("Pet", "They have a cat called Pepper"),
            Fact("Pet", "They have a dog called Salt")));
    }

    // ── What it must leave alone ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One statement containing the other is elaboration, not disagreement. Nothing to adjudicate,
    /// and treating it as a contradiction would ask somebody to choose between a fact and a fuller
    /// version of the same fact.
    /// </summary>
    [Fact]
    public void IgnoresElaborationOfTheSameFact()
    {
        Assert.False(Proposes(
            Fact("Car", "Their car is a blue Corolla with alloy wheels"),
            Fact("Car", "Their car is blue")));
    }

    /// <summary>Opposite polarity belongs to the polarity detector. Catching it here too would
    /// record one disagreement twice, under two different kinds.</summary>
    [Fact]
    public void LeavesDenialsToThePolarityDetector()
    {
        Assert.False(Proposes(
            Fact("Coffee", "They do not drink coffee in the morning"),
            Fact("Coffee", "They drink coffee in the morning")));
    }

    /// <summary>
    /// A registered slot on both sides means the gate already ruled — including its deliberate
    /// finding that a multi-valued slot coexists. A second pet does not delete the first, and this
    /// must not be what quietly reopens that.
    /// </summary>
    [Fact]
    public void LeavesSlottedPairsToTheGate()
    {
        Assert.False(Proposes(
            Fact("Pets", "They have a cat called Pepper", predicate: "pets"),
            Fact("Pets", "They have a dog called Salt",   predicate: "pets")));
    }

    /// <summary>Two facts about different people can read almost identically.</summary>
    [Fact]
    public void IgnoresDifferentSubjects()
    {
        Assert.False(Proposes(
            Fact("Car", "Their car is a red Civic",     subject: "person:mika"),
            Fact("Car", "Their car is a blue Corolla",  subject: "user")));
    }

    /// <summary>Similarity is the load-bearing test: below the floor these are not rival answers
    /// to anything, whatever words they happen to share.</summary>
    [Fact]
    public void IgnoresPairsThatAreNotAboutTheSameThing()
    {
        Assert.False(Proposes(
            Fact("Car", "Their car is a red Civic"),
            Fact("Car", "Their car is a blue Corolla"),
            similarity: 0.4));
    }

    /// <summary>
    /// With no vector there is nothing to say the two are about the same thing, so wording alone
    /// has to clear a much steeper bar — steep enough that a real substitution usually will not.
    /// That is the intended trade: no embeddings, almost no substitution detection, rather than
    /// proposing on any two sentences that happen to share a word.
    /// </summary>
    [Fact]
    public void IsNearlySilentWithoutEmbeddings()
    {
        Assert.False(Proposes(
            Fact("Car", "Their car is a red Civic"),
            Fact("Car", "Their car is a blue Corolla"),
            similarity: null));
    }
}
