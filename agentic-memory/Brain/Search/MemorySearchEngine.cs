using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Retrieval;
using AgenticMemory.Brain.Slots;
using AgenticMemory.Brain.Storage;
using AgenticMemory.Configuration;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Brain.Search;

/// <summary>
/// Multi-channel retrieval: scope filter → candidate channels → rank fusion → diversify → pack.
///
/// The ordering is the point. The previous engine ran recall → truncate → filter → rank, which
/// loses results twice over: candidates came from a purely lexical matcher that had already cut
/// the list to <c>topN * 3</c>, so embeddings only ever re-ranked whatever the lexical stage
/// happened to admit, and the archived/tag filters then ran over that truncated list and could
/// return nothing while hundreds of matching memories existed.
///
/// Here the scope predicate runs first and every filter is applied before any limit, so a filtered
/// search can only be empty if the store genuinely holds nothing matching.
/// </summary>
public sealed class MemorySearchEngine : ISearchService
{
    private readonly IMemoryRepository _repository;
    private readonly IEmbeddingService? _embeddingService;
    private readonly MemoryVectorCache _vectorCache;
    private readonly Bm25Ranker _bm25;
    private readonly RetrievalSettings _settings;
    private readonly ILogger<MemorySearchEngine>? _logger;

    public MemorySearchEngine(
        IMemoryRepository repository,
        IEmbeddingService? embeddingService = null,
        RetrievalSettings? settings = null,
        MemoryVectorCache? vectorCache = null,
        MemoryLexicalCache? lexicalCache = null,
        ILogger<MemorySearchEngine>? logger = null)
    {
        _repository       = repository;
        _embeddingService = embeddingService;
        _settings         = settings ?? new RetrievalSettings();
        _vectorCache      = vectorCache ?? new MemoryVectorCache();
        _bm25             = new Bm25Ranker(lexicalCache ?? new MemoryLexicalCache());
        _logger           = logger;
    }

    /// <summary>Compatibility constructor for callers that predate the settings parameter.</summary>
    public MemorySearchEngine(
        IMemoryRepository repository,
        IEmbeddingService? embeddingService,
        ILogger<MemorySearchEngine>? logger)
        : this(repository, embeddingService, null, null, null, logger) { }

    public bool SemanticSearchAvailable => _embeddingService?.IsAvailable ?? false;

    public async Task<IReadOnlyList<ScoredMemory>> SearchAsync(
        string query, int topN = 5, IEnumerable<string>? tags = null, CancellationToken cancellationToken = default)
    {
        var result = await RetrieveAsync(new RetrievalRequest
        {
            Query = query,
            Scope = MemoryScope.Default,
            TopN  = topN,
            Tags  = tags?.ToList(),
        }, cancellationToken);

        return result.Results;
    }

    public async Task<MemoryRetrievalResult> RetrieveAsync(
        RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var query = request.Query?.Trim() ?? string.Empty;

        // ── 1. Scope filter. Indexed, pushed down, applied before anything else. ──────────────
        var candidates = await _repository.QueryAsync(request.Scope, new MemoryQueryOptions
        {
            Tags           = request.Tags,
            Type           = request.Type,
            SubjectRef     = request.SubjectRef,
            Predicate      = SlotRegistry.Normalize(request.Predicate),
            MaxSensitivity = request.MaxSensitivity,
            AsOf           = request.AsOf,
        }, cancellationToken);

        var core = request.IncludeCoreContext ? BuildCoreContext(candidates) : [];

        if (query.Length == 0 || candidates.Count == 0)
        {
            return new MemoryRetrievalResult
            {
                Results              = [],
                CoreContext          = core,
                Confidence           = RetrievalConfidence.None,
                CandidatesConsidered = candidates.Count,
                SemanticSearchUsed   = false,
            };
        }

        var normalizedQuery = query.ToLowerInvariant();

        // One tokenizer for query and document alike. Stopwords are dropped here, not down-weighted:
        // a companion's questions are mostly function words, and matching on them is what previously
        // let unrelated notes outrank the memory that answered the question.
        var queryTerms    = TextAnalysis.Tokenize(query);
        var queryTermSet  = queryTerms.ToHashSet(StringComparer.Ordinal);
        var queryTrigrams = TrigramFuzzyMatcher.GenerateTrigrams(normalizedQuery);

        // ── 2. Query embedding ────────────────────────────────────────────────────────────────
        float[]? queryVector = null;
        string? modelStamp = null;
        var dimensions = 0;

        if (SemanticSearchAvailable)
        {
            try
            {
                queryVector = await _embeddingService!.GetEmbeddingAsync(query, cancellationToken);
                modelStamp  = MemoryTextIndexer.BuildEmbeddingStamp(_embeddingService.ModelId);
                dimensions  = _embeddingService.Dimensions;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Query embedding failed; falling back to lexical channels only");
                queryVector = null;
            }
        }

        // ── 3. Candidate channels ─────────────────────────────────────────────────────────────
        var channels        = new List<RankedChannel>();
        var lexicalScores   = new Dictionary<Guid, double>();
        var semanticScores  = new Dictionary<Guid, double>();
        var incomparable    = 0;

        var lexical = BuildLexicalChannel(candidates, normalizedQuery, queryTerms, queryTrigrams, lexicalScores);
        if (lexical.Count > 0)
            channels.Add(new RankedChannel("lexical", _settings.LexicalChannelWeight, lexical));

        if (queryVector is { Length: > 0 })
        {
            // Scaled to unit length once, to match what the vector cache stores.
            var unitQuery = VectorMath.Normalize(queryVector);
            var known = QueryIsKnownLanguage(queryTerms);
            var vector = BuildVectorChannel(
                candidates, unitQuery, modelStamp, dimensions,
                known ? _settings.MinTopSemanticZScoreForKnownTerms : _settings.MinTopSemanticZScore,
                known, semanticScores, ref incomparable);
            if (vector.Count > 0)
                channels.Add(new RankedChannel("vector", _settings.VectorChannelWeight, vector));
        }

        var slot = BuildSlotChannel(candidates, request, queryTermSet);
        if (slot.Count > 0)
            channels.Add(new RankedChannel("slot", _settings.SlotChannelWeight, slot));

        // No content channel matched: the store genuinely has nothing relevant. Recency and link
        // expansion are boosters and must never manufacture relevance on their own, otherwise a
        // nonsense query would return whatever happens to be recent.
        if (channels.Count == 0)
        {
            return new MemoryRetrievalResult
            {
                Results                = [],
                CoreContext            = core,
                Confidence             = RetrievalConfidence.None,
                CandidatesConsidered   = candidates.Count,
                SemanticSearchUsed     = queryVector is not null,
                IncomparableEmbeddings = incomparable,
            };
        }

        var preliminary = RankFusion.Fuse(channels);

        var recency = BuildRecencyChannel(candidates, preliminary.Keys.ToHashSet());
        if (recency.Count > 0)
            channels.Add(new RankedChannel("recency", _settings.RecencyChannelWeight, recency));

        var links = BuildLinkChannel(candidates, preliminary);
        if (links.Count > 0)
            channels.Add(new RankedChannel("link", _settings.LinkChannelWeight, links));

        // ── 4. Fuse ───────────────────────────────────────────────────────────────────────────
        var fused = RankFusion.Fuse(channels);
        if (fused.Count == 0)
        {
            return new MemoryRetrievalResult
            {
                Results = [], CoreContext = core, Confidence = RetrievalConfidence.None,
                CandidatesConsidered = candidates.Count, SemanticSearchUsed = queryVector is not null,
                IncomparableEmbeddings = incomparable,
            };
        }

        var byId    = candidates.ToDictionary(c => c.Id);
        var maxFused = fused.Values.Max(v => v.Score);
        var now      = DateTime.UtcNow;

        // What this companion has already said about each candidate. One read for the whole set.
        var awareness = await _repository.GetAwarenessAsync(request.Scope, fused.Keys, cancellationToken);

        var scored = fused
            .Where(kv => byId.ContainsKey(kv.Key))
            .Select(kv =>
            {
                var memory = byId[kv.Key];
                var seen   = awareness.GetValueOrDefault(kv.Key);
                var score  = maxFused > 0 ? kv.Value.Score / maxFused : 0;

                return new ScoredMemory
                {
                    Memory                    = memory,
                    Score                     = ApplyNoveltyBias(score, seen, request.NoveltyBias),
                    FuzzyScore                = lexicalScores.GetValueOrDefault(kv.Key),
                    SemanticScore             = semanticScores.TryGetValue(kv.Key, out var s) ? s : null,
                    StrengthScore             = Math.Min(1.0, memory.GetCurrentStrength() / 2.0),
                    RecencyScore              = CalculateRecencyScore(memory, now),
                    MatchedChannels           = kv.Value.Channels,
                    TimesSurfacedToCompanion  = seen?.SurfaceCount ?? 0,
                    LastSurfacedToCompanionAt = seen?.LastSurfacedAt,
                };
            })
            .OrderByDescending(s => s.Score)
            .ToList();

        // ── 5. Diversify, so the result set is not five paraphrases of one fact ───────────────
        var selected = RankFusion.Diversify(
            scored,
            s => s.Score,
            (a, b) => PairwiseSimilarity(a.Memory, b.Memory, modelStamp, dimensions),
            Math.Min(request.TopN, scored.Count),
            request.DiversityLambda);

        // ── 6. Pack to the caller's context budget ────────────────────────────────────────────
        if (request.CharacterBudget is { } budget and > 0)
            selected = PackToBudget(selected, budget);

        // ── 7. Record the retrieval (one write for the whole result set) ──────────────────────
        if (request.Reinforce && _settings.ReinforceOnRead && selected.Count > 0)
        {
            try
            {
                await _repository.ReinforceManyAsync(selected.Select(s => s.Memory.Id), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to reinforce retrieved memories");
            }
        }

        // Attribute the turn to this companion, so the next one knows what she has already said.
        if (request.TrackAwareness && request.Scope.CompanionId is not null && selected.Count > 0)
        {
            try
            {
                await _repository.RecordSurfacedAsync(
                    request.Scope, selected.Select(s => s.Memory.Id), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to record companion awareness");
            }
        }

        // ── 8. Surface open contradictions touching these memories ────────────────────────────
        var touched   = selected.Select(s => s.Memory.Id).ToHashSet();
        var conflicts = (await _repository.GetConflictsAsync(request.Scope, openOnly: true, cancellationToken))
            .Where(c => touched.Contains(c.NewMemoryId) || touched.Contains(c.ExistingMemoryId))
            .ToList();

        return new MemoryRetrievalResult
        {
            Results                = selected,
            CoreContext            = core,
            Conflicts              = conflicts,
            Confidence             = CalculateConfidence(selected),
            CandidatesConsidered   = candidates.Count,
            SemanticSearchUsed     = queryVector is not null,
            IncomparableEmbeddings = incomparable,
        };
    }

    // ── Channels ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lexical matching: BM25F over title, summary, content, tags and the slot predicate, with an
    /// exact-phrase override above it and trigram fuzzy matching underneath it as a typo net.
    ///
    /// Scores are normalised against the best in the set. BM25 values are unbounded and depend on
    /// corpus statistics, so an absolute threshold would mean something different for every query —
    /// the same reason the vector channel gates on distribution shape rather than a fixed cosine.
    /// </summary>
    private List<(Guid, double)> BuildLexicalChannel(
        IReadOnlyList<MemoryNodeEntity> candidates,
        string normalizedQuery,
        IReadOnlyList<string> queryTerms,
        HashSet<string> queryTrigrams,
        Dictionary<Guid, double> scores)
    {
        var bm25 = _bm25.Score(candidates, queryTerms);
        var bestBm25 = bm25.Count > 0 ? bm25.Values.Max() : 0.0;

        var hits = new List<(Guid, double)>();
        var phraseWorthy = normalizedQuery.Length >= 4;

        foreach (var m in candidates)
        {
            var best = bestBm25 > 0 && bm25.TryGetValue(m.Id, out var raw) ? raw / bestBm25 : 0.0;

            // A verbatim phrase hit is stronger evidence than any bag of words, and is the case a
            // user hits when they paste back something they said.
            if (phraseWorthy)
            {
                var text = m.SearchText.Length > 0 ? m.SearchText : m.ContentNormalized;

                if (m.Title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)) best = 1.0;
                else if (text.Contains(normalizedQuery, StringComparison.Ordinal)) best = Math.Max(best, 0.95);
            }

            // Typo tolerance, applied only where the term-based pass found nothing at all. Letting it
            // contribute alongside BM25 is what made every memory in the store a weak lexical match:
            // trigram overlap between two arbitrary English sentences is rarely zero.
            if (best <= 0 && m.Trigrams.Count > 0 && queryTrigrams.Count > 0)
            {
                var stored  = new HashSet<string>(m.Trigrams, StringComparer.OrdinalIgnoreCase);
                // Compared in float: the overlap is a ratio of small integers, and widening it to
                // double puts an exact match like 9/20 a hair below a configured 0.45.
                var overlap = TrigramFuzzyMatcher.CalculateOverlap(queryTrigrams, stored);
                if (overlap >= (float)_settings.MinTrigramSimilarity) best = overlap;
            }

            if (best < _settings.MinLexicalScore) continue;

            scores[m.Id] = best;
            hits.Add((m.Id, best));
        }

        return hits.OrderByDescending(h => h.Item2)
            .Take(_settings.MaxCandidatesPerChannel)
            .ToList();
    }

    /// <summary>
    /// True semantic recall over the whole scoped set, not a re-rank of lexical survivors.
    ///
    /// Admission is decided by how far a match stands out from the rest of the corpus, not by an
    /// absolute cosine. Absolute thresholds do not work with sentence-transformer models: a query
    /// of unknown tokens embeds near the corpus centroid and scores <em>above</em> many genuine
    /// matches, so the only reliable signal that a query means anything is that something separates
    /// from the pack. Memories whose vector is not comparable to the current model are counted and
    /// skipped rather than scored against a meaningless constant.
    /// </summary>
    /// <summary>
    /// Whether the query is written in words the embedding model actually knows.
    ///
    /// This is the one signal about a query that does not depend on the corpus, and it is what
    /// separates "meaningless" from "meaningful but worded differently". A real word has a real
    /// position in the semantic space; invented tokens are reassembled from sub-word fragments and
    /// land near the corpus centroid, where they out-score genuine matches without meaning anything.
    ///
    /// A majority, not all: a genuine question often contains one proper noun the model has never
    /// seen, and that must not condemn the whole query.
    /// </summary>
    private bool QueryIsKnownLanguage(IReadOnlyList<string> queryTerms)
    {
        if (queryTerms.Count == 0 || _embeddingService is null) return false;

        var known = queryTerms.Count(_embeddingService.IsKnownTerm);
        return known > 0 && known * 2 >= queryTerms.Count;
    }

    private List<(Guid, double)> BuildVectorChannel(
        IReadOnlyList<MemoryNodeEntity> candidates,
        float[] queryVector,
        string? modelStamp,
        int dimensions,
        double topGate,
        bool knownLanguage,
        Dictionary<Guid, double> scores,
        ref int incomparable)
    {
        var similarities = new List<(Guid Id, double Cosine)>();

        foreach (var m in candidates)
        {
            if (m.EmbeddingBytes is not { Length: > 0 }) continue;

            var vector = _vectorCache.Get(m, modelStamp, dimensions);
            if (vector is null || !VectorMath.TryUnitSimilarity(queryVector, vector, out var cosine))
            {
                incomparable++;
                continue;
            }

            scores[m.Id] = cosine;
            similarities.Add((m.Id, cosine));
        }

        if (similarities.Count == 0) return [];

        // Too few samples to characterise the distribution — the case a brand-new user is in, where
        // semantic recall matters most because there is nothing else to go on.
        if (similarities.Count < _settings.MinSamplesForSemanticDistribution)
        {
            // With no distribution to reason about, the only signal left is whether the query is
            // real language. If it is not, an absolute floor cannot help: gibberish measured 0.45
            // against this model, above most genuine matches, so any floor low enough to admit real
            // answers admits nonsense first.
            if (!knownLanguage) return [];

            return similarities
                .Where(s => s.Cosine >= _settings.MinSemanticSimilarity)
                .OrderByDescending(s => s.Cosine)
                .Take(_settings.MaxCandidatesPerChannel)
                .Select(s => (s.Id, s.Cosine))
                .ToList();
        }

        var mean = similarities.Average(s => s.Cosine);
        var std  = Math.Sqrt(similarities.Average(s => Math.Pow(s.Cosine - mean, 2)));

        if (std < 1e-6) return [];

        var best = similarities.Max(s => s.Cosine);

        // Nothing stands out: the query carries no discriminating semantic signal for this corpus.
        if ((best - mean) / std < topGate) return [];

        return similarities
            .Where(s => (s.Cosine - mean) / std >= _settings.MinSemanticZScore)
            .OrderByDescending(s => s.Cosine)
            .Take(_settings.MaxCandidatesPerChannel)
            .Select(s => (s.Id, s.Cosine))
            .ToList();
    }

    /// <summary>
    /// Exact structured lookup. When the caller names a slot, or the query mentions a known
    /// predicate, memories asserting that slot are the strongest possible evidence.
    /// </summary>
    private static List<(Guid, double)> BuildSlotChannel(
        IReadOnlyList<MemoryNodeEntity> candidates,
        RetrievalRequest request,
        HashSet<string> queryTerms)
    {
        var requested = SlotRegistry.Normalize(request.Predicate);
        var hits = new List<(Guid, double)>();

        foreach (var m in candidates)
        {
            if (m.Predicate is null) continue;

            if (requested is not null && string.Equals(m.Predicate, requested, StringComparison.Ordinal))
            {
                hits.Add((m.Id, 1.0));
                continue;
            }

            // "what are his favourite foods" → the words of predicate "favourite_food", stemmed
            // through the same analyzer as the query so singular and plural forms meet.
            var parts = m.Predicate
                .Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(TextAnalysis.Stem)
                .ToList();

            if (parts.Count > 0 && parts.All(queryTerms.Contains))
                hits.Add((m.Id, 0.9));
        }

        return hits.OrderByDescending(h => h.Item2).ToList();
    }

    /// <summary>
    /// Recent episodic and affective memories, restricted to items another channel already
    /// surfaced. A booster for "what did we talk about", never a source of relevance by itself.
    /// </summary>
    private static List<(Guid, double)> BuildRecencyChannel(
        IReadOnlyList<MemoryNodeEntity> candidates, HashSet<Guid> alreadyMatched)
    {
        return candidates
            .Where(m => alreadyMatched.Contains(m.Id))
            .Where(m => m.Type is MemoryType.Episodic or MemoryType.Affective or MemoryType.Ephemeral)
            .OrderByDescending(m => m.EventTime ?? m.CreatedAt)
            .Take(50)
            .Select(m => (m.Id, 1.0))
            .ToList();
    }

    /// <summary>Graph expansion from the current best hits — makes LinkedNodeIds functional.</summary>
    private static List<(Guid, double)> BuildLinkChannel(
        IReadOnlyList<MemoryNodeEntity> candidates,
        Dictionary<Guid, (double Score, List<string> Channels)> preliminary)
    {
        if (preliminary.Count == 0) return [];

        var byId = candidates.ToDictionary(c => c.Id);
        var seeds = preliminary.OrderByDescending(kv => kv.Value.Score).Take(10).Select(kv => kv.Key);

        var linked = new List<(Guid, double)>();
        var seen = new HashSet<Guid>();

        foreach (var seedId in seeds)
        {
            if (!byId.TryGetValue(seedId, out var seed)) continue;

            foreach (var linkId in seed.LinkedNodeIds)
            {
                if (preliminary.ContainsKey(linkId) || !seen.Add(linkId)) continue;
                if (byId.ContainsKey(linkId)) linked.Add((linkId, 1.0));
            }
        }

        return linked;
    }

    // ── Support ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Identity and persona facts a companion turn always needs, independent of the query.</summary>
    private static List<ScoredMemory> BuildCoreContext(IReadOnlyList<MemoryNodeEntity> candidates) =>
        candidates
            .Where(m => m.Type.IsCoreContext() || m.IsPinned)
            .OrderByDescending(m => m.Importance)
            .ThenBy(m => m.CreatedAt)
            .Take(20)
            .Select(m => new ScoredMemory
            {
                Memory          = m,
                Score           = 1.0,
                IsCoreContext   = true,
                MatchedChannels = ["core"],
            })
            .ToList();

    private double PairwiseSimilarity(MemoryNodeEntity a, MemoryNodeEntity b, string? modelStamp, int dimensions)
    {
        var va = _vectorCache.Get(a, modelStamp, dimensions);
        var vb = _vectorCache.Get(b, modelStamp, dimensions);

        if (va is not null && vb is not null && VectorMath.TryUnitSimilarity(va, vb, out var cosine))
            return Math.Clamp(cosine, 0, 1);

        if (a.Trigrams.Count == 0 || b.Trigrams.Count == 0) return 0;

        return TrigramFuzzyMatcher.CalculateSimilarity(
            new HashSet<string>(a.Trigrams, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(b.Trigrams, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Discounts a memory this companion has already brought up, with diminishing effect: the step
    /// from never-mentioned to mentioned-once is the one that matters conversationally, and beyond a
    /// handful of repetitions there is nothing further to learn from the count.
    ///
    /// A multiplier bounded below by <c>1 - bias</c>, so even at full strength this reorders equally
    /// relevant memories rather than removing a well-worn fact from the answer.
    /// </summary>
    private static double ApplyNoveltyBias(double score, MemoryAwareness? seen, double bias)
    {
        if (bias <= 0 || seen is null || seen.SurfaceCount <= 0) return score;

        var saturation = 1.0 - 1.0 / (1.0 + seen.SurfaceCount);   // 1 → 0.5, 2 → 0.67, 5 → 0.83
        return score * (1.0 - Math.Clamp(bias, 0, 1) * saturation);
    }

    private const double MaxAgeForRecencyDays = 365.0;

    private static double CalculateRecencyScore(MemoryNodeEntity memory, DateTime now)
    {
        var ageInDays = (now - (memory.EventTime ?? memory.CreatedAt)).TotalDays;
        if (ageInDays >= MaxAgeForRecencyDays) return 0.0;
        return 1.0 - ageInDays / MaxAgeForRecencyDays;
    }

    private static List<ScoredMemory> PackToBudget(List<ScoredMemory> ordered, int budget)
    {
        var packed = new List<ScoredMemory>();
        var used = 0;

        foreach (var s in ordered)
        {
            var cost = s.Memory.Title.Length + s.Memory.Summary.Length + 32;
            if (packed.Count > 0 && used + cost > budget) continue;
            packed.Add(s);
            used += cost;
        }

        return packed;
    }

    /// <summary>
    /// How much to trust the result set. Exposed so a companion can hedge — "I don't remember
    /// exactly, remind me?" is far better behaviour than confabulating a high-ranked weak match.
    /// </summary>
    private static RetrievalConfidence CalculateConfidence(List<ScoredMemory> results)
    {
        if (results.Count == 0) return RetrievalConfidence.None;

        var top = results[0];
        var channels = top.MatchedChannels.Count;
        var semantic = top.SemanticScore ?? 0;

        if (top.MatchedChannels.Contains("slot")) return RetrievalConfidence.High;
        if (channels >= 2 && (semantic >= 0.6 || top.FuzzyScore >= 0.9)) return RetrievalConfidence.High;
        if (channels >= 2 || semantic >= 0.5 || top.FuzzyScore >= 0.9) return RetrievalConfidence.Medium;

        return RetrievalConfidence.Low;
    }
}
