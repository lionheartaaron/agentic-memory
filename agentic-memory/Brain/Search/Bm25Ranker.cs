using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Retrieval;

namespace AgenticMemory.Brain.Search;

/// <summary>
/// BM25F ranking over the scope-filtered candidate set.
///
/// This replaces a cascade of substring tests whose score was, in effect, "what fraction of the
/// query's words appear anywhere in this document". That measure has no notion of how informative a
/// word is, so in a query like "who is his closest friend" the memory recording the user's best
/// friend scored no better than any note containing the word "is" — and because rank fusion consumes
/// <em>ranks</em>, a channel full of confident nonsense actively displaces the correct answer rather
/// than merely failing to find it.
///
/// BM25's inverse document frequency term is what fixes that: a term appearing in one memory out of
/// three hundred dominates one appearing in half of them. The F (fielded) part weights a hit in the
/// title above one buried in the body, using the standard approximation of scaling term frequencies
/// per field before a single saturation curve is applied — so a long document cannot win by repeating
/// a term, and a short title match is not drowned out.
/// </summary>
public sealed class Bm25Ranker
{
    // Standard defaults. k1 controls how quickly term frequency saturates, b how strongly length is
    // normalised. These are the values BM25 is almost always deployed with; the corpus here is short
    // documents, where the saturation curve matters far more than the exact constant.
    private const double K1 = 1.2;
    private const double B = 0.75;

    private const double TitleWeight = 3.0;
    private const double SummaryWeight = 2.0;
    private const double TagWeight = 2.5;
    private const double ContentWeight = 1.0;

    private readonly MemoryLexicalCache _cache;

    public Bm25Ranker(MemoryLexicalCache? cache = null) => _cache = cache ?? new MemoryLexicalCache();

    /// <summary>A memory's weighted term frequencies and effective length.</summary>
    public sealed record Document(Dictionary<string, double> Terms, double Length)
    {
        public static readonly Document Empty = new([], 0);
    }

    /// <summary>
    /// Builds the fielded term-frequency map for a memory. Public so the cache and the tests can
    /// share exactly one definition of what "the indexed text of a memory" means.
    /// </summary>
    public static Document Analyze(MemoryNodeEntity memory)
    {
        var terms = new Dictionary<string, double>(StringComparer.Ordinal);

        Accumulate(terms, memory.Title, TitleWeight);
        Accumulate(terms, memory.Summary, SummaryWeight);
        Accumulate(terms, memory.Content, ContentWeight);

        foreach (var tag in memory.Tags)
            Accumulate(terms, tag, TagWeight);

        // The subject and slot are part of what a memory says, and a caller asking "what is Aria's
        // favourite colour" should reach a memory whose predicate is favourite_colour even when the
        // prose never spells it out.
        Accumulate(terms, memory.Predicate?.Replace('_', ' '), SummaryWeight);

        var length = terms.Values.Sum();
        return new Document(terms, length);
    }

    private static void Accumulate(Dictionary<string, double> terms, string? text, double weight)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        foreach (var token in TextAnalysis.Tokenize(text))
            terms[token] = terms.GetValueOrDefault(token) + weight;
    }

    /// <summary>
    /// Scores every candidate against the query terms.
    ///
    /// Returns raw BM25 scores. They are unbounded and corpus-dependent by nature, so the caller
    /// normalises against the best score in the set rather than comparing them to a fixed threshold —
    /// the same reasoning that rules out an absolute cosine cut-off for the vector channel.
    /// </summary>
    public Dictionary<Guid, double> Score(IReadOnlyList<MemoryNodeEntity> candidates, IReadOnlyList<string> queryTerms)
    {
        var scores = new Dictionary<Guid, double>();
        if (candidates.Count == 0 || queryTerms.Count == 0) return scores;

        var documents = new Document[candidates.Count];
        var totalLength = 0.0;

        for (var i = 0; i < candidates.Count; i++)
        {
            documents[i] = _cache.Get(candidates[i]);
            totalLength += documents[i].Length;
        }

        var averageLength = totalLength / candidates.Count;
        if (averageLength <= 0) return scores;

        var distinctTerms = queryTerms.Distinct(StringComparer.Ordinal).ToList();

        // Document frequency per query term, over this candidate set. Computing it over the scoped
        // set rather than the whole store is deliberate: informativeness is relative to what this
        // user's companion can actually see.
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in distinctTerms)
        {
            var df = 0;
            foreach (var document in documents)
                if (document.Terms.ContainsKey(term)) df++;

            documentFrequency[term] = df;
        }

        var n = candidates.Count;

        for (var i = 0; i < candidates.Count; i++)
        {
            var document = documents[i];
            if (document.Length <= 0) continue;

            var score = 0.0;

            foreach (var term in distinctTerms)
            {
                if (!document.Terms.TryGetValue(term, out var tf)) continue;

                var df = documentFrequency[term];
                if (df == 0) continue;

                // Probabilistic IDF, smoothed so a term present in more than half the corpus still
                // contributes a small positive amount instead of turning negative.
                var idf = Math.Log(1.0 + (n - df + 0.5) / (df + 0.5));

                var denominator = tf + K1 * (1 - B + B * document.Length / averageLength);
                score += idf * (tf * (K1 + 1)) / denominator;
            }

            if (score > 0) scores[candidates[i].Id] = score;
        }

        return scores;
    }
}
