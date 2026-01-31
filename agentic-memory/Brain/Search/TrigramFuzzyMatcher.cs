namespace AgenticMemory.Brain.Search;

/// <summary>
/// Trigram-based fuzzy text matching for typo tolerance
/// Uses Jaccard similarity coefficient over character trigrams
/// </summary>
public static class TrigramFuzzyMatcher
{
    /// <summary>
    /// Generate trigrams from text for fuzzy matching
    /// </summary>
    /// <param name="text">Input text</param>
    /// <returns>Set of trigrams</returns>
    public static HashSet<string> GenerateTrigrams(string text)
    {
        var trigrams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(text))
            return trigrams;

        // Normalize: lowercase and pad with spaces for edge trigrams
        var normalized = $"  {text.ToLowerInvariant().Trim()}  ";

        // Generate overlapping trigrams
        for (int i = 0; i <= normalized.Length - 3; i++)
        {
            var trigram = normalized.Substring(i, 3);
            // Skip pure whitespace trigrams
            if (!string.IsNullOrWhiteSpace(trigram))
            {
                trigrams.Add(trigram);
            }
        }

        return trigrams;
    }

    /// <summary>
    /// Generate trigrams as a list (for storage)
    /// </summary>
    public static List<string> GenerateTrigramList(string text)
    {
        return [.. GenerateTrigrams(text)];
    }

    /// <summary>
    /// Calculate Jaccard similarity between two strings using trigrams
    /// </summary>
    /// <param name="a">First string</param>
    /// <param name="b">Second string</param>
    /// <returns>Similarity score between 0.0 and 1.0</returns>
    public static float CalculateSimilarity(string a, string b)
    {
        var trigramsA = GenerateTrigrams(a);
        var trigramsB = GenerateTrigrams(b);

        return CalculateSimilarity(trigramsA, trigramsB);
    }

    /// <summary>
    /// Calculate Jaccard similarity between two trigram sets
    /// </summary>
    public static float CalculateSimilarity(HashSet<string> trigramsA, HashSet<string> trigramsB)
    {
        if (trigramsA.Count == 0 || trigramsB.Count == 0)
            return 0f;

        var intersection = trigramsA.Intersect(trigramsB, StringComparer.OrdinalIgnoreCase).Count();
        var union = trigramsA.Union(trigramsB, StringComparer.OrdinalIgnoreCase).Count();

        if (union == 0)
            return 0f;

        return (float)intersection / union;
    }

    /// <summary>
    /// Calculate similarity between a query and a list of stored trigrams
    /// </summary>
    public static float CalculateSimilarity(string query, List<string> storedTrigrams)
    {
        var queryTrigrams = GenerateTrigrams(query);
        var storedSet = new HashSet<string>(storedTrigrams, StringComparer.OrdinalIgnoreCase);

        return CalculateSimilarity(queryTrigrams, storedSet);
    }

    /// <summary>
    /// Check if similarity exceeds a threshold
    /// </summary>
    public static bool IsSimilar(string a, string b, float threshold = 0.3f)
    {
        return CalculateSimilarity(a, b) >= threshold;
    }
}
