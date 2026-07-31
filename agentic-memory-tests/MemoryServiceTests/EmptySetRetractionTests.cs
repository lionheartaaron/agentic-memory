using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Retrieval;
using AgenticMemory.Tools;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// The user says he has none of something, then names one.
///
/// This is the shape that produced the worst answer the system can give — a confident, current,
/// factually inverted one. "I am not allergic to anything", then "I am allergic to bears", then
/// "what am I allergic to" answered with "nothing". Three separate things had to be wrong for that
/// to happen and each is covered here:
///
///   * <c>allergies</c> is a multi-valued slot, so the gate filed "none" as one more value in the
///     set instead of the claim that the set is empty, and refused to supersede it.
///   * The pair was often never compared at all. Candidates were proposed by embedding distance,
///     and a denial does not sit near what it denies — "no allergies" measured 0.50 against
///     "allergic to bears", below the candidate floor and further away than most unrelated pairs.
///   * Both memories came back from retrieval scoring 1.00, carrying no date and no sign that the
///     other existed, so which one answered the question was down to reading order.
///
/// The second half of the file is the other side of the same rule: denying one value is not the
/// same as claiming there are none, and must never license a replacement.
/// </summary>
public class EmptySetRetractionTests : MemoryServiceTestBase
{
    private const string User = "aaron";

    private static MemoryScope UserScope => MemoryScope.AllFor(User);

    private async Task<StoreResult> StoreAsync(
        string title, string summary,
        string? predicate = null, string? value = null,
        MemoryVisibility visibility = MemoryVisibility.Global, string? companionId = null)
    {
        var memory = CreateTestMemory(
            title, summary, content: "", userId: User,
            predicate: predicate, value: value, visibility: visibility, companionId: companionId);

        return await ConflictStorage.StoreAsync(memory, UserScope, "test", Ct);
    }

    private Task<MemoryRetrievalResult> RecallAsync(string query) =>
        SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = query, Scope = UserScope, TopN = 10, Reinforce = false,
        }, Ct);

    // ── The reported exchange ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole conversation, on the slotted path the store tool recommends: the retraction is
    /// superseded, and the only thing left to answer with is the allergy the user actually named.
    /// </summary>
    [Fact]
    public async Task NamingAnAllergyAfterDenyingAllOfThemAnswersWithTheAllergy()
    {
        await StoreAsync("Allergies", "The user is not allergic to anything",
            predicate: "allergies", value: "none");
        await StoreAsync("Allergies", "The user is allergic to bears",
            predicate: "allergies", value: "bears");

        var result = await RecallAsync("what am I allergic to");

        Assert.NotEmpty(result.Results);
        Assert.Contains("bears", result.Results[0].Memory.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Results, r => r.Memory.Summary.Contains("not allergic to anything"));
    }

    [Fact]
    public async Task TheDenialOfAllValuesIsSupersededRatherThanKeptAlongsideTheValue()
    {
        var denial = await StoreAsync("Allergies", "The user is not allergic to anything",
            predicate: "allergies", value: "none");
        var actual = await StoreAsync("Allergies", "The user is allergic to bears",
            predicate: "allergies", value: "bears");

        Assert.Equal(StoreAction.StoredWithSupersede, actual.Action);

        var live = await Repository.QueryAsync(UserScope, null, Ct);
        Assert.DoesNotContain(live, m => m.Id == denial.Memory.Id);
        Assert.Contains(live, m => m.Id == actual.Memory.Id);
    }

    /// <summary>Superseded, not deleted: "wasn't I fine with everything last year?" still answers.</summary>
    [Fact]
    public async Task TheSupersededDenialIsKeptAsHistory()
    {
        await StoreAsync("Allergies", "The user is not allergic to anything",
            predicate: "allergies", value: "none");
        await StoreAsync("Allergies", "The user is allergic to bears",
            predicate: "allergies", value: "bears");

        var history = await ConflictStorage.GetSlotHistoryAsync(UserScope, SubjectRefs.User, "allergies", Ct);

        Assert.Contains(history, m => m.Summary.Contains("not allergic to anything"));
        Assert.Contains(history, m => m.State == MemoryState.Superseded);
    }

    // ── The same exchange without a slot ──────────────────────────────────────────────────────

    /// <summary>
    /// Free text, which is what actually arrives when the model storing the memory does not set a
    /// predicate. Nothing may be archived on this path, so the requirement is weaker but has to
    /// hold: the contradiction is noticed, and both halves say so.
    /// </summary>
    [Theory]
    [InlineData("Allergies", "The user is not allergic to anything", "Allergies", "The user is allergic to bears")]
    [InlineData("No allergies", "The user is not allergic to anything", "Allergic to bears", "The user is allergic to bears")]
    [InlineData("Allergies", "No known allergies", "Allergies", "Allergic to bears")]
    [InlineData("No allergies", "The user has no allergies", "Bear allergy", "The user is allergic to bears")]
    public async Task AnUnslottedDenialOfAllValuesIsRecordedAsAContradiction(
        string denialTitle, string denialSummary, string valueTitle, string valueSummary)
    {
        await StoreAsync(denialTitle, denialSummary);
        await StoreAsync(valueTitle, valueSummary);

        var conflicts = await Repository.GetConflictsAsync(UserScope, openOnly: true, Ct);

        Assert.Contains(conflicts, c => c.Kind == ConflictKind.PolarityContradiction);
    }

    /// <summary>
    /// The failure that survived every other fix: two contradictory memories returned as two
    /// ordinary results, equally relevant, neither mentioning the other.
    /// </summary>
    [Fact]
    public async Task BothSidesOfAnUnslottedContradictionAreMarkedAsContested()
    {
        await StoreAsync("Allergies", "The user is not allergic to anything");
        await StoreAsync("Allergies", "The user is allergic to bears");

        var result = await RecallAsync("what am I allergic to");

        Assert.Equal(2, result.Results.Count);
        Assert.All(result.Results, r => Assert.True(r.IsContradicted));
        Assert.Contains(result.Conflicts, c => c.Kind == ConflictKind.PolarityContradiction);
    }

    /// <summary>Equal relevance must not mean arbitrary order — the correction reads first.</summary>
    [Fact]
    public async Task WhenTwoContradictingMemoriesTieOnRelevanceTheNewerOneIsListedFirst()
    {
        await StoreAsync("Allergies", "The user is not allergic to anything");
        await StoreAsync("Allergies", "The user is allergic to bears");

        var result = await RecallAsync("what am I allergic to");

        Assert.Equal(result.Results[0].Score, result.Results[1].Score, 6);
        Assert.Contains("bears", result.Results[0].Memory.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A contradiction naming both memories by the same title told the reader nothing at all —
    /// "'Allergies' contradicts 'Allergies'" is exactly the case a self-correcting user produces.
    /// </summary>
    [Fact]
    public async Task TheContradictionDescriptionQuotesBothClaimsAndSaysWhichIsNewer()
    {
        await StoreAsync("Allergies", "The user is not allergic to anything");
        await StoreAsync("Allergies", "The user is allergic to bears");

        var conflict = Assert.Single(
            await Repository.GetConflictsAsync(UserScope, openOnly: true, Ct),
            c => c.Kind == ConflictKind.PolarityContradiction);

        Assert.Contains("bears", conflict.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not allergic to anything", conflict.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("newer", conflict.Description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What the companion actually reads. The rendered result carried no timestamp, so even a
    /// correctly ordered pair of contradictory memories gave it nothing to choose between.
    /// </summary>
    [Fact]
    public async Task TheRenderedRecallCarriesTheDateAndFlagsTheContradiction()
    {
        await StoreAsync("Allergies", "The user is not allergic to anything");
        await StoreAsync("Allergies", "The user is allergic to bears");

        var tools = new MemoryTools(Repository, SearchService, EventLog, Fixture.Slots, ConflictStorage);
        var rendered = await tools.SearchMemories("what am I allergic to", User, cancellationToken: Ct);

        Assert.Contains("recorded ", rendered, StringComparison.Ordinal);
        Assert.Contains($"{DateTime.UtcNow:yyyy-MM-dd}", rendered, StringComparison.Ordinal);
        Assert.Contains("contradicts this one", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prefer the more recently recorded", rendered, StringComparison.OrdinalIgnoreCase);
    }

    // ── Denying one value is not claiming there are none ──────────────────────────────────────

    /// <summary>
    /// The rule that keeps the one above safe. "Not allergic to bears" names a value and denies only
    /// that value; the rest of the set is untouched, and a later allergy must not archive it.
    /// </summary>
    [Fact]
    public async Task DenyingOneValueDoesNotLicenseReplacingIt()
    {
        var denial = await StoreAsync("Allergies", "The user is not allergic to bears",
            predicate: "allergies", value: "not bears");
        await StoreAsync("Allergies", "The user is allergic to peanuts",
            predicate: "allergies", value: "peanuts");

        var live = await Repository.QueryAsync(UserScope, null, Ct);
        Assert.Contains(live, m => m.Id == denial.Memory.Id);
    }

    /// <summary>A second allergy is a second allergy. The multi-valued rule still governs.</summary>
    [Fact]
    public async Task ASecondValueOnAMultiValuedSlotStillCoexists()
    {
        var bears = await StoreAsync("Allergies", "The user is allergic to bears",
            predicate: "allergies", value: "bears");
        await StoreAsync("Allergies", "The user is allergic to peanuts",
            predicate: "allergies", value: "peanuts");

        var live = await Repository.QueryAsync(UserScope, null, Ct);
        Assert.Contains(live, m => m.Id == bears.Memory.Id);
    }

    /// <summary>
    /// The reverse direction. Going from a recorded allergy back to "I have none" retracts something
    /// real on a slot marked never-auto-remove, and a heuristic must not be trusted to do that.
    /// </summary>
    [Fact]
    public async Task RetractingARecordedValueBackToNoneIsNeverAutomatic()
    {
        var bears = await StoreAsync("Allergies", "The user is allergic to bears",
            predicate: "allergies", value: "bears");
        await StoreAsync("Allergies", "The user is not allergic to anything",
            predicate: "allergies", value: "none");

        var live = await Repository.QueryAsync(UserScope, null, Ct);
        Assert.Contains(live, m => m.Id == bears.Memory.Id);

        var conflicts = await Repository.GetConflictsAsync(UserScope, openOnly: true, Ct);
        Assert.NotEmpty(conflicts);
    }

    /// <summary>
    /// Emptiness is not a licence to cross a companion boundary. A private "no allergies" must not
    /// archive one every companion can see, or the fact disappears for all the others.
    /// </summary>
    [Fact]
    public async Task AnEmptySetRetractionCannotNarrowScope()
    {
        var shared = await StoreAsync("Allergies", "The user is not allergic to anything",
            predicate: "allergies", value: "none");
        await StoreAsync("Allergies", "The user is allergic to bears",
            predicate: "allergies", value: "bears",
            visibility: MemoryVisibility.Scoped, companionId: "aria");

        var live = await Repository.QueryAsync(UserScope, null, Ct);
        Assert.Contains(live, m => m.Id == shared.Memory.Id);
    }

    /// <summary>
    /// A denial about someone else is not a retraction of the user's own facts. Same wording, same
    /// slot, different subject.
    /// </summary>
    [Fact]
    public async Task ADenialAboutAnotherSubjectDoesNotRetractTheUsersValue()
    {
        var memory = CreateTestMemory(
            "Allergies", "Aria is not allergic to anything", content: "", userId: User,
            subject: "companion:aria", predicate: "allergies", value: "none");
        await ConflictStorage.StoreAsync(memory, UserScope, "test", Ct);

        await StoreAsync("Allergies", "The user is allergic to bears",
            predicate: "allergies", value: "bears");

        var live = await Repository.QueryAsync(UserScope, null, Ct);
        Assert.Contains(live, m => m.Id == memory.Id);
    }
}
