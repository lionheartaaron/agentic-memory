using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Search;
using AgenticMemory.Brain.Slots;
using AgenticMemory.Brain.Storage;
using AgenticMemory.Configuration;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Brain.Conflict;

/// <summary>
/// Stores memories with contradiction handling.
///
/// Two changes from the previous design carry most of the weight:
///
///   * Candidates for comparison come from an exact (subject, predicate) lookup plus a semantic
///     scan over the whole scoped set. Previously they came from a lexical search truncated to ten
///     results, so at any real corpus size whether a duplicate was noticed was close to chance.
///   * Whether one memory may replace another is decided by <see cref="SupersedeGate"/>, not by a
///     similarity threshold. Contradictions that are not safe to resolve automatically are recorded
///     and both sides stay active.
///
/// Everything is committed as a single atomic batch, so a crash cannot leave the old fact archived
/// with its replacement missing.
/// </summary>
public sealed class ConflictAwareStorageService : IConflictAwareStorage
{
    private readonly IMemoryRepository _repository;
    private readonly IEmbeddingService? _embeddingService;
    private readonly ConflictSettings _settings;
    private readonly SupersedeGate _gate;
    private readonly SlotRegistry _slots;
    private readonly ILogger<ConflictAwareStorageService>? _logger;

    public ConflictAwareStorageService(
        IMemoryRepository repository,
        IEmbeddingService? embeddingService,
        ConflictSettings settings,
        SlotRegistry? slots = null,
        ILogger<ConflictAwareStorageService>? logger = null)
    {
        _repository       = repository;
        _embeddingService = embeddingService;
        _settings         = settings;
        _slots            = slots ?? new SlotRegistry();
        _gate             = new SupersedeGate(_slots);
        _logger           = logger;
    }

    /// <summary>Compatibility constructor matching the previous parameter order.</summary>
    public ConflictAwareStorageService(
        IMemoryRepository repository,
        ISearchService searchService,
        IEmbeddingService? embeddingService,
        ConflictSettings settings,
        ILogger<ConflictAwareStorageService>? logger = null)
        : this(repository, embeddingService, settings, null, logger) { }

    public Task<StoreResult> StoreAsync(MemoryNodeEntity entity, CancellationToken cancellationToken = default)
        => StoreAsync(entity, MemoryScope.Default, "storage:default", cancellationToken);

    public async Task<StoreResult> StoreAsync(
        MemoryNodeEntity entity, MemoryScope scope, string actor, CancellationToken cancellationToken = default)
    {
        Normalize(entity, scope);
        await EnsureEmbeddingAsync(entity, cancellationToken);

        var candidates = await GatherCandidatesAsync(entity, scope, cancellationToken);

        // A restatement of something already recorded — identical title and summary, or a vector
        // near-identical to an existing memory in the same subject and scope. Handled before the
        // gate because it is not a contradiction at all, whatever slot is involved.
        var restatement = candidates.FirstOrDefault(c => c.IsRestatement);
        if (restatement is not null)
            return await ReinforceDuplicateAsync(entity, restatement.Memory, actor, cancellationToken);

        var superseded = new List<MemoryNodeEntity>();
        var conflicts  = new List<MemoryConflict>();
        MemoryNodeEntity? duplicate = null;

        foreach (var (existing, similarity, _) in candidates)
        {
            var verdict = _gate.Evaluate(entity, existing);

            switch (verdict.Decision)
            {
                case SupersedeDecision.Duplicate:
                    duplicate ??= existing;
                    break;

                // Free text the slot gate has no jurisdiction over. A statement and its denial would
                // otherwise both stay active and be returned together, leaving the companion to pick
                // one at random.
                case SupersedeDecision.Coexist
                    when PolarityDetector.IsPolarityContradiction(
                        entity, existing, similarity > 0 ? similarity : null, out var polarityReason):
                    conflicts.Add(BuildConflict(
                        entity, existing, ConflictKind.PolarityContradiction, polarityReason, scope));
                    break;

                case SupersedeDecision.Supersede when _settings.AutoSupersedeEnabled:
                    superseded.Add(existing);
                    break;

                case SupersedeDecision.Supersede:
                    conflicts.Add(BuildConflict(entity, existing, ConflictKind.ValueReplaced,
                        "auto-supersede disabled: " + verdict.Reason, scope));
                    break;

                case SupersedeDecision.Conflict:
                    conflicts.Add(BuildConflict(entity, existing,
                        verdict.ConflictKind ?? ConflictKind.ValueReplaced, verdict.Reason, scope));
                    break;
            }
        }

        // A restatement of something already known: reinforce rather than duplicate.
        if (duplicate is not null && superseded.Count == 0)
            return await ReinforceDuplicateAsync(entity, duplicate, actor, cancellationToken);

        entity.ValidFrom     = DateTime.UtcNow;
        entity.IngestedAt    = DateTime.UtcNow;
        entity.SupersededIds = superseded.Select(m => m.Id).ToList();

        // One atomic unit: the replacement and every archival it causes commit together.
        var batch = new MemoryWriteBatch().Upsert(entity);
        var now = DateTime.UtcNow;

        foreach (var old in superseded)
            batch.ChangeState(old.Id, MemoryState.Superseded, entity.Id, now, "superseded by newer value");

        foreach (var conflict in conflicts)
            batch.RecordConflict(conflict);

        await _repository.ExecuteAsync(batch, actor, cancellationToken);

        if (superseded.Count > 0)
        {
            var titles = string.Join(", ", superseded.Select(m => $"'{m.Title}'"));
            _logger?.LogInformation(
                "Memory '{Title}' superseded {Count} memories on slot '{Predicate}'",
                entity.Title, superseded.Count, entity.Predicate);

            return new StoreResult
            {
                Memory             = entity,
                Action             = StoreAction.StoredWithSupersede,
                SupersededMemories = superseded,
                Conflicts          = conflicts,
                Message = $"Memory stored. Superseded {superseded.Count} previous " +
                          $"memor{(superseded.Count == 1 ? "y" : "ies")}: {titles}.",
            };
        }

        if (conflicts.Count > 0)
        {
            return new StoreResult
            {
                Memory    = entity,
                Action    = StoreAction.StoredWithConflict,
                Conflicts = conflicts,
                Message = $"Memory stored. {conflicts.Count} contradiction" +
                          $"{(conflicts.Count == 1 ? "" : "s")} recorded and left for confirmation: " +
                          string.Join("; ", conflicts.Select(c => c.Description)),
            };
        }

        if (candidates.Count > 0)
        {
            return new StoreResult
            {
                Memory  = entity,
                Action  = StoreAction.StoredCoexist,
                Message = $"Memory stored alongside {candidates.Count} related memor" +
                          $"{(candidates.Count == 1 ? "y" : "ies")}.",
            };
        }

        return new StoreResult
        {
            Memory  = entity,
            Action  = StoreAction.StoredNew,
            Message = "Memory stored successfully.",
        };
    }

    // ── Candidate generation ──────────────────────────────────────────────────────────────────

    private sealed record CandidateMatch(MemoryNodeEntity Memory, double Similarity, bool IsRestatement);

    /// <summary>
    /// Memories worth comparing against.
    ///
    /// Three complementary sources over a single scan of the scoped set: an exact slot lookup,
    /// which is deterministic and complete; an exact title-and-summary match, which catches
    /// restatements even with no embedding service available; and a semantic scan across the whole
    /// scoped set. The previous implementation asked a lexical search for ten results, so at any
    /// real corpus size noticing a duplicate was largely a matter of luck.
    /// </summary>
    private async Task<List<CandidateMatch>> GatherCandidatesAsync(
        MemoryNodeEntity entity, MemoryScope scope, CancellationToken cancellationToken)
    {
        var found = new Dictionary<Guid, CandidateMatch>();

        if (entity.Predicate is not null)
        {
            var slotMatches = await _repository.GetBySlotAsync(
                scope, entity.SubjectRef, entity.Predicate, includeHistory: false, cancellationToken);

            foreach (var m in slotMatches)
                if (m.Id != entity.Id) found[m.Id] = new CandidateMatch(m, 0, false);
        }

        var vector = entity.GetEmbedding();
        var stamp  = _embeddingService is null
            ? null
            : MemoryTextIndexer.BuildEmbeddingStamp(_embeddingService.ModelId);

        var active     = await _repository.GetActiveAsync(scope, cancellationToken);
        var identity   = RestatementKey(entity);
        var semantic   = new List<CandidateMatch>();

        foreach (var m in active)
        {
            if (m.Id == entity.Id) continue;

            // A restatement must be about the same subject and visible to exactly the same
            // audience — otherwise "reinforcing" it would quietly discard a differently-scoped fact.
            var sameContext = string.Equals(m.SubjectRef, entity.SubjectRef, StringComparison.OrdinalIgnoreCase)
                              && m.Visibility == entity.Visibility
                              && m.CompanionIds.ToHashSet(StringComparer.Ordinal).SetEquals(entity.CompanionIds);

            if (sameContext && RestatementKey(m) == identity)
            {
                found[m.Id] = new CandidateMatch(m, 1.0, true);
                continue;
            }

            if (vector is not { Length: > 0 } || !m.HasComparableEmbedding(stamp, vector.Length)) continue;
            if (!VectorMath.TryCosineSimilarity(vector, m.GetEmbedding(), out var cosine)) continue;
            if (cosine < _settings.CandidateSimilarityThreshold) continue;

            var nearIdentical = sameContext
                                && entity.Predicate is null
                                && m.Predicate is null
                                && cosine >= _settings.DuplicateSimilarityThreshold;

            semantic.Add(new CandidateMatch(m, cosine, nearIdentical));
        }

        foreach (var match in semantic.OrderByDescending(s => s.Similarity).Take(_settings.MaxCandidates))
            found.TryAdd(match.Memory.Id, match);

        // Restatements first, so the caller can short-circuit on the strongest signal.
        return found.Values.OrderByDescending(c => c.IsRestatement).ThenByDescending(c => c.Similarity).ToList();
    }

    /// <summary>Identity for "the same thing said again": title plus summary, normalised.</summary>
    private static string RestatementKey(MemoryNodeEntity memory) =>
        $"{memory.Title.Trim().ToLowerInvariant()}|{memory.Summary.Trim().ToLowerInvariant()}";

    private async Task<StoreResult> ReinforceDuplicateAsync(
        MemoryNodeEntity incoming, MemoryNodeEntity existing, string actor, CancellationToken cancellationToken)
    {
        await _repository.ReinforceAsync(existing.Id, cancellationToken);

        // Keep whichever version says more, but never lose detail already recorded.
        if (!string.IsNullOrWhiteSpace(incoming.Content) && incoming.Content.Length > existing.Content.Length)
        {
            existing.Content = incoming.Content;
            existing.Confidence = Math.Max(existing.Confidence, incoming.Confidence);
            await _repository.SaveAsync(existing, cancellationToken);
        }

        _logger?.LogInformation(
            "Duplicate detected: '{New}' matches existing '{Existing}'", incoming.Title, existing.Title);

        return new StoreResult
        {
            Memory  = existing,
            Action  = StoreAction.ReinforcedExisting,
            Message = $"Similar memory already exists. Reinforced '{existing.Title}' instead of duplicating it.",
        };
    }

    private MemoryConflict BuildConflict(
        MemoryNodeEntity incoming, MemoryNodeEntity existing, ConflictKind kind, string reason, MemoryScope scope) =>
        new()
        {
            UserId           = incoming.UserId,
            NewMemoryId      = incoming.Id,
            ExistingMemoryId = existing.Id,
            SubjectRef       = incoming.SubjectRef,
            Predicate        = incoming.Predicate,
            Kind             = kind,
            CompanionId      = scope.CompanionId,
            Description      = $"'{incoming.Title}' contradicts '{existing.Title}' ({reason})",
        };

    private static void Normalize(MemoryNodeEntity entity, MemoryScope scope)
    {
        if (string.IsNullOrWhiteSpace(entity.UserId) || entity.UserId == MemoryScope.DefaultUserId)
            entity.UserId = scope.UserId;

        entity.SubjectRef = SubjectRefs.Normalize(entity.SubjectRef);
        entity.Predicate  = SlotRegistry.Normalize(entity.Predicate);

        // A memory created inside a companion's context defaults to that companion's scope only if
        // the caller asked for it; defaulting to Global would leak private conversation to everyone.
        if (entity.Visibility == MemoryVisibility.Scoped && entity.CompanionIds.Count == 0 && scope.CompanionId is not null)
            entity.CompanionIds = [scope.CompanionId];

        // Derive a value key so "same slot, same value" is distinguishable from a contradiction.
        if (entity.Predicate is not null && entity.ValueKey is null)
            entity.ValueKey = MemoryTextIndexer.BuildValueKey(entity.Summary);
    }

    private async Task EnsureEmbeddingAsync(MemoryNodeEntity entity, CancellationToken cancellationToken)
    {
        if (_embeddingService?.IsAvailable != true) return;

        var stamp = MemoryTextIndexer.BuildEmbeddingStamp(_embeddingService.ModelId);
        if (entity.HasComparableEmbedding(stamp, _embeddingService.Dimensions)) return;

        try
        {
            var vector = await _embeddingService.GetEmbeddingAsync(
                MemoryTextIndexer.BuildEmbeddingText(entity), cancellationToken);
            entity.SetEmbedding(vector, stamp);
        }
        catch (Exception ex)
        {
            // Durability first: a memory is stored even if it cannot be embedded. It will be picked
            // up by the next reindex rather than blocking or losing the write.
            _logger?.LogWarning(ex, "Failed to embed memory '{Title}'; storing without a vector", entity.Title);
        }
    }

    // ── History ───────────────────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<MemoryNodeEntity>> GetSlotHistoryAsync(
        MemoryScope scope, string subjectRef, string predicate, CancellationToken cancellationToken = default)
        => _repository.GetBySlotAsync(scope, subjectRef, predicate, includeHistory: true, cancellationToken);

    public async Task<IReadOnlyList<MemoryNodeEntity>> GetTagHistoryAsync(
        string tag, bool includeArchived = true, CancellationToken cancellationToken = default)
    {
        var memories = await _repository.QueryAsync(MemoryScope.Default, new MemoryQueryOptions
        {
            Tags              = [tag],
            IncludeNonCurrent = includeArchived,
        }, cancellationToken);

        return memories.OrderByDescending(m => m.ValidFrom).ToList();
    }
}
