using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Retrieval;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// Retrieval quality, measured rather than asserted by eye.
///
/// The gold set deliberately uses queries with little or no word overlap with their target memory.
/// That is the case the previous engine could not serve at all: candidates came from a purely
/// lexical matcher truncated to <c>topN * 3</c>, so embeddings only ever re-ranked whatever the
/// lexical stage had already admitted.
/// </summary>
public class RetrievalQualityTests(ITestOutputHelper output) : MemoryServiceTestBase
{
    private const string User = "aaron";

    private sealed record GoldPair(string Query, string Title, string Summary);

    private static readonly GoldPair[] GoldSet =
    [
        new("what does he like to eat",            "Food preference",   "The user's favourite meal is tonkotsu ramen"),
        new("where is home",                       "Residence",         "The user lives in Fremantle, Western Australia"),
        new("any brothers or sisters",             "Family",            "The user has an older sister called Mia"),
        new("what does he do for a living",        "Occupation",        "The user is a backend engineer at Acme"),
        new("is there a pet",                      "Pet",               "The user has a tabby cat named Mochi"),
        new("what music does he enjoy",            "Music taste",       "The user listens mostly to ambient and jazz"),
        new("how does he get to the office",       "Commute",           "The user cycles to work most mornings"),
        new("anything he cannot eat",              "Allergy",           "The user is allergic to shellfish"),
        new("what is he studying",                 "Learning",          "The user is teaching himself Japanese"),
        new("what does he do at the weekend",      "Hobby",             "The user goes bouldering on Saturdays"),
        new("who is his closest friend",           "Friendship",        "The user's best friend is called Sam"),
        new("what car does he drive",              "Vehicle",           "The user drives an old diesel Hilux"),
        new("when is his birthday",                "Birthday",          "The user was born on the third of March"),
        new("what makes him anxious",              "Emotional trigger", "The user gets anxious before public speaking"),
        new("does he play any instrument",         "Instrument",        "The user plays bass guitar badly but often"),
        new("what is he reading",                  "Reading",           "The user is partway through a book on ant colonies"),
        new("preferred way to be contacted",       "Contact preference","The user would rather receive a text than a call"),
        new("what did he study at university",     "Education",         "The user read philosophy at Melbourne"),
        new("any plans to travel",                 "Travel plan",       "The user wants to visit Hokkaido next winter"),
        new("what wakes him up",                   "Routine",           "The user gets up at five to swim"),
    ];

    private static readonly string[] DistractorTopics =
    [
        "quarterly revenue projections", "the migration of arctic terns", "a recipe for sourdough starter",
        "settings for a film camera", "the rules of shogi", "how tides work", "a broken dishwasher",
        "the history of the printing press", "notes on kubernetes ingress", "a list of climbing knots",
        "bus timetable changes", "how to prune tomatoes", "the plot of a spy novel",
        "differences between diesel engines", "a tutorial on watercolour washes",
    ];

    private async Task SeedAsync(int distractorCount)
    {
        foreach (var gold in GoldSet)
            await Repository.SaveAsync(
                await EmbedAsync(CreateTestMemory(gold.Title, gold.Summary, content: "", userId: User)), Ct);

        for (var i = 0; i < distractorCount; i++)
        {
            var topic = DistractorTopics[i % DistractorTopics.Length];
            await Repository.SaveAsync(
                await EmbedAsync(CreateTestMemory(
                    $"Note {i}", $"An unrelated note about {topic}, entry {i}", content: "", userId: User)), Ct);
        }
    }

    private async Task<MemoryNodeEntity> EmbedAsync(MemoryNodeEntity memory)
    {
        if (EmbeddingService?.IsAvailable != true) return memory;

        var stamp = AgenticMemory.Brain.Storage.MemoryTextIndexer.BuildEmbeddingStamp(EmbeddingService.ModelId);
        var text  = AgenticMemory.Brain.Storage.MemoryTextIndexer.BuildEmbeddingText(memory);
        memory.SetEmbedding(await EmbeddingService.GetEmbeddingAsync(text, Ct), stamp);
        return memory;
    }

    [Fact]
    public async Task RecallAtK_MeetsBaselineAgainstDistractors()
    {
        Assert.SkipUnless(EmbeddingService?.IsAvailable == true, "Embedding model unavailable");

        await SeedAsync(distractorCount: 200);

        int hitsAt1 = 0, hitsAt5 = 0, hitsAt20 = 0;
        var reciprocalRanks = 0.0;
        var misses = new List<string>();

        foreach (var gold in GoldSet)
        {
            var result = await SearchService.RetrieveAsync(new RetrievalRequest
            {
                Query = gold.Query, Scope = MemoryScope.AllFor(User), TopN = 20, Reinforce = false,
            }, Ct);

            var rank = result.Results
                .Select((r, i) => (r.Memory.Title, Index: i))
                .Where(x => x.Title == gold.Title)
                .Select(x => x.Index + 1)
                .FirstOrDefault();

            if (rank == 1) hitsAt1++;
            if (rank is >= 1 and <= 5) hitsAt5++;
            if (rank >= 1) hitsAt20++;
            if (rank >= 1) reciprocalRanks += 1.0 / rank;

            // Printed so a regression names the query it lost, rather than only moving a percentage.
            if (rank == 0)
                misses.Add($"  MISS '{gold.Query}' → '{gold.Title}' " +
                           $"(returned {result.Results.Count}, semantic={result.SemanticSearchUsed}, {result.Confidence})");
        }

        var n = GoldSet.Length;
        output.WriteLine($"recall@1  = {hitsAt1 / (double)n:P0}  ({hitsAt1}/{n})");
        output.WriteLine($"recall@5  = {hitsAt5 / (double)n:P0}  ({hitsAt5}/{n})");
        output.WriteLine($"recall@20 = {hitsAt20 / (double)n:P0}  ({hitsAt20}/{n})");
        output.WriteLine($"MRR       = {reciprocalRanks / n:F3}");
        foreach (var miss in misses) output.WriteLine(miss);

        // Guards set just below observed performance (recall@1 70%, recall@5 90%, recall@20 100%,
        // MRR 0.78) so a genuine degradation fails the build without the test being flaky.
        Assert.True(hitsAt1  >= n * 0.60, $"recall@1 regressed: {hitsAt1}/{n}");
        Assert.True(hitsAt5  >= n * 0.85, $"recall@5 regressed: {hitsAt5}/{n}");
        Assert.True(hitsAt20 >= n * 0.95, $"recall@20 regressed: {hitsAt20}/{n}");
        Assert.True(reciprocalRanks / n >= 0.70, $"MRR regressed: {reciprocalRanks / n:F3}");
    }

    /// <summary>
    /// The precise shape of the old recall bug: a query sharing no words with its target.
    /// </summary>
    [Fact]
    public async Task SemanticRecall_WorksWithZeroLexicalOverlap()
    {
        Assert.SkipUnless(EmbeddingService?.IsAvailable == true, "Embedding model unavailable");

        await Repository.SaveAsync(
            await EmbedAsync(CreateTestMemory("Food preference", "The user's favourite meal is tonkotsu ramen", userId: User)), Ct);

        for (var i = 0; i < 100; i++)
            await Repository.SaveAsync(
                await EmbedAsync(CreateTestMemory($"Filler {i}", $"An unrelated note about topic {i}", userId: User)), Ct);

        var result = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "what does he like to eat", Scope = MemoryScope.AllFor(User), TopN = 5, Reinforce = false,
        }, Ct);

        Assert.Contains(result.Results, r => r.Memory.Title == "Food preference");
    }

    [Fact]
    public async Task ContentIsSearchable_NotJustTitleAndSummary()
    {
        await Repository.SaveAsync(CreateTestMemory(
            "Holiday notes", "A few things from the trip",
            "On the third day we found a tiny izakaya in Nakameguro that served horse sashimi.",
            userId: User), Ct);

        var result = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "nakameguro izakaya", Scope = MemoryScope.AllFor(User), TopN = 5, Reinforce = false,
        }, Ct);

        Assert.Contains(result.Results, r => r.Memory.Title == "Holiday notes");
    }

    [Fact]
    public async Task NonsenseQuery_ReturnsNothingAndReportsNoConfidence()
    {
        await SeedAsync(distractorCount: 20);

        var result = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "xyznonexistent123 qqzzxw", Scope = MemoryScope.AllFor(User), TopN = 10, Reinforce = false,
        }, Ct);

        Assert.Empty(result.Results);
        Assert.Equal(RetrievalConfidence.None, result.Confidence);
    }

    /// <summary>
    /// A well-formed English question about something the store knows nothing about is not the same
    /// as gibberish, and the two need different handling.
    ///
    /// The vector channel gates on how far the best match stands out from the corpus, with a lower
    /// bar for queries written in words the embedding model actually knows — invented tokens embed
    /// near the corpus centroid and out-score genuine matches, so they have to clear a high one.
    /// The risk of that lower bar is confabulation: returning the nearest unrelated memory with
    /// confidence. This pins the boundary.
    /// </summary>
    [Fact]
    public async Task WellFormedButUnanswerableQuery_DoesNotAnswerConfidently()
    {
        await SeedAsync(distractorCount: 50);

        foreach (var query in new[]
                 {
                     "what is the capital of Peru",
                     "how do I renew a passport",
                     "what did the doctor say about the results",
                 })
        {
            var result = await SearchService.RetrieveAsync(new RetrievalRequest
            {
                Query = query, Scope = MemoryScope.AllFor(User), TopN = 5, Reinforce = false,
            }, Ct);

            output.WriteLine($"'{query}' → {result.Results.Count} result(s), {result.Confidence}");

            // A companion may offer a loosely related memory, but never as something it is sure of.
            Assert.NotEqual(RetrievalConfidence.High, result.Confidence);
        }
    }

    /// <summary>
    /// The first days of a companion's life: a handful of memories, no distribution to reason about,
    /// and semantic recall mattering more than ever because there is nothing else to go on.
    ///
    /// This is the branch a fixed cosine floor gets wrong in both directions. "What can the user not
    /// eat" scores 0.30 against "the user is allergic to shellfish" — under the old floor of 0.35 the
    /// vector channel simply switched off, and a new companion could recall nothing until the corpus
    /// grew past ten memories.
    /// </summary>
    [Fact]
    public async Task ANewCompanionWithOnlyAFewMemoriesCanStillRecallSemantically()
    {
        Assert.SkipUnless(EmbeddingService?.IsAvailable == true, "Embedding model unavailable");

        await Repository.SaveAsync(
            await EmbedAsync(CreateTestMemory("Allergy", "The user is allergic to shellfish", content: "", userId: User)), Ct);
        await Repository.SaveAsync(
            await EmbedAsync(CreateTestMemory("Secret", "The user is planning a surprise trip", content: "", userId: User)), Ct);

        var result = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "what can the user not eat", Scope = MemoryScope.AllFor(User), TopN = 3, Reinforce = false,
        }, Ct);

        Assert.NotEmpty(result.Results);
        Assert.Equal("Allergy", result.Results[0].Memory.Title);
    }

    /// <summary>The other direction: a tiny corpus must not make nonsense look like a match.</summary>
    [Fact]
    public async Task ATinyCorpusStillRejectsInventedTokens()
    {
        Assert.SkipUnless(EmbeddingService?.IsAvailable == true, "Embedding model unavailable");

        await Repository.SaveAsync(
            await EmbedAsync(CreateTestMemory("Allergy", "The user is allergic to shellfish", content: "", userId: User)), Ct);
        await Repository.SaveAsync(
            await EmbedAsync(CreateTestMemory("Secret", "The user is planning a surprise trip", content: "", userId: User)), Ct);

        var result = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "zzqqxw blorptastic frumbulator", Scope = MemoryScope.AllFor(User), TopN = 3, Reinforce = false,
        }, Ct);

        Assert.Empty(result.Results);
        Assert.Equal(RetrievalConfidence.None, result.Confidence);
    }

    [Fact]
    public async Task InventedTokensAreRejectedEvenWhenTheyLookLikeAQuestion()
    {
        await SeedAsync(distractorCount: 50);

        var result = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "what about zzqqxw blorptastic frumbulator", Scope = MemoryScope.AllFor(User),
            TopN = 5, Reinforce = false,
        }, Ct);

        output.WriteLine($"invented-token query → {result.Results.Count} result(s), {result.Confidence}");
        Assert.Empty(result.Results);
    }

    [Fact]
    public async Task Diversification_AvoidsReturningTheSameFactFiveTimes()
    {
        Assert.SkipUnless(EmbeddingService?.IsAvailable == true, "Embedding model unavailable");

        // Six near-paraphrases plus two genuinely different facts.
        var paraphrases = new[]
        {
            "The user's favourite meal is tonkotsu ramen",
            "The user loves eating tonkotsu ramen",
            "Tonkotsu ramen is the user's favourite dish",
            "The user's preferred food is tonkotsu ramen",
            "Ramen, specifically tonkotsu, is the user's favourite",
            "The user says tonkotsu ramen is the best food",
        };

        for (var i = 0; i < paraphrases.Length; i++)
            await Repository.SaveAsync(
                await EmbedAsync(CreateTestMemory($"Food note {i}", paraphrases[i], userId: User)), Ct);

        await Repository.SaveAsync(
            await EmbedAsync(CreateTestMemory("Dining habit", "The user usually eats dinner very late", userId: User)), Ct);
        await Repository.SaveAsync(
            await EmbedAsync(CreateTestMemory("Cooking", "The user cooks at home most weeknights", userId: User)), Ct);

        var diverse = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "what does the user eat", Scope = MemoryScope.AllFor(User), TopN = 4,
            DiversityLambda = 0.5, Reinforce = false,
        }, Ct);

        var greedy = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "what does the user eat", Scope = MemoryScope.AllFor(User), TopN = 4,
            DiversityLambda = 1.0, Reinforce = false,
        }, Ct);

        var diverseParaphrases = diverse.Results.Count(r => r.Memory.Title.StartsWith("Food note"));
        var greedyParaphrases  = greedy.Results.Count(r => r.Memory.Title.StartsWith("Food note"));

        output.WriteLine($"paraphrases returned — diversified: {diverseParaphrases}, greedy: {greedyParaphrases}");
        Assert.True(diverseParaphrases <= greedyParaphrases);
    }

    [Fact]
    public async Task ExactSlotMatch_ReportsHighConfidence()
    {
        await Repository.SaveAsync(CreateTestMemory(
            "Employer", "The user works at Acme", userId: User, predicate: "employer", value: "acme"), Ct);

        var result = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "where does the user work", Scope = MemoryScope.AllFor(User),
            Predicate = "employer", TopN = 5, Reinforce = false,
        }, Ct);

        Assert.NotEmpty(result.Results);
        Assert.Equal(RetrievalConfidence.High, result.Confidence);
        Assert.Contains("slot", result.Results[0].MatchedChannels);
    }

    [Fact]
    public async Task CoreContext_IsReturnedRegardlessOfTheQuery()
    {
        await Repository.SaveAsync(CreateTestMemory(
            "Name", "The user is called Aaron", userId: User, type: MemoryType.Identity), Ct);
        await Repository.SaveAsync(CreateTestMemory(
            "Aria's temperament", "Aria is warm and a little sardonic", userId: User, type: MemoryType.Persona), Ct);
        await Repository.SaveAsync(CreateTestMemory(
            "Bicycle", "The user cycles to work", userId: User), Ct);

        var result = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "tell me about the commute", Scope = MemoryScope.AllFor(User),
            IncludeCoreContext = true, TopN = 3, Reinforce = false,
        }, Ct);

        Assert.Contains(result.CoreContext, c => c.Memory.Title == "Name");
        Assert.Contains(result.CoreContext, c => c.Memory.Title == "Aria's temperament");
        Assert.All(result.CoreContext, c => Assert.True(c.IsCoreContext));
    }

    [Fact]
    public async Task SearchPerformsOneReinforcementWritePerQueryNotOnePerHit()
    {
        for (var i = 0; i < 10; i++)
            await Repository.SaveAsync(CreateTestMemory($"Project note {i}", $"Notes about project {i}", userId: User), Ct);

        var before = await Repository.QueryAsync(MemoryScope.AllFor(User), null, Ct);
        Assert.All(before, m => Assert.Equal(0, m.AccessCount));

        await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "project notes", Scope = MemoryScope.AllFor(User), TopN = 5,
        }, Ct);

        var after = await Repository.QueryAsync(MemoryScope.AllFor(User), null, Ct);
        Assert.Equal(5, after.Count(m => m.AccessCount == 1));
    }

    [Fact]
    public async Task ReinforceCanBeDisabledForBackgroundReads()
    {
        await Repository.SaveAsync(CreateTestMemory("Note", "something", userId: User), Ct);

        await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "something", Scope = MemoryScope.AllFor(User), TopN = 5, Reinforce = false,
        }, Ct);

        var after = await Repository.QueryAsync(MemoryScope.AllFor(User), null, Ct);
        Assert.All(after, m => Assert.Equal(0, m.AccessCount));
    }

    [Fact]
    public async Task CharacterBudget_LimitsThePackedResultSet()
    {
        for (var i = 0; i < 20; i++)
            await Repository.SaveAsync(CreateTestMemory(
                $"Project note {i}", new string('x', 200) + $" project {i}", userId: User), Ct);

        var result = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "project note", Scope = MemoryScope.AllFor(User), TopN = 20,
            CharacterBudget = 700, Reinforce = false,
        }, Ct);

        Assert.NotEmpty(result.Results);
        Assert.True(result.Results.Count < 20);
    }
}
