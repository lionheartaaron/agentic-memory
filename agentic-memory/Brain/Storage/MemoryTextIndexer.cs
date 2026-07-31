using System.Text;
using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Search;

namespace AgenticMemory.Brain.Storage;

/// <summary>
/// The single definition of what text is indexed and what text is embedded.
///
/// Previously these were computed in three places that disagreed: the save path indexed
/// title+summary+tags truncated to 800 characters, the reindex path recomputed them from the full
/// content and then had its work silently overwritten on the way to disk, and the embedding was
/// built from a fourth string whose casing depended on which code path last touched the memory.
/// </summary>
public static class MemoryTextIndexer
{
    /// <summary>
    /// LiteDB rejects index keys over 1023 bytes, so the indexed key stays short. Full-text search
    /// uses <see cref="MemoryNodeEntity.SearchText"/>, which is not indexed.
    /// </summary>
    public const int MaxIndexKeyLength = 800;

    /// <summary>
    /// Bumped when the embedding recipe below changes. Folded into the stored model stamp so that
    /// vectors built from an older recipe are treated as incomparable rather than silently mixed.
    /// </summary>
    public const int EmbeddingTextVersion = 2;

    /// <summary>
    /// Recomputes all derived search fields. Call exactly once, on the save path, so that no other
    /// code can compute them differently.
    /// </summary>
    public static void ApplyTextIndex(MemoryNodeEntity node)
    {
        var tagsText = node.Tags.Count > 0 ? " " + string.Join(" ", node.Tags) : string.Empty;

        // Short key: kept under the index limit.
        var indexKey = $"{node.Title} {node.Summary}{tagsText}".ToLowerInvariant().Trim();
        if (indexKey.Length > MaxIndexKeyLength)
            indexKey = indexKey[..MaxIndexKeyLength];
        node.ContentNormalized = indexKey;

        // Full text: content included, no truncation, not indexed.
        var full = new StringBuilder()
            .Append(node.Title).Append(' ')
            .Append(node.Summary).Append(' ')
            .Append(node.Content).Append(tagsText);

        if (!string.IsNullOrWhiteSpace(node.VerbatimQuote))
            full.Append(' ').Append(node.VerbatimQuote);

        node.SearchText = full.ToString().ToLowerInvariant().Trim();

        // Trigrams over the short key only. Generating them over full content produced enormous
        // per-document arrays for negligible benefit; typo tolerance matters on titles and tags.
        node.Trigrams = TrigramFuzzyMatcher.GenerateTrigramList(indexKey);

        Sanitize(node);
    }

    /// <summary>
    /// The text handed to the embedding model. Includes the subject so that "the user's favourite
    /// colour" and "Aria's favourite colour" separate in vector space instead of colliding at a
    /// similarity high enough to trip conflict detection.
    /// </summary>
    public static string BuildEmbeddingText(MemoryNodeEntity node)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(node.SubjectRef) && node.SubjectRef != SubjectRefs.User)
            sb.Append("about ").Append(node.SubjectRef).Append(": ");

        if (!string.IsNullOrWhiteSpace(node.Predicate))
            sb.Append(node.Predicate!.Replace('_', ' ')).Append(": ");

        sb.Append(node.Title);

        if (!string.IsNullOrWhiteSpace(node.Summary))
            sb.Append(". ").Append(node.Summary);

        if (!string.IsNullOrWhiteSpace(node.Content))
            sb.Append(". ").Append(node.Content);

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Stamp recorded alongside a vector. Comparing vectors across different stamps is meaningless,
    /// so the stamp covers both the model and the text recipe.
    /// </summary>
    public static string BuildEmbeddingStamp(string modelId) => $"{modelId}/text-v{EmbeddingTextVersion}";

    /// <summary>
    /// Normalises a slot value so "Acme Corp." and "acme corp" are recognised as the same value,
    /// which is what separates a duplicate from a contradiction.
    /// </summary>
    public static string? BuildValueKey(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return null;

        var sb = new StringBuilder(rawValue.Length);
        foreach (var c in rawValue.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (char.IsWhiteSpace(c) && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }

        return sb.ToString().Trim() is { Length: > 0 } s ? s : null;
    }

    /// <summary>
    /// Strips unpaired surrogates, which LiteDB cannot encode. Valid pairs (emoji) are preserved —
    /// a companion app sees a lot of them.
    /// </summary>
    public static string SanitizeForLiteDb(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;

        var sb = new StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
                {
                    sb.Append(c).Append(input[i + 1]);
                    i++;
                }
                // Orphaned high surrogate: drop.
            }
            else if (!char.IsLowSurrogate(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static void Sanitize(MemoryNodeEntity node)
    {
        node.Title             = SanitizeForLiteDb(node.Title);
        node.Summary           = SanitizeForLiteDb(node.Summary);
        node.Content           = SanitizeForLiteDb(node.Content);
        node.ContentNormalized = SanitizeForLiteDb(node.ContentNormalized);
        node.SearchText        = SanitizeForLiteDb(node.SearchText);

        if (node.VerbatimQuote is not null)
            node.VerbatimQuote = SanitizeForLiteDb(node.VerbatimQuote);

        for (var i = 0; i < node.Tags.Count; i++)
            node.Tags[i] = SanitizeForLiteDb(node.Tags[i]);

        for (var i = 0; i < node.Trigrams.Count; i++)
            node.Trigrams[i] = SanitizeForLiteDb(node.Trigrams[i]);
    }
}
