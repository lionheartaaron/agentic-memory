namespace AgenticMemory.Brain.Search;

/// <summary>
/// The single tokenizer used by every lexical path — query parsing, document indexing and the
/// negation detector.
///
/// Having one definition matters more than the details of any one rule: when the query is split by
/// one set of delimiters and the document by another, terms that should match silently do not, and
/// the failure is invisible because the result set is merely worse rather than wrong.
/// </summary>
public static class TextAnalysis
{
    /// <summary>
    /// Terms carrying no discriminating power in a companion's memory corpus.
    ///
    /// These are removed before scoring rather than merely down-weighted. Almost every question a
    /// user asks is mostly function words ("what does he like to eat" is five stopwords and one
    /// content word), so a matcher that counts them rewards long documents for containing "the" and
    /// buries the one memory that actually answers the question.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "about", "after", "again", "all", "am", "an", "and", "any", "are", "as", "at",
        "be", "because", "been", "before", "being", "between", "both", "but", "by",
        "can", "cannot", "could", "did", "do", "does", "doing", "down", "during",
        "each", "few", "for", "from", "further", "had", "has", "have", "having",
        "he", "her", "here", "hers", "herself", "him", "himself", "his", "how",
        "i", "if", "in", "into", "is", "it", "its", "itself", "just",
        "me", "more", "most", "my", "myself", "no", "nor", "now",
        "of", "off", "on", "once", "only", "or", "other", "our", "ours", "ourselves", "out", "over", "own",
        "same", "she", "should", "so", "some", "such",
        "than", "that", "the", "their", "theirs", "them", "themselves", "then", "there", "these",
        "they", "this", "those", "through", "to", "too",
        "under", "until", "up", "us", "very", "was", "we", "were", "what", "when", "where",
        "which", "while", "who", "whom", "why", "will", "with", "would",
        "you", "your", "yours", "yourself", "yourselves",
    };

    /// <summary>
    /// Words that flip the polarity of a statement. Used by contradiction detection, and never
    /// removed as stopwords — dropping "not" would make a statement and its denial identical.
    /// </summary>
    public static readonly HashSet<string> NegationMarkers = new(StringComparer.Ordinal)
    {
        "not", "never", "no", "none", "nothing", "nobody", "neither", "nor",
        "dont", "doesnt", "didnt", "isnt", "arent", "wasnt", "werent",
        "cant", "cannot", "couldnt", "wont", "wouldnt", "shouldnt", "hasnt", "havent", "hadnt",
        "stopped", "quit", "former", "ex", "longer",
    };

    /// <summary>
    /// Words that stand in for "any member at all" instead of naming one.
    ///
    /// These separate the two kinds of denial that a bag of words otherwise makes identical. "Not
    /// allergic to bears" denies one value and leaves the rest of the set alone; "not allergic to
    /// anything" claims the set is empty, which every later value contradicts outright. Only the
    /// second is safe to replace automatically, so the distinction has to be visible here.
    ///
    /// Held in stemmed form because <see cref="ContentSkeleton"/> stems: "anything" reaches this set
    /// as "anyth", and a raw list would silently never match.
    /// </summary>
    public static readonly HashSet<string> UniversalPlaceholders =
        new[]
        {
            "anything", "anyone", "anybody", "any", "all", "known", "whatsoever",
            "nothing", "none", "nobody", "everything", "everyone", "else", "specific", "particular",
        }
        .Select(Stem)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>Shortest prefix two tokens must share to count as forms of one root.</summary>
    private const int RootPrefixLength = 5;

    public static bool IsStopWord(string term) => StopWords.Contains(term);

    /// <summary>
    /// Splits on anything that is not a letter or digit, lowercases, and stems. Apostrophes are
    /// dropped rather than split on, so "doesn't" becomes the single token "doesnt" and stays
    /// recognisable as a negation.
    /// </summary>
    public static List<string> Tokenize(string? text, bool removeStopWords = true, bool stem = true)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text)) return tokens;

        var buffer = new System.Text.StringBuilder(24);

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer.Append(char.ToLowerInvariant(ch));
                continue;
            }

            // Apostrophes join rather than split: "user's" → "users", "doesn't" → "doesnt".
            if (ch is '\'' or '’') continue;

            Flush(buffer, tokens, removeStopWords, stem);
        }

        Flush(buffer, tokens, removeStopWords, stem);
        return tokens;
    }

    private static void Flush(
        System.Text.StringBuilder buffer, List<string> tokens, bool removeStopWords, bool stem)
    {
        if (buffer.Length == 0) return;

        var raw = buffer.ToString();
        buffer.Clear();

        if (raw.Length < 2) return;

        // Function-word negations ("not", "no", "cannot") are dropped here like any other stopword:
        // as retrieval terms they match half the corpus and discriminate nothing. Polarity detection
        // does not go through this path — it tokenizes with removeStopWords off, so it still sees
        // every marker.
        if (removeStopWords && StopWords.Contains(raw)) return;

        tokens.Add(stem ? Stem(raw) : raw);
    }

    /// <summary>
    /// Deliberately conservative suffix stripping — plurals and the commonest verb inflections only.
    ///
    /// It exists so "any brothers or sisters" can reach "the user has an older sister", which is the
    /// realistic shape of a companion's recall: the user asks in the plural about something recorded
    /// in the singular. A full Porter stemmer conflates far more aggressively ("universal" and
    /// "university" collapse together), which costs precision that a small corpus cannot spare.
    /// </summary>
    public static string Stem(string term)
    {
        if (term.Length <= 3) return term;

        if (term.EndsWith("ies", StringComparison.Ordinal) && term.Length > 4)
            return term[..^3] + "y";

        if (term.EndsWith("sses", StringComparison.Ordinal))
            return term[..^2];

        if (term.EndsWith("ss", StringComparison.Ordinal))
            return term;

        if (term.EndsWith('s') && !term.EndsWith("us", StringComparison.Ordinal)
                               && !term.EndsWith("is", StringComparison.Ordinal))
            return term[..^1];

        if (term.EndsWith("ing", StringComparison.Ordinal) && term.Length > 5)
            return Undouble(term[..^3]);

        if (term.EndsWith("ed", StringComparison.Ordinal) && term.Length > 4)
            return Undouble(term[..^2]);

        return term;
    }

    /// <summary>"running" → "runn" → "run". Only for the doubled-consonant case.</summary>
    private static string Undouble(string stem)
    {
        if (stem.Length < 3) return stem;

        var last = stem[^1];
        if (last == stem[^2] && last is not ('l' or 's' or 'e' or 'o'))
            return stem[..^1];

        return stem;
    }

    /// <summary>
    /// True when the text asserts a denial. Matched against unstemmed tokens: stemming turns
    /// "stopped" into "stop" and would take it out of the marker set.
    /// </summary>
    public static bool ContainsNegation(string? text) =>
        Tokenize(text, removeStopWords: false, stem: false).Any(NegationMarkers.Contains);

    /// <summary>Content tokens with any negation markers removed — the polarity-free skeleton of a
    /// statement, so "the user likes coffee" and "the user does not like coffee" compare as the
    /// same claim with opposite signs.</summary>
    public static List<string> ContentSkeleton(string? text) =>
        Tokenize(text).Where(t => !NegationMarkers.Contains(t)).ToList();

    /// <summary>
    /// True when <paramref name="skeleton"/> names no value of its own — everything in it is either
    /// the topic the two statements share or a placeholder standing in for "any at all".
    ///
    /// This is the wording half of a negative existential. "The user is not allergic to anything"
    /// against "the user is allergic to bears" leaves the residue {anything}, which names nothing;
    /// "the user is not allergic to bears" against "the user is allergic to peanuts" leaves {bears},
    /// which names a value and is therefore an ordinary denial rather than a claim of emptiness.
    ///
    /// Topic membership is matched by root as well as exactly, because the two halves of this
    /// comparison routinely use different parts of speech for the same idea — the slot is
    /// "allergies", the sentence says "allergic", and the conservative stemmer here deliberately
    /// does not conflate them.
    /// </summary>
    public static bool NamesNoSpecificValue(IEnumerable<string> skeleton, IReadOnlyCollection<string> topic)
    {
        foreach (var term in skeleton)
        {
            if (UniversalPlaceholders.Contains(term)) continue;
            if (topic.Contains(term)) continue;
            if (topic.Any(t => SharesRoot(t, term))) continue;

            return false;
        }

        return true;
    }

    /// <summary>"allergies" and "allergic" are the same idea in different parts of speech.</summary>
    private static bool SharesRoot(string a, string b) =>
        a.Length >= RootPrefixLength && b.Length >= RootPrefixLength &&
        a.AsSpan(0, RootPrefixLength).SequenceEqual(b.AsSpan(0, RootPrefixLength));
}
