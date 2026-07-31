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
}
