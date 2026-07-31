using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Retrieval;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// Point-in-time recall over the valid-time axis.
///
/// Without it, "where was I working last year" is unanswerable: the only memory still active on the
/// employer slot is the one that <em>replaced</em> the correct answer. The bitemporal fields were
/// being written all along; these tests are what make them load-bearing.
/// </summary>
public class TemporalRecallTests : MemoryServiceTestBase
{
    private const string User = "aaron";

    private static MemoryScope UserScope => MemoryScope.AllFor(User);

    /// <summary>Writes a slot value with an explicit validity window, as history replay would.</summary>
    private async Task<MemoryNodeEntity> RecordAsync(
        string title, string summary, string predicate, string value,
        DateTime validFrom, DateTime? validUntil = null)
    {
        var memory = CreateTestMemory(title, summary, userId: User, predicate: predicate, value: value);
        memory.ValidFrom  = validFrom;
        memory.ValidUntil = validUntil;
        memory.State      = validUntil.HasValue ? MemoryState.Superseded : MemoryState.Active;

        await Repository.SaveAsync(memory, Ct);
        return memory;
    }

    [Fact]
    public async Task AsOfReturnsTheValueThatWasCurrentThenNotTheOneThatReplacedIt()
    {
        var y2023 = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var y2025 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await RecordAsync("Employer", "The user works at Initech", "employer", "initech", y2023, y2025);
        await RecordAsync("Employer", "The user works at Acme",    "employer", "acme",    y2025);

        var back = await Repository.GetBySlotAsync(
            UserScope, "user", "employer", includeHistory: false,
            asOf: new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Ct);

        var single = Assert.Single(back);
        Assert.Contains("Initech", single.Summary);

        var nowValue = await Repository.GetBySlotAsync(UserScope, "user", "employer", false, Ct);
        Assert.Contains("Acme", Assert.Single(nowValue).Summary);
    }

    [Fact]
    public async Task AsOfExcludesFactsNotYetLearnedAtThatInstant()
    {
        var y2020 = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var y2026 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await RecordAsync("Old", "true from 2020", "city_of_residence", "perth", y2020);
        await RecordAsync("New", "true from 2026", "nickname_for_user", "skipper", y2026);

        var back = await Repository.QueryAsync(
            UserScope, new MemoryQueryOptions { AsOf = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc) }, Ct);

        Assert.Contains(back, m => m.Title == "Old");
        Assert.DoesNotContain(back, m => m.Title == "New");
    }

    [Fact]
    public async Task SearchAnsweringAsOfReachesTheSupersededMemory()
    {
        var y2023 = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var y2025 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await RecordAsync("Employer", "The user works at Initech", "employer", "initech", y2023, y2025);
        await RecordAsync("Employer", "The user works at Acme",    "employer", "acme",    y2025);

        var past = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "employer", Scope = UserScope, Predicate = "employer", TopN = 5,
            AsOf = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Reinforce = false,
        }, Ct);

        Assert.Contains(past.Results, r => r.Memory.Summary.Contains("Initech"));
        Assert.DoesNotContain(past.Results, r => r.Memory.Summary.Contains("Acme"));

        var present = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "employer", Scope = UserScope, Predicate = "employer", TopN = 5, Reinforce = false,
        }, Ct);

        Assert.Contains(present.Results, r => r.Memory.Summary.Contains("Acme"));
        Assert.DoesNotContain(present.Results, r => r.Memory.Summary.Contains("Initech"));
    }

    /// <summary>
    /// A memory the user asked to forget stays forgotten in the past too. Time travel is not a
    /// loophole in deletion.
    /// </summary>
    [Fact]
    public async Task AsOfNeverResurrectsAForgottenMemory()
    {
        var memory = await RecordAsync(
            "Secret", "something the user retracted", "nickname_for_user", "old",
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await Repository.ForgetAsync(memory.Id, UserScope, "test", Ct);

        var back = await Repository.QueryAsync(
            UserScope, new MemoryQueryOptions { AsOf = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc) }, Ct);

        Assert.DoesNotContain(back, m => m.Id == memory.Id);
    }

    /// <summary>Expiry is evaluated at the requested instant, so a lapsed note is visible when it was live.</summary>
    [Fact]
    public async Task AsOfSeesAnEphemeralMemoryInsideItsWindow()
    {
        var memory = CreateTestMemory("Ephemeral", "the user is out at lunch", userId: User, type: MemoryType.Ephemeral);
        memory.ValidFrom = DateTime.UtcNow.AddDays(-2);
        memory.ExpiresAt = DateTime.UtcNow.AddDays(-1);
        await Repository.SaveAsync(memory, Ct);

        var now  = await Repository.QueryAsync(UserScope, null, Ct);
        var then = await Repository.QueryAsync(
            UserScope, new MemoryQueryOptions { AsOf = DateTime.UtcNow.AddHours(-36) }, Ct);

        Assert.DoesNotContain(now,  m => m.Id == memory.Id);
        Assert.Contains(then, m => m.Id == memory.Id);
    }

    [Fact]
    public async Task AsOfRespectsCompanionScope()
    {
        var validFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var private_ = CreateTestMemory(
            "Aria only", "something told to Aria alone", userId: User,
            companionId: "aria", visibility: MemoryVisibility.Scoped);
        private_.ValidFrom = validFrom;
        await Repository.SaveAsync(private_, Ct);

        var asOf = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var aria = await Repository.QueryAsync(
            MemoryScope.For(User, "aria"), new MemoryQueryOptions { AsOf = asOf }, Ct);
        var mika = await Repository.QueryAsync(
            MemoryScope.For(User, "mika"), new MemoryQueryOptions { AsOf = asOf }, Ct);

        Assert.Contains(aria, m => m.Id == private_.Id);
        Assert.DoesNotContain(mika, m => m.Id == private_.Id);
    }
}
