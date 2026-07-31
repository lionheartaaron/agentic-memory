using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Retrieval;
using AgenticMemory.Brain.Search;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// The analyzer and the BM25F ranker beneath the lexical channel.
///
/// These are unit tests on purpose: the ranking behaviours they pin down are invisible end-to-end,
/// because the semantic channel usually rescues a bad lexical result. When it does not — no
/// embedding model, or a query the model has no purchase on — the lexical channel is the whole
/// system, and this is the level at which its failures are legible.
/// </summary>
public class LexicalRankingTests : MemoryServiceTestBase
{
    private const string User = "aaron";

    // ── Analyzer ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StopWordsAreRemovedSoAQuestionReducesToItsContentWords()
    {
        var terms = TextAnalysis.Tokenize("what does he like to eat");

        Assert.DoesNotContain("what", terms);
        Assert.DoesNotContain("does", terms);
        Assert.DoesNotContain("he", terms);
        Assert.Contains("like", terms);
        Assert.Contains("eat", terms);
    }

    [Fact]
    public void PluralsAndSingularsMeetAfterStemming()
    {
        Assert.Equal(TextAnalysis.Stem("sisters"), TextAnalysis.Stem("sister"));
        Assert.Equal(TextAnalysis.Stem("hobbies"), TextAnalysis.Stem("hobby"));
        Assert.Equal(TextAnalysis.Stem("walked"),  TextAnalysis.Stem("walk"));
    }

    /// <summary>Over-stemming costs precision a small corpus cannot spare.</summary>
    [Fact]
    public void StemmingDoesNotConflateUnrelatedWords()
    {
        Assert.NotEqual(TextAnalysis.Stem("university"), TextAnalysis.Stem("universal"));
        Assert.NotEqual(TextAnalysis.Stem("business"),   TextAnalysis.Stem("busy"));
    }

    [Fact]
    public void ApostrophesJoinRatherThanSplit()
    {
        var terms = TextAnalysis.Tokenize("the user doesn't like it", removeStopWords: false, stem: false);

        Assert.Contains("doesnt", terms);
        Assert.DoesNotContain("t", terms);
    }

    /// <summary>Dropping "not" as a stopword would make a statement and its denial identical.</summary>
    [Fact]
    public void NegationSurvivesTokenization()
    {
        Assert.True(TextAnalysis.ContainsNegation("the user does not drink coffee"));
        Assert.True(TextAnalysis.ContainsNegation("the user doesn't drink coffee"));
        Assert.True(TextAnalysis.ContainsNegation("the user stopped drinking coffee"));
        Assert.False(TextAnalysis.ContainsNegation("the user drinks coffee every morning"));
    }

    [Fact]
    public void TheContentSkeletonIgnoresPolarity()
    {
        var affirmed = TextAnalysis.ContentSkeleton("the user drinks coffee");
        var denied   = TextAnalysis.ContentSkeleton("the user does not drink coffee");

        Assert.Equal(affirmed.OrderBy(x => x), denied.OrderBy(x => x));
    }

    // ── BM25F ───────────────────────────────────────────────────────────────────────────────

    private MemoryNodeEntity Doc(string title, string summary, string content = "") =>
        CreateTestMemory(title, summary, content, userId: User);

    /// <summary>
    /// The property the previous scorer lacked: a term appearing in one memory out of many counts
    /// for far more than one appearing in most of them.
    /// </summary>
    [Fact]
    public void ARareTermOutweighsACommonOne()
    {
        var rare   = Doc("Note", "the user mentioned tonkotsu once");
        var common = Doc("Note", "the user mentioned project project project");

        var corpus = new List<MemoryNodeEntity> { rare, common };
        for (var i = 0; i < 30; i++)
            corpus.Add(Doc($"Filler {i}", "another note about the project"));

        var scores = new Bm25Ranker().Score(corpus, ["tonkotsu", "project"]);

        Assert.True(scores[rare.Id] > scores[common.Id],
            $"rare-term match scored {scores[rare.Id]:F3}, repeated common term {scores[common.Id]:F3}");
    }

    [Fact]
    public void ATitleHitOutweighsTheSameWordBuriedInTheBody()
    {
        var titled = Doc("Bouldering", "a note", "nothing relevant here");
        var buried = Doc("A note", "a note", "the user mentioned bouldering somewhere in a long body of text");

        var corpus = new List<MemoryNodeEntity> { titled, buried };
        var scores = new Bm25Ranker().Score(corpus, ["boulder"]);

        Assert.True(scores[titled.Id] > scores[buried.Id]);
    }

    [Fact]
    public void ATermInNoDocumentContributesNothing()
    {
        var corpus = new List<MemoryNodeEntity> { Doc("Note", "the user cycles to work") };

        Assert.Empty(new Bm25Ranker().Score(corpus, ["xyznonexistent123"]));
    }

    [Fact]
    public void TheSlotPredicateIsSearchableEvenWhenTheProseNeverSaysIt()
    {
        var slotted = CreateTestMemory(
            "Acme", "the user is there five days a week", userId: User, predicate: "employer", value: "acme");

        var scores = new Bm25Ranker().Score([slotted], ["employer"]);

        Assert.True(scores.ContainsKey(slotted.Id));
    }

    // ── End to end ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The failure the old scorer produced: with only stopwords in common, an unrelated note ranked
    /// alongside the memory that answered the question. Rank fusion consumes ranks, so that noise
    /// displaced the right answer rather than merely failing to find it.
    /// </summary>
    [Fact]
    public async Task StopWordOverlapAloneDoesNotMakeAMemoryRelevant()
    {
        await Repository.SaveAsync(CreateTestMemory(
            "Friendship", "The user's best friend is called Sam", userId: User), Ct);

        for (var i = 0; i < 40; i++)
            await Repository.SaveAsync(CreateTestMemory(
                $"Note {i}", $"This is a note and it is about something else entirely, number {i}", userId: User), Ct);

        var result = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "who is his closest friend", Scope = MemoryScope.AllFor(User), TopN = 3, Reinforce = false,
        }, Ct);

        Assert.Equal("Friendship", result.Results[0].Memory.Title);
    }

    [Fact]
    public async Task AVerbatimPhraseOutranksAPartialTermMatch()
    {
        await Repository.SaveAsync(CreateTestMemory(
            "Exact", "the user said the coffee machine is broken again", userId: User), Ct);
        await Repository.SaveAsync(CreateTestMemory(
            "Partial", "the coffee is fine and the machine works", userId: User), Ct);

        var result = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "the coffee machine is broken", Scope = MemoryScope.AllFor(User), TopN = 2, Reinforce = false,
        }, Ct);

        Assert.Equal("Exact", result.Results[0].Memory.Title);
    }

    [Fact]
    public async Task ATypoStillFindsTheMemory()
    {
        await Repository.SaveAsync(CreateTestMemory(
            "Bouldering", "The user goes bouldering on Saturdays", userId: User), Ct);

        var result = await SearchService.RetrieveAsync(new RetrievalRequest
        {
            Query = "bouldring saturdys", Scope = MemoryScope.AllFor(User), TopN = 5, Reinforce = false,
        }, Ct);

        Assert.Contains(result.Results, r => r.Memory.Title == "Bouldering");
    }
}
