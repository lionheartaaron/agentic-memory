namespace AgenticMemory.CodeIndex;

/// <summary>
/// Deterministic, multi-factor relevance scorer for code search.
///
/// Final score = Structural(query, record) + Semantic(cosine) + Lexical(rank)
///
/// Structural scores are calibrated so filename-level and parent-directory matches
/// always outrank pure semantic similarity. Beyond the parent directory, path scores
/// decay exponentially and intentionally fall below the semantic ceiling — a match
/// 3+ hops up the tree is weak evidence that should be beaten by a strong embedding.
/// </summary>
internal static class SearchScorer
{
    // ── Structural tiers — name and symbol signals ────────────────────────────

    /// <summary>Filename stem equals query exactly. "Chat.tsx" → query "chat".</summary>
    internal const float ExactFilename      = 1.00f;

    /// <summary>A public or exported symbol has an exact name match.</summary>
    internal const float ExactPublicSymbol  = 0.90f;

    /// <summary>Any symbol (regardless of visibility) has an exact name match.</summary>
    internal const float ExactSymbol        = 0.80f;

    /// <summary>The filename (with extension) contains the query string.</summary>
    internal const float FilenameContains   = 0.65f;

    // ── Structural tiers — distance-based path scoring ────────────────────────
    // For each directory segment (working from parent toward root), score =
    //   PathParentBase                         (distance = 1, parent directory)
    //   PathParentBase * PathDecay^(distance-1) (distance ≥ 2, grandparent+)
    //
    // Example with PathParentBase = 0.50, PathDecay = 0.45:
    //   parent      (d=1): 0.500   > semantic ceiling (0.25) ✓
    //   grandparent (d=2): 0.225   ≈ semantic ceiling — intentionally soft boundary
    //   d=3               : 0.101   below semantic ceiling — semantic can win here
    //   d=4               : 0.045
    //   d=5               : 0.020

    internal const float PathParentBase    = 0.50f;
    internal const float PathDecay         = 0.45f;

    // ── Structural tiers — symbol substring ───────────────────────────────────

    /// <summary>A symbol name contains the query as a substring.</summary>
    internal const float SymbolContains    = 0.30f;

    // ── Semantic contribution ─────────────────────────────────────────────────
    // Cosine ∈ [−1, 1] clamped to [0, 1] and scaled to [0, SemanticCeiling].
    // Ceiling sits below PathParentBase and SymbolContains so that parent-dir
    // and symbol matches always outrank pure embedding similarity.

    internal const float SemanticCeiling   = 0.25f;

    // ── Lexical rank decay ────────────────────────────────────────────────────
    // RRF-style: score = LexicalWeight / (LexicalK + rank)
    //   rank 1  → ≈ 0.016   rank 10 → ≈ 0.014   rank 50 → ≈ 0.011
    // Used to differentiate within the same structural tier and to give candidates
    // found only in the lexical lane a nonzero signal.

    internal const float LexicalWeight     = 1.00f;
    internal const int   LexicalK          = 60;

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the score for the highest-priority structural tier that fires,
    /// or 0 if the query has no structural relationship to this file.
    /// </summary>
    internal static float Structural(string query, CodeIndexRecord r)
    {
        // Exact filename stem
        var stem = Path.GetFileNameWithoutExtension(r.FileName);
        if (stem.Equals(query, StringComparison.OrdinalIgnoreCase))
            return ExactFilename;

        // Exact symbol name — visibility weighted
        foreach (var sym in r.Symbols)
        {
            if (!sym.Name.Equals(query, StringComparison.OrdinalIgnoreCase)) continue;
            return sym.Accessibility is "public" or "exported" or "export"
                ? ExactPublicSymbol
                : ExactSymbol;
        }

        // Filename contains
        if (r.FileName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return FilenameContains;

        // Distance-based directory segment match.
        // Walk from parent (i = length-2) toward root (i = 0).
        // Only exact segment equality counts — substring checks on the full path
        // cause false positives when the workspace folder name contains the query
        // (e.g. every file in "agentic-memory\" would match query "memory").
        var segments = r.RelativePath.Split(
            ['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        for (var i = segments.Length - 2; i >= 0; i--)
        {
            if (!segments[i].Equals(query, StringComparison.OrdinalIgnoreCase)) continue;
            var distance = (segments.Length - 2) - i; // 0-based: 0 = parent, 1 = grandparent, …
            return distance == 0
                ? PathParentBase
                : PathParentBase * MathF.Pow(PathDecay, distance);
        }

        // Symbol name contains
        if (r.Symbols.Any(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            return SymbolContains;

        return 0f;
    }

    /// <summary>Scales cosine similarity to [0, SemanticCeiling].</summary>
    internal static float Semantic(float cosine)
        => MathF.Max(0f, cosine) * SemanticCeiling;

    /// <summary>RRF-style rank decay for the lexical lane.</summary>
    internal static float Lexical(int rank)
        => LexicalWeight / (LexicalK + rank);

    /// <summary>Adds all three components to produce the final relevance score.</summary>
    internal static float Combine(float structural, float semantic, float lexical)
        => structural + semantic + lexical;
}
