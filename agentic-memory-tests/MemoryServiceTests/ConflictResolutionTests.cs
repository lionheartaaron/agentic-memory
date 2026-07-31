using AgenticMemory.Brain.Conflict;
using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Slots;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// Contradiction handling.
///
/// The rule under test: replacing a memory is a deterministic decision about the slot, subject,
/// scope and provenance — not a similarity threshold. The previous implementation archived an
/// existing memory whenever cosine similarity cleared raw 0.60, which is "same topic" and not
/// "contradicts", so storing one food preference deleted another.
/// </summary>
public class ConflictResolutionTests : MemoryServiceTestBase
{
    private const string User = "aaron";

    private MemoryNodeEntity Fact(
        string title, string summary, string? predicate = null, string? value = null,
        string? companionId = null, MemoryVisibility visibility = MemoryVisibility.Global,
        string? subject = null, MemorySource source = MemorySource.UserStated,
        MemoryType type = MemoryType.Semantic) =>
        CreateTestMemory(title, summary, userId: User, companionId: companionId, visibility: visibility,
            subject: subject, predicate: predicate, value: value, source: source, type: type);

    // ── The cases that used to destroy data ───────────────────────────────────────────────────

    [Fact]
    public async Task RelatedButIndependentPreferences_BothSurvive()
    {
        var pizza = Fact("Likes pizza", "The user loves pizza");
        var pasta = Fact("Likes pasta", "The user loves pasta");

        await ConflictStorage.StoreAsync(pizza, MemoryScope.AllFor(User), "test", Ct);
        var result = await ConflictStorage.StoreAsync(pasta, MemoryScope.AllFor(User), "test", Ct);

        Assert.NotEqual(StoreAction.StoredWithSupersede, result.Action);

        // Both remain recallable. Under the old threshold these sat at ~0.6 cosine and one
        // archived the other.
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(pizza.Id, Ct))!.State);
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(pasta.Id, Ct))!.State);
    }

    [Fact]
    public async Task SameAttributeOnDifferentSubjects_DoNotCollide()
    {
        var userColour = Fact("Favourite colour", "The user's favourite colour is blue",
            predicate: "favourite_colour", value: "blue", subject: SubjectRefs.User);

        var ariaColour = Fact("Favourite colour", "Aria's favourite colour is violet",
            predicate: "favourite_colour", value: "violet", subject: SubjectRefs.Companion("aria"),
            type: MemoryType.Persona);

        await ConflictStorage.StoreAsync(userColour, MemoryScope.AllFor(User), "test", Ct);
        var result = await ConflictStorage.StoreAsync(ariaColour, MemoryScope.AllFor(User), "test", Ct);

        Assert.NotEqual(StoreAction.StoredWithSupersede, result.Action);
        Assert.Empty(result.Conflicts);

        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(userColour.Id, Ct))!.State);
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(ariaColour.Id, Ct))!.State);
    }

    /// <summary>
    /// The rule that matters most with several companions: a private conversation must never
    /// archive something every companion knows, or it erases knowledge from all the others.
    /// </summary>
    [Fact]
    public async Task CompanionScopedMemory_CannotSupersedeAGlobalOne()
    {
        var global = Fact("Employer", "The user works at Acme", predicate: "employer", value: "acme");
        await ConflictStorage.StoreAsync(global, MemoryScope.AllFor(User), "test", Ct);

        var scoped = Fact("Employer", "The user works at Globex", predicate: "employer", value: "globex",
            companionId: "aria", visibility: MemoryVisibility.Scoped);

        var result = await ConflictStorage.StoreAsync(scoped, MemoryScope.For(User, "aria"), "test", Ct);

        // The shared memory survives untouched.
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(global.Id, Ct))!.State);

        // And the disagreement is recorded rather than silently resolved.
        Assert.Equal(StoreAction.StoredWithConflict, result.Action);
        Assert.Contains(result.Conflicts, c => c.Kind == ConflictKind.CrossScopeContradiction);

        // Mika still knows the original.
        var mikaView = await Repository.GetBySlotAsync(
            MemoryScope.For(User, "mika"), SubjectRefs.User, "employer", includeHistory: false, Ct);

        Assert.Single(mikaView);
        Assert.Equal(global.Id, mikaView[0].Id);
    }

    // ── Legal replacement ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SingularSlot_NewValueSupersedesOldAndKeepsHistory()
    {
        var acme = Fact("Employer", "The user works at Acme", predicate: "employer", value: "acme");
        await ConflictStorage.StoreAsync(acme, MemoryScope.AllFor(User), "test", Ct);

        var globex = Fact("Employer", "The user works at Globex", predicate: "employer", value: "globex");
        var result = await ConflictStorage.StoreAsync(globex, MemoryScope.AllFor(User), "test", Ct);

        Assert.Equal(StoreAction.StoredWithSupersede, result.Action);

        var old = await AdminStore.GetByIdUnscopedAsync(acme.Id, Ct);
        Assert.Equal(MemoryState.Superseded, old!.State);
        Assert.Equal(globex.Id, old.SupersededBy);
        Assert.NotNull(old.ValidUntil);

        // History is intact and ordered.
        var history = await Repository.GetBySlotAsync(
            MemoryScope.AllFor(User), SubjectRefs.User, "employer", includeHistory: true, Ct);

        Assert.Equal(2, history.Count);
    }

    [Fact]
    public async Task MultiValuedSlot_NeverSupersedes()
    {
        var cat = Fact("Pet", "The user has a cat called Mochi", predicate: "pets", value: "mochi");
        var dog = Fact("Pet", "The user has a dog called Rex", predicate: "pets", value: "rex");

        await ConflictStorage.StoreAsync(cat, MemoryScope.AllFor(User), "test", Ct);
        var result = await ConflictStorage.StoreAsync(dog, MemoryScope.AllFor(User), "test", Ct);

        Assert.NotEqual(StoreAction.StoredWithSupersede, result.Action);
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(cat.Id, Ct))!.State);
    }

    [Fact]
    public async Task Allergies_AreNeverReplacedBySomethingSimilar()
    {
        var shellfish = Fact("Allergy", "Allergic to shellfish", predicate: "allergies", value: "shellfish");
        var peanuts   = Fact("Allergy", "Allergic to peanuts", predicate: "allergies", value: "peanuts");

        await ConflictStorage.StoreAsync(shellfish, MemoryScope.AllFor(User), "test", Ct);
        await ConflictStorage.StoreAsync(peanuts, MemoryScope.AllFor(User), "test", Ct);

        var current = await Repository.GetBySlotAsync(
            MemoryScope.AllFor(User), SubjectRefs.User, "allergies", includeHistory: false, Ct);

        Assert.Equal(2, current.Count);
    }

    // ── Escalation ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SoftPreferenceChange_IsRecordedForConfirmationRatherThanApplied()
    {
        var ramen = Fact("Favourite food", "The user's favourite food is ramen",
            predicate: "favourite_food", value: "ramen");
        await ConflictStorage.StoreAsync(ramen, MemoryScope.AllFor(User), "test", Ct);

        var curry = Fact("Favourite food", "The user's favourite food is curry",
            predicate: "favourite_food", value: "curry");
        var result = await ConflictStorage.StoreAsync(curry, MemoryScope.AllFor(User), "test", Ct);

        Assert.Equal(StoreAction.StoredWithConflict, result.Action);
        Assert.Contains(result.Conflicts, c => c.Kind == ConflictKind.SoftPreferenceChange);

        // Both stay active so the companion can ask which is right.
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(ramen.Id, Ct))!.State);
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(curry.Id, Ct))!.State);
    }

    [Fact]
    public async Task ImmutableSlot_ContradictionIsFlaggedAndOriginalKept()
    {
        var original = Fact("Birthday", "Born on 3 March 1990", predicate: "birthday", value: "1990-03-03");
        await ConflictStorage.StoreAsync(original, MemoryScope.AllFor(User), "test", Ct);

        var wrong = Fact("Birthday", "Born on 7 July 1991", predicate: "birthday", value: "1991-07-07");
        var result = await ConflictStorage.StoreAsync(wrong, MemoryScope.AllFor(User), "test", Ct);

        Assert.Contains(result.Conflicts, c => c.Kind == ConflictKind.ImmutableViolation);
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(original.Id, Ct))!.State);
    }

    [Fact]
    public async Task InferredMemory_CannotOverwriteSomethingTheUserStated()
    {
        var stated = Fact("Employer", "The user works at Acme",
            predicate: "employer", value: "acme", source: MemorySource.UserStated);
        await ConflictStorage.StoreAsync(stated, MemoryScope.AllFor(User), "test", Ct);

        var guessed = Fact("Employer", "The user probably works at Initech",
            predicate: "employer", value: "initech", source: MemorySource.CompanionInferred);
        var result = await ConflictStorage.StoreAsync(guessed, MemoryScope.AllFor(User), "test", Ct);

        Assert.Contains(result.Conflicts, c => c.Kind == ConflictKind.ProvenanceDowngrade);
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(stated.Id, Ct))!.State);
    }

    [Fact]
    public async Task PersonaFacts_CannotBeOverwrittenByConversation()
    {
        var persona = Fact("Aria's colour", "Aria's favourite colour is violet",
            predicate: "favourite_colour", value: "violet",
            subject: SubjectRefs.Companion("aria"), type: MemoryType.Persona);
        await ConflictStorage.StoreAsync(persona, MemoryScope.AllFor(User), "test", Ct);

        var talkedInto = Fact("Aria's colour", "Aria's favourite colour is grey",
            predicate: "favourite_colour", value: "grey",
            subject: SubjectRefs.Companion("aria"), type: MemoryType.Semantic);

        var result = await ConflictStorage.StoreAsync(talkedInto, MemoryScope.AllFor(User), "test", Ct);

        Assert.NotEqual(StoreAction.StoredWithSupersede, result.Action);
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(persona.Id, Ct))!.State);
    }

    [Fact]
    public async Task UnslottedMemories_AlwaysCoexist()
    {
        var first  = Fact("Note", "The user mentioned wanting to move house");
        var second = Fact("Note", "The user mentioned wanting to move abroad");

        await ConflictStorage.StoreAsync(first, MemoryScope.AllFor(User), "test", Ct);
        var result = await ConflictStorage.StoreAsync(second, MemoryScope.AllFor(User), "test", Ct);

        Assert.NotEqual(StoreAction.StoredWithSupersede, result.Action);
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(first.Id, Ct))!.State);
    }

    // ── Resolution ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolvingAConflict_SupersedesTheLoserWithoutDeletingIt()
    {
        var ramen = Fact("Favourite food", "ramen", predicate: "favourite_food", value: "ramen");
        await ConflictStorage.StoreAsync(ramen, MemoryScope.AllFor(User), "test", Ct);

        var curry = Fact("Favourite food", "curry", predicate: "favourite_food", value: "curry");
        var result = await ConflictStorage.StoreAsync(curry, MemoryScope.AllFor(User), "test", Ct);

        var conflict = Assert.Single(result.Conflicts);

        Assert.Equal(ConflictResolution.Resolved, await Repository.ResolveConflictAsync(
            conflict.Id, MemoryScope.AllFor(User), curry.Id, dismissed: false, "test", Ct));

        Assert.Equal(MemoryState.Superseded, (await AdminStore.GetByIdUnscopedAsync(ramen.Id, Ct))!.State);
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(curry.Id, Ct))!.State);

        Assert.Empty(await Repository.GetConflictsAsync(MemoryScope.AllFor(User), openOnly: true, Ct));
    }

    [Fact]
    public async Task DismissingAConflict_LeavesBothActive()
    {
        var a = Fact("Favourite food", "ramen", predicate: "favourite_food", value: "ramen");
        await ConflictStorage.StoreAsync(a, MemoryScope.AllFor(User), "test", Ct);
        var b = Fact("Favourite food", "curry", predicate: "favourite_food", value: "curry");
        var result = await ConflictStorage.StoreAsync(b, MemoryScope.AllFor(User), "test", Ct);

        var conflict = Assert.Single(result.Conflicts);
        await Repository.ResolveConflictAsync(conflict.Id, MemoryScope.AllFor(User), null, dismissed: true, "test", Ct);

        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(a.Id, Ct))!.State);
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(b.Id, Ct))!.State);
        Assert.Empty(await Repository.GetConflictsAsync(MemoryScope.AllFor(User), openOnly: true, Ct));
    }

    [Fact]
    public async Task RestatingAKnownFact_ReinforcesInsteadOfDuplicating()
    {
        var first = Fact("Employer", "The user works at Acme", predicate: "employer", value: "acme");
        await ConflictStorage.StoreAsync(first, MemoryScope.AllFor(User), "test", Ct);

        var again = Fact("Employer", "The user works at Acme", predicate: "employer", value: "acme");
        var result = await ConflictStorage.StoreAsync(again, MemoryScope.AllFor(User), "test", Ct);

        Assert.Equal(StoreAction.ReinforcedExisting, result.Action);
        Assert.Equal(first.Id, result.Memory.Id);

        var all = await Repository.QueryAsync(MemoryScope.AllFor(User), null, Ct);
        Assert.Single(all);
    }

    // ── Gate unit tests ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate_RequiresSharedStructuredSlot()
    {
        var gate = new SupersedeGate(new SlotRegistry());

        var verdict = gate.Evaluate(
            Fact("A", "one thing"),
            Fact("B", "another thing"));

        Assert.Equal(SupersedeDecision.Coexist, verdict.Decision);
    }

    [Fact]
    public void Gate_AllowsLatestWinsOnSingularSlot()
    {
        var gate = new SupersedeGate(new SlotRegistry());

        var verdict = gate.Evaluate(
            Fact("New", "Globex", predicate: "employer", value: "globex"),
            Fact("Old", "Acme", predicate: "employer", value: "acme"));

        Assert.Equal(SupersedeDecision.Supersede, verdict.Decision);
    }

    [Fact]
    public void Gate_TreatsUnknownPredicatesAsMultiValued()
    {
        var registry = new SlotRegistry();
        Assert.Equal(SlotCardinality.MultiValued, registry.Resolve("something_nobody_registered").Cardinality);
        Assert.Equal(SlotCardinality.MultiValued, registry.Resolve(null).Cardinality);
    }

    // ── Resolving with a choice ───────────────────────────────────────────────────────────────
    //
    // ResolveConflictAsync used to return bool, so the only thing it could report was whether the
    // conflict existed. Every case below was previously indistinguishable from a correct
    // resolution, and the first one silently corrupted the store.

    /// <summary>Stores two contradicting facts and returns the conflict between them.</summary>
    private async Task<(MemoryConflict Conflict, MemoryNodeEntity Old, MemoryNodeEntity New)> Contradiction()
    {
        var older = Fact("Employer", "Acme", predicate: "employer", value: "acme");
        await ConflictStorage.StoreAsync(older, MemoryScope.AllFor(User), "test", Ct);

        var newer = Fact("Employer", "Globex", predicate: "employer", value: "globex",
            source: MemorySource.CompanionInferred);
        var result = await ConflictStorage.StoreAsync(newer, MemoryScope.AllFor(User), "test", Ct);

        return (Assert.Single(result.Conflicts), older, newer);
    }

    /// <summary>
    /// The one that mattered. The loser is "whichever side is not the winner", so an id belonging
    /// to neither made the *new* memory the loser and superseded it in favour of a memory that was
    /// never part of the conflict. It returned true.
    /// </summary>
    [Fact]
    public async Task AWinnerThatIsNotInTheConflict_ChangesNothing()
    {
        var (conflict, old, @new) = await Contradiction();
        var bystander = Fact("Unrelated", "The user has a cat");
        await ConflictStorage.StoreAsync(bystander, MemoryScope.AllFor(User), "test", Ct);

        var outcome = await Repository.ResolveConflictAsync(
            conflict.Id, MemoryScope.AllFor(User), bystander.Id, dismissed: false, "test", Ct);

        Assert.Equal(ConflictResolution.WinnerNotInConflict, outcome);

        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(old.Id, Ct))!.State);
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(@new.Id, Ct))!.State);
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(bystander.Id, Ct))!.State);

        // Still open, so the decision it was actually waiting for can still be made.
        Assert.Single(await Repository.GetConflictsAsync(MemoryScope.AllFor(User), openOnly: true, Ct));
    }

    [Fact]
    public async Task ResolvingWithNeitherAWinnerNorADismissal_ChangesNothing()
    {
        var (conflict, old, @new) = await Contradiction();

        var outcome = await Repository.ResolveConflictAsync(
            conflict.Id, MemoryScope.AllFor(User), winnerId: null, dismissed: false, "test", Ct);

        Assert.Equal(ConflictResolution.NoChoice, outcome);
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(old.Id, Ct))!.State);
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(@new.Id, Ct))!.State);
        Assert.Single(await Repository.GetConflictsAsync(MemoryScope.AllFor(User), openOnly: true, Ct));
    }

    /// <summary>
    /// Settling twice with opposite winners would supersede both sides, leaving the slot with
    /// nothing current. The second attempt is refused instead.
    /// </summary>
    [Fact]
    public async Task SettlingTheSameConflictTwice_IsRefusedAndLeavesTheFirstWinnerStanding()
    {
        var (conflict, old, @new) = await Contradiction();

        Assert.Equal(ConflictResolution.Resolved, await Repository.ResolveConflictAsync(
            conflict.Id, MemoryScope.AllFor(User), @new.Id, dismissed: false, "test", Ct));

        var second = await Repository.ResolveConflictAsync(
            conflict.Id, MemoryScope.AllFor(User), old.Id, dismissed: false, "test", Ct);

        Assert.Equal(ConflictResolution.AlreadySettled, second);
        Assert.Equal(MemoryState.Active, (await AdminStore.GetByIdUnscopedAsync(@new.Id, Ct))!.State);
        Assert.Equal(MemoryState.Superseded, (await AdminStore.GetByIdUnscopedAsync(old.Id, Ct))!.State);
    }

    [Fact]
    public async Task AConflictBelongingToAnotherUser_IsNotFound()
    {
        var (conflict, _, @new) = await Contradiction();

        var outcome = await Repository.ResolveConflictAsync(
            conflict.Id, MemoryScope.AllFor("someone-else"), @new.Id, dismissed: false, "test", Ct);

        Assert.Equal(ConflictResolution.NotFound, outcome);
        Assert.Single(await Repository.GetConflictsAsync(MemoryScope.AllFor(User), openOnly: true, Ct));
    }

    [Fact]
    public async Task DismissingRecordsNoWinner()
    {
        var (conflict, _, _) = await Contradiction();

        Assert.Equal(ConflictResolution.Dismissed, await Repository.ResolveConflictAsync(
            conflict.Id, MemoryScope.AllFor(User), winnerId: null, dismissed: true, "test", Ct));

        var settled = Assert.Single(
            await Repository.GetConflictsAsync(MemoryScope.AllFor(User), openOnly: false, Ct));

        Assert.Equal(ConflictStatus.Dismissed, settled.Status);
        Assert.Null(settled.WinnerId);
    }

    // ── Seeing what is being chosen between ───────────────────────────────────────────────────

    [Fact]
    public async Task ConflictDetail_CarriesBothSidesWithEnoughToChooseBetweenThem()
    {
        var (conflict, old, @new) = await Contradiction();

        var detail = Assert.Single(
            await Repository.GetConflictDetailsAsync(MemoryScope.AllFor(User), openOnly: true, Ct));

        Assert.Equal(conflict.Id, detail.Conflict.Id);
        Assert.NotNull(detail.Existing);
        Assert.NotNull(detail.New);

        Assert.Equal(old.Id, detail.Existing!.Id);
        Assert.Equal(@new.Id, detail.New!.Id);

        // The fields a decision actually turns on: which value, still current, and how trusted.
        Assert.Equal("acme", detail.Existing.ValueKey);
        Assert.Equal("globex", detail.New.ValueKey);
        Assert.Equal(MemoryState.Active, detail.Existing.State);
        Assert.Equal(MemorySource.UserStated, detail.Existing.Source);
        Assert.Equal(MemorySource.CompanionInferred, detail.New.Source);
    }

    /// <summary>
    /// A forgotten memory is deliberately unfetchable by id, so a caller told only "new: X,
    /// existing: Y" could not read one side at all. The detail projection reads the collection
    /// directly for exactly this reason.
    /// </summary>
    [Fact]
    public async Task AForgottenSideIsStillShown()
    {
        var (conflict, old, _) = await Contradiction();

        await Repository.ForgetAsync(old.Id, MemoryScope.AllFor(User), "test", Ct);
        Assert.Null(await Repository.GetAsync(old.Id, MemoryScope.AllFor(User), Ct));

        var detail = await Repository.GetConflictAsync(conflict.Id, MemoryScope.AllFor(User), Ct);

        Assert.NotNull(detail);
        Assert.NotNull(detail!.Existing);
        Assert.Equal(MemoryState.Forgotten, detail.Existing!.State);
    }

    [Fact]
    public async Task GetConflict_IsScopedToItsOwner()
    {
        var (conflict, _, _) = await Contradiction();

        Assert.NotNull(await Repository.GetConflictAsync(conflict.Id, MemoryScope.AllFor(User), Ct));
        Assert.Null(await Repository.GetConflictAsync(conflict.Id, MemoryScope.AllFor("someone-else"), Ct));
    }
}
