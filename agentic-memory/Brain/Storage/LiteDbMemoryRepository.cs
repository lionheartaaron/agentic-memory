using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Slots;
using AgenticMemory.Persistence;
using LiteDB;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Brain.Storage;

/// <summary>
/// LiteDB implementation of the scoped memory store.
///
/// Three invariants this type is responsible for:
///   1. No read crosses a <see cref="MemoryScope"/>. The user predicate is pushed into the query;
///      visibility is evaluated over that user-bounded set and always before any limit.
///   2. Nothing is ever physically deleted here. Removal lives on <see cref="IMemoryAdminStore"/>.
///   3. Multi-document mutations are atomic, so a supersede cannot lose the replacement fact.
/// </summary>
public sealed class LiteDbMemoryRepository : IMemoryRepository, IMemoryAdminStore
{
    public const string CollectionName = "memories";
    public const string ConflictCollectionName = "memory_conflicts";
    public const string AwarenessCollectionName = "memory_awareness";

    private const double WeakMemoryThreshold = 0.3;

    private readonly SharedLiteDatabase _sharedDb;
    private readonly ILiteCollection<MemoryNodeEntity> _collection;
    private readonly ILiteCollection<MemoryConflict> _conflicts;
    private readonly ILiteCollection<MemoryAwareness> _awareness;
    private readonly IMemoryEventLog _eventLog;
    private readonly ILogger<LiteDbMemoryRepository>? _logger;

    /// <summary>Bumped on every mutation so derived caches (e.g. the vector cache) can invalidate.</summary>
    private long _writeStamp;

    public long WriteStamp => Interlocked.Read(ref _writeStamp);

    public LiteDbMemoryRepository(
        SharedLiteDatabase sharedDb,
        IMemoryEventLog eventLog,
        ILogger<LiteDbMemoryRepository>? logger = null)
    {
        _sharedDb   = sharedDb;
        _eventLog   = eventLog;
        _logger     = logger;

        // Schema migration is not done here any more: it runs when the file is opened, in
        // SharedLiteDatabase, so that every collection is current before anything can read one —
        // not just the collections whose repository remembered to ask.
        _collection = sharedDb.Database.GetCollection<MemoryNodeEntity>(CollectionName);
        _conflicts  = sharedDb.Database.GetCollection<MemoryConflict>(ConflictCollectionName);
        _awareness  = sharedDb.Database.GetCollection<MemoryAwareness>(AwarenessCollectionName);
        EnsureIndexes();
    }

    private void EnsureIndexes()
    {
        // Scope: the only predicate that must be index-backed, because it bounds every read.
        _collection.EnsureIndex(x => x.UserId);
        _collection.EnsureIndex(x => x.State);
        _collection.EnsureIndex(x => x.CompanionIds);

        // Slot lookups, used by both retrieval and the supersede gate.
        _collection.EnsureIndex(x => x.SubjectRef);
        _collection.EnsureIndex(x => x.Predicate);

        _collection.EnsureIndex(x => x.ContentNormalized);
        _collection.EnsureIndex(x => x.Tags);
        _collection.EnsureIndex(x => x.CreatedAt);
        _collection.EnsureIndex(x => x.ExpiresAt);
        _collection.EnsureIndex(x => x.LastAccessedAt);

        // Trigrams are deliberately NOT indexed: one entry per trigram per document produced tens
        // of millions of index entries at modest corpus sizes, and the lexical channel now scores
        // them in memory over an already scope-filtered set.

        _conflicts.EnsureIndex(x => x.UserId);
        _conflicts.EnsureIndex(x => x.Status);
        _conflicts.EnsureIndex(x => x.NewMemoryId);
        _conflicts.EnsureIndex(x => x.ExistingMemoryId);

        _awareness.EnsureIndex(x => x.MemoryId);
        _awareness.EnsureIndex(x => x.CompanionId);
    }

    // ── Scoped reads ──────────────────────────────────────────────────────────────────────────

    // ── User-bounded row cache ────────────────────────────────────────────────────────────────
    //
    // Retrieval scores the whole scope-filtered set, so every query materialises every one of that
    // user's documents — and each carries a 1.5 KB embedding blob, which makes BSON deserialization,
    // not the vector maths, the dominant cost of a search. Measured over ten thousand memories it was
    // roughly 460 ms per query, against about 20 ms for all the scoring put together.
    //
    // Invalidation is deliberately blunt: one process-wide write stamp, bumped by every committed
    // batch, and any change to it drops every user's rows. A clever per-document scheme would be
    // faster to invalidate and far easier to get subtly wrong, and the failure mode of a stale
    // memory cache is exactly the one this system exists to prevent.

    private sealed record UserRows(long Stamp, List<MemoryNodeEntity> Rows, Dictionary<Guid, MemoryNodeEntity> ById);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, UserRows> _userRows = new(StringComparer.Ordinal);

    /// <summary>How many users' row sets to hold. Small: this is a local-first, few-tenant store.</summary>
    private const int MaxCachedUsers = 8;

    private List<MemoryNodeEntity> LoadUserRows(string userId)
    {
        var stamp = Interlocked.Read(ref _writeStamp);

        if (_userRows.TryGetValue(userId, out var cached) && cached.Stamp == stamp)
            return cached.Rows;

        var rows = _collection.Find(x => x.UserId == userId).ToList();

        if (_userRows.Count >= MaxCachedUsers) _userRows.Clear();

        // Re-read the stamp: a write that landed while we were loading must not be masked by an
        // entry claiming to be current as of before it.
        _userRows[userId] = new UserRows(
            Interlocked.Read(ref _writeStamp) == stamp ? stamp : -1,
            rows,
            rows.ToDictionary(r => r.Id));

        return rows;
    }

    /// <summary>The cached instance for an id, if this row set is loaded and current.</summary>
    private MemoryNodeEntity? FindCachedRow(Guid id)
    {
        var stamp = Interlocked.Read(ref _writeStamp);

        foreach (var entry in _userRows.Values)
            if (entry.Stamp == stamp && entry.ById.TryGetValue(id, out var row))
                return row;

        return null;
    }

    /// <summary>
    /// The scope predicate, pushed as far into the storage engine as LiteDB allows.
    /// Returns the user-bounded set; visibility and expiry are applied by the caller via
    /// <see cref="ApplyScopeFilter"/> before any truncation.
    /// </summary>
    private IEnumerable<MemoryNodeEntity> QueryUserBounded(MemoryScope scope, bool activeOnly)
    {
        var rows = LoadUserRows(scope.UserId);

        return activeOnly
            ? rows.Where(x => x.State == MemoryState.Active)
            : rows;
    }

    private static IEnumerable<MemoryNodeEntity> ApplyScopeFilter(
        IEnumerable<MemoryNodeEntity> source, MemoryScope scope, MemoryQueryOptions options)
    {
        // With AsOf set, every temporal predicate is evaluated against that instant instead of now —
        // including expiry, so an ephemeral memory that has since lapsed is still visible in the past
        // where it was live.
        var now = options.AsOf ?? DateTime.UtcNow;

        foreach (var m in source)
        {
            if (!scope.Admits(m)) continue;

            if (!options.IncludeForgotten && m.State == MemoryState.Forgotten) continue;

            if (options.AsOf is { } asOf)
            {
                // Was this memory's assertion live at that instant?
                if (m.ValidFrom > asOf) continue;
                if (m.ValidUntil is { } until && until <= asOf) continue;
            }
            else if (!options.IncludeNonCurrent && m.State != MemoryState.Active) continue;

            if (!options.IncludeExpired && m.ExpiresAt.HasValue && now > m.ExpiresAt.Value) continue;

            if (options.Type.HasValue && m.Type != options.Type.Value) continue;

            if (options.SubjectRef is not null &&
                !string.Equals(m.SubjectRef, options.SubjectRef, StringComparison.OrdinalIgnoreCase)) continue;

            if (options.Predicate is not null &&
                !string.Equals(m.Predicate, options.Predicate, StringComparison.OrdinalIgnoreCase)) continue;

            if (options.MaxSensitivity.HasValue && m.Sensitivity > options.MaxSensitivity.Value) continue;

            if (options.Tags is { Count: > 0 } &&
                !m.Tags.Any(t => options.Tags.Any(f => string.Equals(t, f, StringComparison.OrdinalIgnoreCase))))
                continue;

            yield return m;
        }
    }

    public Task<MemoryNodeEntity?> GetAsync(Guid id, MemoryScope scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entity = _collection.FindById(id);
        if (entity is null || !scope.Admits(entity))
            return Task.FromResult<MemoryNodeEntity?>(null);

        // Superseded and archived memories remain fetchable by id — history views and restore need
        // them. A memory the user asked to forget does not come back through this door.
        if (entity.State == MemoryState.Forgotten)
            return Task.FromResult<MemoryNodeEntity?>(null);

        return Task.FromResult<MemoryNodeEntity?>(entity);
    }

    public Task<IReadOnlyList<MemoryNodeEntity>> QueryAsync(
        MemoryScope scope, MemoryQueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= MemoryQueryOptions.Default;

        // A point-in-time query must read superseded rows, so the storage-level active filter is off.
        var activeOnly = !options.IncludeNonCurrent && !options.IncludeForgotten && options.AsOf is null;
        var filtered   = ApplyScopeFilter(QueryUserBounded(scope, activeOnly), scope, options);

        // Limit is applied last, after every filter — never before.
        if (options.Limit is { } limit and > 0)
            filtered = filtered.Take(limit);

        return Task.FromResult<IReadOnlyList<MemoryNodeEntity>>(filtered.ToList());
    }

    public Task<IReadOnlyList<MemoryNodeEntity>> GetActiveAsync(MemoryScope scope, CancellationToken cancellationToken = default)
        => QueryAsync(scope, MemoryQueryOptions.Default, cancellationToken);

    public Task<IReadOnlyList<MemoryNodeEntity>> GetBySlotAsync(
        MemoryScope scope,
        string subjectRef,
        string predicate,
        bool includeHistory = false,
        CancellationToken cancellationToken = default)
        => GetBySlotAsync(scope, subjectRef, predicate, includeHistory, null, cancellationToken);

    public Task<IReadOnlyList<MemoryNodeEntity>> GetBySlotAsync(
        MemoryScope scope,
        string subjectRef,
        string predicate,
        bool includeHistory,
        DateTime? asOf,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var options = new MemoryQueryOptions
        {
            SubjectRef        = SubjectRefs.Normalize(subjectRef),
            Predicate         = SlotRegistry.Normalize(predicate),
            IncludeNonCurrent = includeHistory,
            AsOf              = asOf,
        };

        var activeOnly = !includeHistory && asOf is null;

        var results = ApplyScopeFilter(QueryUserBounded(scope, activeOnly), scope, options)
            .OrderByDescending(m => m.ValidFrom)
            .ToList();

        return Task.FromResult<IReadOnlyList<MemoryNodeEntity>>(results);
    }

    public Task<RepositoryStats> GetStatsAsync(MemoryScope scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var all = ApplyScopeFilter(
            QueryUserBounded(scope, activeOnly: false),
            scope,
            new MemoryQueryOptions { IncludeNonCurrent = true, IncludeForgotten = true, IncludeExpired = true })
            .ToList();

        return Task.FromResult(BuildStats(all, GetDatabaseSizeBytes(), CountOpenConflicts(scope.UserId)));
    }

    private int CountOpenConflicts(string userId) =>
        _conflicts.Count(x => x.UserId == userId && x.Status == ConflictStatus.Open);

    private static RepositoryStats BuildStats(List<MemoryNodeEntity> all, long dbSize, int openConflicts)
    {
        if (all.Count == 0)
            return new RepositoryStats { DatabaseSizeBytes = dbSize, OpenConflicts = openConflicts };

        var active    = all.Where(m => m.State == MemoryState.Active).ToList();
        var strengths = active.Select(m => m.GetCurrentStrength()).ToList();

        return new RepositoryStats
        {
            TotalNodes        = all.Count,
            ActiveNodes       = active.Count,
            SupersededNodes   = all.Count(m => m.State == MemoryState.Superseded),
            ArchivedNodes     = all.Count(m => m.State is MemoryState.Archived or MemoryState.Merged),
            ForgottenNodes    = all.Count(m => m.State == MemoryState.Forgotten),
            AverageStrength   = strengths.Count > 0 ? strengths.Average() : 0,
            WeakMemoriesCount = strengths.Count(s => s < WeakMemoryThreshold),
            OldestMemory      = all.Min(m => m.CreatedAt),
            NewestMemory      = all.Max(m => m.CreatedAt),
            DatabaseSizeBytes = dbSize,
            OpenConflicts     = openConflicts,
        };
    }

    // ── Atomic writes ─────────────────────────────────────────────────────────────────────────

    public Task<MemoryWriteResult> ExecuteAsync(
        MemoryWriteBatch batch, string actor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (batch.IsEmpty)
            return Task.FromResult(new MemoryWriteResult());

        var inserted = 0;
        var updated = 0;
        var stateChanged = 0;
        var events = new List<MemoryEvent>();

        // BeginTrans returns false when this thread is already inside a transaction; in that case
        // the outer scope owns commit/rollback.
        var ownsTransaction = _sharedDb.Database.BeginTrans();
        try
        {
            foreach (var upsert in batch.Upserts)
            {
                var node = upsert.Entity;
                Prepare(node);

                var existing = _collection.FindById(node.Id);

                if (existing is null)
                {
                    if (upsert.ExpectedVersion is > 0)
                        throw new MemoryConcurrencyException(node.Id, upsert.ExpectedVersion.Value, 0);

                    node.Version = 1;
                    _collection.Insert(node);
                    inserted++;
                    events.Add(NewEvent(node, MemoryEventType.Created, actor));
                }
                else
                {
                    if (upsert.ExpectedVersion is { } expected && existing.Version != expected)
                        throw new MemoryConcurrencyException(node.Id, expected, existing.Version);

                    // Retrieval statistics belong to the reinforcement path, which deliberately does
                    // not bump Version. Writing a caller's copy verbatim would roll them back every
                    // time an edit raced a search — and every search reinforces.
                    node.AccessCount    = Math.Max(node.AccessCount, existing.AccessCount);
                    node.LastAccessedAt = existing.LastAccessedAt > node.LastAccessedAt
                        ? existing.LastAccessedAt : node.LastAccessedAt;
                    node.BaseStrength   = Math.Max(node.BaseStrength, existing.BaseStrength);

                    node.Version = existing.Version + 1;
                    _collection.Update(node);
                    updated++;
                    events.Add(NewEvent(node, MemoryEventType.Updated, actor));
                }
            }

            foreach (var change in batch.StateChanges)
            {
                var entity = _collection.FindById(change.Id);
                if (entity is null) continue;
                if (entity.State == change.NewState && change.SupersededBy is null) continue;

                entity.State        = change.NewState;
                entity.Version     += 1;
                entity.SupersededBy = change.SupersededBy ?? entity.SupersededBy;

                if (change.NewState is MemoryState.Superseded or MemoryState.Archived or MemoryState.Merged)
                    entity.ValidUntil = change.ValidUntil ?? DateTime.UtcNow;
                else if (change.NewState == MemoryState.Active)
                    entity.ValidUntil = null;

                if (change.NewState == MemoryState.Merged)
                    entity.MergedInto = change.SupersededBy;

                _collection.Update(entity);
                stateChanged++;

                events.Add(NewEvent(entity, MapStateToEvent(change.NewState), actor,
                    change.SupersededBy, change.Detail));
            }

            foreach (var conflict in batch.Conflicts)
            {
                conflict.Description = MemoryTextIndexer.SanitizeForLiteDb(conflict.Description);
                _conflicts.Insert(conflict);

                events.Add(new MemoryEvent
                {
                    UserId          = conflict.UserId,
                    MemoryId        = conflict.NewMemoryId,
                    RelatedMemoryId = conflict.ExistingMemoryId,
                    Type            = MemoryEventType.ConflictRecorded,
                    Actor           = actor,
                    Detail          = $"{conflict.Kind}: {conflict.Description}",
                });
            }

            _eventLog.AppendMany(events);

            if (ownsTransaction) _sharedDb.Database.Commit();
        }
        catch
        {
            if (ownsTransaction) _sharedDb.Database.Rollback();
            throw;
        }

        Interlocked.Increment(ref _writeStamp);

        return Task.FromResult(new MemoryWriteResult
        {
            Inserted          = inserted,
            Updated           = updated,
            StateChanged      = stateChanged,
            ConflictsRecorded = batch.Conflicts.Count,
        });
    }

    private static MemoryEventType MapStateToEvent(MemoryState state) => state switch
    {
        MemoryState.Superseded => MemoryEventType.Superseded,
        MemoryState.Archived   => MemoryEventType.Archived,
        MemoryState.Forgotten  => MemoryEventType.Forgotten,
        MemoryState.Merged     => MemoryEventType.Merged,
        MemoryState.Active     => MemoryEventType.Restored,
        _                      => MemoryEventType.Updated,
    };

    private static MemoryEvent NewEvent(
        MemoryNodeEntity node, MemoryEventType type, string actor, Guid? related = null, string? detail = null) =>
        new()
        {
            UserId          = node.UserId,
            MemoryId        = node.Id,
            Type            = type,
            Actor           = actor,
            MemoryTitle     = node.Title,
            RelatedMemoryId = related,
            Detail          = detail,
        };

    /// <summary>Normalises identifiers and recomputes derived search fields before persisting.</summary>
    private static void Prepare(MemoryNodeEntity node)
    {
        node.UserId     = MemoryScope.NormalizeUser(node.UserId);
        node.SubjectRef = SubjectRefs.Normalize(node.SubjectRef);
        node.Predicate  = SlotRegistry.Normalize(node.Predicate);

        node.CompanionIds = node.CompanionIds
            .Select(MemoryScope.NormalizeId)
            .Where(c => c is not null)
            .Select(c => c!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // A scoped memory with no companions would be invisible to everyone; treat it as global
        // rather than silently orphaning it.
        if (node.Visibility == MemoryVisibility.Scoped && node.CompanionIds.Count == 0)
            node.Visibility = MemoryVisibility.Global;

        if (node.Visibility == MemoryVisibility.Global)
            node.CompanionIds.Clear();

        MemoryTextIndexer.ApplyTextIndex(node);
    }

    public Task SaveAsync(MemoryNodeEntity node, CancellationToken cancellationToken = default)
        => ExecuteAsync(new MemoryWriteBatch().Upsert(node), "repository:save", cancellationToken);

    public Task SaveAsync(MemoryNodeEntity node, long expectedVersion, string actor, CancellationToken cancellationToken = default)
        => ExecuteAsync(new MemoryWriteBatch().Upsert(node, expectedVersion), actor, cancellationToken);

    // ── Reinforcement (ranking signal only: no version guard, no events) ──────────────────────

    public Task ReinforceAsync(Guid id, CancellationToken cancellationToken = default)
        => ReinforceManyAsync([id], cancellationToken);

    public Task ReinforceManyAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var updates = new List<MemoryNodeEntity>();
        foreach (var id in ids)
        {
            // Reinforce the cached instance where there is one, so exactly one object represents the
            // row. Reinforcement deliberately does not bump the write stamp — a search reinforces
            // every result it returns, and invalidating the row cache on that would defeat it — so
            // updating a separate copy here would quietly roll the counts back on the next read.
            var entity = FindCachedRow(id) ?? _collection.FindById(id);
            if (entity is null || entity.State != MemoryState.Active) continue;
            entity.Reinforce();
            updates.Add(entity);
        }

        if (updates.Count == 0) return Task.CompletedTask;

        // One round trip instead of one per hit: a search reinforces every result it returns.
        _collection.Update(updates);
        return Task.CompletedTask;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────────────────────

    public async Task<bool> ForgetAsync(Guid id, MemoryScope scope, string actor, CancellationToken cancellationToken = default)
    {
        var entity = await GetAsync(id, scope, cancellationToken);
        if (entity is null) return false;

        await ExecuteAsync(
            new MemoryWriteBatch().ChangeState(id, MemoryState.Forgotten, detail: "user requested"),
            actor, cancellationToken);
        return true;
    }

    public async Task<bool> RestoreAsync(Guid id, MemoryScope scope, string actor, CancellationToken cancellationToken = default)
    {
        var entity = _collection.FindById(id);
        if (entity is null || !scope.Admits(entity)) return false;
        if (entity.State == MemoryState.Active) return true;

        await ExecuteAsync(
            new MemoryWriteBatch().ChangeState(id, MemoryState.Active, detail: "restored"),
            actor, cancellationToken);
        return true;
    }

    // ── Per-companion awareness ───────────────────────────────────────────────────────────────

    public Task RecordSurfacedAsync(
        MemoryScope scope, IEnumerable<Guid> memoryIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Awareness is a property of a specific companion. An administrative or all-companion scope
        // has no one to attribute the turn to, so there is nothing to record.
        if (scope.CompanionId is not { } companionId) return Task.CompletedTask;

        var now = DateTime.UtcNow;
        var writes = new List<MemoryAwareness>();

        foreach (var id in memoryIds.Distinct())
        {
            var key = MemoryAwareness.BuildId(id, companionId);
            var entry = _awareness.FindById(key);

            if (entry is null)
            {
                entry = new MemoryAwareness
                {
                    Id              = key,
                    MemoryId        = id,
                    UserId          = scope.UserId,
                    CompanionId     = companionId,
                    FirstSurfacedAt = now,
                    LastSurfacedAt  = now,
                    SurfaceCount    = 1,
                };
            }
            else
            {
                entry.LastSurfacedAt = now;
                entry.SurfaceCount++;
            }

            writes.Add(entry);
        }

        foreach (var entry in writes)
            _awareness.Upsert(entry);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<Guid, MemoryAwareness>> GetAwarenessAsync(
        MemoryScope scope, IEnumerable<Guid> memoryIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var empty = (IReadOnlyDictionary<Guid, MemoryAwareness>)new Dictionary<Guid, MemoryAwareness>();
        if (scope.CompanionId is not { } companionId) return Task.FromResult(empty);

        var result = new Dictionary<Guid, MemoryAwareness>();

        foreach (var id in memoryIds.Distinct())
        {
            var entry = _awareness.FindById(MemoryAwareness.BuildId(id, companionId));
            if (entry is not null) result[id] = entry;
        }

        return Task.FromResult((IReadOnlyDictionary<Guid, MemoryAwareness>)result);
    }

    // ── Conflicts ─────────────────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<MemoryConflict>> GetConflictsAsync(
        MemoryScope scope, bool openOnly = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var userId = scope.UserId;
        var query = openOnly
            ? _conflicts.Find(x => x.UserId == userId && x.Status == ConflictStatus.Open)
            : _conflicts.Find(x => x.UserId == userId);

        var results = query.OrderByDescending(x => x.DetectedAt).ToList();

        // A conflict is only visible to a companion that can see both sides of it.
        if (!scope.AllCompanions)
        {
            results = results.Where(c =>
                    Admits(c.NewMemoryId) && Admits(c.ExistingMemoryId))
                .ToList();

            bool Admits(Guid id)
            {
                var m = _collection.FindById(id);
                return m is not null && scope.Admits(m);
            }
        }

        return Task.FromResult<IReadOnlyList<MemoryConflict>>(results);
    }

    public async Task<IReadOnlyList<ConflictDetail>> GetConflictDetailsAsync(
        MemoryScope scope, bool openOnly = true, CancellationToken cancellationToken = default)
    {
        var conflicts = await GetConflictsAsync(scope, openOnly, cancellationToken);
        return conflicts.Select(c => Describe(c, scope)).ToList();
    }

    public Task<ConflictResolution> ResolveConflictAsync(
        Guid conflictId, MemoryScope scope, Guid? winnerId, bool dismissed, string actor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var conflict = _conflicts.FindById(conflictId);
        if (conflict is null || !string.Equals(conflict.UserId, scope.UserId, StringComparison.Ordinal))
            return Task.FromResult(ConflictResolution.NotFound);

        if (conflict.Status != ConflictStatus.Open)
            return Task.FromResult(ConflictResolution.AlreadySettled);

        // Every check below runs before anything is written. A contradiction is settled by
        // superseding a memory, which is the kind of thing that should not happen halfway.
        if (!dismissed)
        {
            if (winnerId is not { } candidate)
                return Task.FromResult(ConflictResolution.NoChoice);

            if (candidate != conflict.NewMemoryId && candidate != conflict.ExistingMemoryId)
                return Task.FromResult(ConflictResolution.WinnerNotInConflict);
        }

        conflict.Status     = dismissed ? ConflictStatus.Dismissed : ConflictStatus.Resolved;
        conflict.ResolvedAt = DateTime.UtcNow;
        conflict.WinnerId   = dismissed ? null : winnerId;
        _conflicts.Update(conflict);

        // Resolving in favour of one side supersedes the other, atomically.
        if (!dismissed && winnerId is { } winner)
        {
            var loser = winner == conflict.NewMemoryId ? conflict.ExistingMemoryId : conflict.NewMemoryId;
            return ExecuteAsync(
                    new MemoryWriteBatch().ChangeState(loser, MemoryState.Superseded, winner, detail: "conflict resolved"),
                    actor, cancellationToken)
                .ContinueWith(_ => ConflictResolution.Resolved, cancellationToken);
        }

        Interlocked.Increment(ref _writeStamp);
        return Task.FromResult(ConflictResolution.Dismissed);
    }

    public Task<ConflictDetail?> GetConflictAsync(
        Guid conflictId, MemoryScope scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var conflict = _conflicts.FindById(conflictId);
        if (conflict is null || !string.Equals(conflict.UserId, scope.UserId, StringComparison.Ordinal))
            return Task.FromResult<ConflictDetail?>(null);

        return Task.FromResult<ConflictDetail?>(Describe(conflict, scope));
    }

    /// <summary>
    /// Pairs a conflict with the two memories it is about.
    ///
    /// Read straight from the collection rather than through <see cref="GetAsync"/>, which hides a
    /// forgotten memory on purpose. Here that would blank out one side of a decision the caller is
    /// being asked to make, and "choose between this and something I will not show you" is not a
    /// question anyone can answer. Scope is still enforced: a side the scope does not admit is
    /// returned as null, exactly as <see cref="GetConflictsAsync"/> already requires of both.
    /// </summary>
    private ConflictDetail Describe(MemoryConflict conflict, MemoryScope scope)
    {
        return new ConflictDetail(conflict, Side(conflict.ExistingMemoryId), Side(conflict.NewMemoryId));

        ConflictSide? Side(Guid id)
        {
            var m = _collection.FindById(id);
            if (m is null || !scope.Admits(m)) return null;

            return new ConflictSide(
                m.Id, m.Title, m.Summary, m.ValueKey, m.State, m.Source,
                m.Confidence, m.CreatedAt, m.ValidFrom, m.IsPinned);
        }
    }

    // ── IMemoryAdminStore ─────────────────────────────────────────────────────────────────────

    public IEnumerable<MemoryNodeEntity> StreamAll(bool includeNonCurrent = true) =>
        includeNonCurrent
            ? _collection.FindAll()
            : _collection.Find(x => x.State == MemoryState.Active);

    public Task<IReadOnlyList<MemoryNodeEntity>> GetAllAsync(
        bool includeNonCurrent = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<MemoryNodeEntity>>(StreamAll(includeNonCurrent).ToList());
    }

    public Task<MemoryNodeEntity?> GetByIdUnscopedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<MemoryNodeEntity?>(_collection.FindById(id));
    }

    public Task<IReadOnlyList<string>> GetUserIdsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = _collection.FindAll()
            .Select(m => m.UserId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(ids);
    }

    public Task<RepositoryStats> GetGlobalStatsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Counts come from index-backed count queries rather than deserialising every document.
        var total      = _collection.Count();
        var dbSize     = GetDatabaseSizeBytes();
        var openConf   = _conflicts.Count(x => x.Status == ConflictStatus.Open);

        if (total == 0)
            return Task.FromResult(new RepositoryStats { DatabaseSizeBytes = dbSize, OpenConflicts = openConf });

        var active = _collection.Find(x => x.State == MemoryState.Active).ToList();
        var strengths = active.Select(m => m.GetCurrentStrength()).ToList();

        return Task.FromResult(new RepositoryStats
        {
            TotalNodes        = total,
            ActiveNodes       = active.Count,
            SupersededNodes   = _collection.Count(x => x.State == MemoryState.Superseded),
            ArchivedNodes     = _collection.Count(x => x.State == MemoryState.Archived),
            ForgottenNodes    = _collection.Count(x => x.State == MemoryState.Forgotten),
            AverageStrength   = strengths.Count > 0 ? strengths.Average() : 0,
            WeakMemoriesCount = strengths.Count(s => s < WeakMemoryThreshold),
            OldestMemory      = active.Count > 0 ? active.Min(m => m.CreatedAt) : null,
            NewestMemory      = active.Count > 0 ? active.Max(m => m.CreatedAt) : null,
            DatabaseSizeBytes = dbSize,
            OpenConflicts     = openConf,
        });
    }

    public Task<int> PurgeForgottenAsync(TimeSpan retention, string actor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cutoff = DateTime.UtcNow - retention;
        var doomed = _collection
            .Find(x => x.State == MemoryState.Forgotten)
            .Where(x => (x.ValidUntil ?? x.LastAccessedAt) <= cutoff)
            .ToList();

        if (doomed.Count == 0) return Task.FromResult(0);

        var ownsTransaction = _sharedDb.Database.BeginTrans();
        try
        {
            foreach (var m in doomed)
            {
                // The event outlives the row, so the audit trail survives the purge.
                _eventLog.Append(NewEvent(m, MemoryEventType.Purged, actor,
                    detail: $"purged after {retention.TotalDays:F0}d tombstone"));
                _collection.Delete(m.Id);

                // Awareness rows reference a memory that no longer exists; leaving them would leak
                // the fact that something was forgotten and slowly accumulate dead keys.
                var memoryId = m.Id;
                _awareness.DeleteMany(a => a.MemoryId == memoryId);
            }

            if (ownsTransaction) _sharedDb.Database.Commit();
        }
        catch
        {
            if (ownsTransaction) _sharedDb.Database.Rollback();
            throw;
        }

        Interlocked.Increment(ref _writeStamp);
        _logger?.LogInformation("Purged {Count} forgotten memories older than {Days:F0} days", doomed.Count, retention.TotalDays);
        return Task.FromResult(doomed.Count);
    }

    public Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = _collection.DeleteAll();
        _conflicts.DeleteAll();
        _awareness.DeleteAll();
        Interlocked.Increment(ref _writeStamp);
        return Task.FromResult(count);
    }

    public Task CompactAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _sharedDb.Database.Rebuild();
        return Task.CompletedTask;
    }

    public long GetDatabaseSizeBytes()
    {
        try
        {
            var path = _sharedDb.DatabasePath;
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch { return 0; }
    }

    public void Dispose() { /* lifetime owned by SharedLiteDatabase */ }
}
