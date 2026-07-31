using AgenticMemory.Brain.Models;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// Contradictions the slot registry cannot see.
///
/// The supersede gate only engages when both memories assert the same registered predicate. A
/// statement and its denial usually carry no predicate at all — "the user drinks coffee every
/// morning" and "the user has stopped drinking coffee" are filed as two unrelated facts, both stay
/// active, and retrieval faithfully returns both, leaving the companion to pick one at random.
///
/// The detector is deliberately narrow. Half of these tests exist to prove it stays quiet: a false
/// contradiction between two memories that merely share a subject is worse than a missed one.
/// </summary>
public class PolarityContradictionTests : MemoryServiceTestBase
{
    private const string User = "aaron";

    private static MemoryScope UserScope => MemoryScope.AllFor(User);

    private async Task<StoreResult> StoreAsync(
        string title, string summary,
        string? subject = null, MemoryVisibility visibility = MemoryVisibility.Global,
        string? companionId = null)
    {
        var memory = CreateTestMemory(
            title, summary, content: "", userId: User,
            subject: subject, visibility: visibility, companionId: companionId);

        return await ConflictStorage.StoreAsync(memory, UserScope, "test", Ct);
    }

    [Fact]
    public async Task ADenialOfAnExistingStatementIsRecordedAsAContradiction()
    {
        await StoreAsync("Coffee", "The user drinks coffee every morning");
        await StoreAsync("Coffee", "The user does not drink coffee every morning");

        var conflicts = await Repository.GetConflictsAsync(UserScope, openOnly: true, Ct);

        Assert.Contains(conflicts, c => c.Kind == ConflictKind.PolarityContradiction);
    }

    /// <summary>Both sides must survive — a heuristic must never be trusted to delete.</summary>
    [Fact]
    public async Task NeitherSideOfAPolarityContradictionIsArchived()
    {
        var first  = await StoreAsync("Coffee", "The user drinks coffee every morning");
        var second = await StoreAsync("Coffee", "The user does not drink coffee every morning");

        var live = await Repository.QueryAsync(UserScope, null, Ct);

        Assert.Contains(live, m => m.Id == first.Memory.Id);
        Assert.Contains(live, m => m.Id == second.Memory.Id);
    }

    [Fact]
    public async Task TheContradictionIsSurfacedAlongsideTheMemoriesItTouches()
    {
        await StoreAsync("Coffee", "The user drinks coffee every morning");
        await StoreAsync("Coffee", "The user does not drink coffee every morning");

        var result = await SearchService.RetrieveAsync(new AgenticMemory.Brain.Retrieval.RetrievalRequest
        {
            Query = "coffee", Scope = UserScope, TopN = 5, Reinforce = false,
        }, Ct);

        Assert.NotEmpty(result.Results);
        Assert.Contains(result.Conflicts, c => c.Kind == ConflictKind.PolarityContradiction);
    }

    // ── Everything below is a case the detector must stay quiet about ────────────────────────

    [Fact]
    public async Task TwoDifferentFactsAboutTheSameTopicAreNotAContradiction()
    {
        await StoreAsync("Coffee", "The user drinks coffee every morning");
        await StoreAsync("Tea", "The user drinks tea in the afternoon");

        var conflicts = await Repository.GetConflictsAsync(UserScope, openOnly: true, Ct);
        Assert.DoesNotContain(conflicts, c => c.Kind == ConflictKind.PolarityContradiction);
    }

    [Fact]
    public async Task TwoDenialsAreNotAContradictionWithEachOther()
    {
        await StoreAsync("Coffee", "The user does not drink coffee");
        await StoreAsync("Coffee again", "The user does not drink coffee at all");

        var conflicts = await Repository.GetConflictsAsync(UserScope, openOnly: true, Ct);
        Assert.DoesNotContain(conflicts, c => c.Kind == ConflictKind.PolarityContradiction);
    }

    [Fact]
    public async Task ADenialAboutADifferentSubjectIsNotAContradiction()
    {
        await StoreAsync("Coffee", "The user drinks coffee every morning", subject: "user");
        await StoreAsync("Coffee", "Aria does not drink coffee every morning", subject: "companion:aria");

        var conflicts = await Repository.GetConflictsAsync(UserScope, openOnly: true, Ct);
        Assert.DoesNotContain(conflicts, c => c.Kind == ConflictKind.PolarityContradiction);
    }

    /// <summary>
    /// A private memory and a shared one describe different audiences, not different truths. Treating
    /// them as contradictory would surface one companion's private conversation to the rest.
    /// </summary>
    [Fact]
    public async Task ADenialInADifferentScopeIsNotComparedAtAll()
    {
        await StoreAsync("Coffee", "The user drinks coffee every morning");
        await StoreAsync("Coffee", "The user does not drink coffee every morning",
            visibility: MemoryVisibility.Scoped, companionId: "aria");

        var conflicts = await Repository.GetConflictsAsync(MemoryScope.AllFor(User), openOnly: true, Ct);
        Assert.DoesNotContain(conflicts, c => c.Kind == ConflictKind.PolarityContradiction);
    }

    [Fact]
    public async Task AnUnrelatedNegativeStatementIsNotAContradiction()
    {
        await StoreAsync("Coffee", "The user drinks coffee every morning");
        await StoreAsync("Travel", "The user has never been to Japan");

        var conflicts = await Repository.GetConflictsAsync(UserScope, openOnly: true, Ct);
        Assert.DoesNotContain(conflicts, c => c.Kind == ConflictKind.PolarityContradiction);
    }

    /// <summary>
    /// Where a registered slot applies, the gate owns the decision. The polarity heuristic must not
    /// add a second, differently-reasoned conflict on top of it.
    /// </summary>
    [Fact]
    public async Task SlottedFactsAreLeftToTheSupersedeGate()
    {
        var first = CreateTestMemory(
            "Employer", "The user works at Acme", content: "", userId: User,
            predicate: "employer", value: "acme");
        await ConflictStorage.StoreAsync(first, UserScope, "test", Ct);

        var second = CreateTestMemory(
            "Employer", "The user does not work at Acme", content: "", userId: User,
            predicate: "employer", value: "globex");
        await ConflictStorage.StoreAsync(second, UserScope, "test", Ct);

        var conflicts = await Repository.GetConflictsAsync(UserScope, openOnly: false, Ct);
        Assert.DoesNotContain(conflicts, c => c.Kind == ConflictKind.PolarityContradiction);
    }
}
