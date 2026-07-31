namespace AgenticMemory.Brain.Models;

/// <summary>
/// The retrieval scope for a memory operation: which user's store, and which companion is asking.
///
/// This is a hard privacy boundary, not a ranking hint. Every read path on
/// <see cref="Interfaces.IMemoryRepository"/> requires one, and the predicate is pushed into the
/// storage query rather than applied as a post-filter — post-filtering a truncated candidate list
/// both leaks and drops results.
///
/// Scope matching is always exact (after normalisation). It is never substring or fuzzy: a
/// companion named "Ari" must not match memories belonging to "Aria".
/// </summary>
public sealed record MemoryScope
{
    /// <summary>Owner for stores created before multi-user support existed.</summary>
    public const string DefaultUserId = "default";

    /// <summary>The tenancy boundary. Never crossed.</summary>
    public required string UserId { get; init; }

    /// <summary>The companion asking. Null means "no companion context" — only global memories
    /// are visible unless <see cref="AllCompanions"/> is set.</summary>
    public string? CompanionId { get; init; }

    /// <summary>Whether memories shared by all of this user's companions are visible.</summary>
    public bool IncludeGlobal { get; init; } = true;

    /// <summary>Administrative view across every companion of a single user (dashboard, export).
    /// Still bounded by <see cref="UserId"/>.</summary>
    public bool AllCompanions { get; init; }

    /// <summary>Default single-user scope, used by callers that have not adopted scoping yet.</summary>
    public static MemoryScope Default { get; } = new() { UserId = DefaultUserId, AllCompanions = true };

    public static MemoryScope ForUser(string? userId) =>
        new() { UserId = NormalizeUser(userId), AllCompanions = true };

    public static MemoryScope For(string? userId, string? companionId) => new()
    {
        UserId      = NormalizeUser(userId),
        CompanionId = NormalizeId(companionId),
    };

    /// <summary>Everything belonging to one user, regardless of companion.</summary>
    public static MemoryScope AllFor(string? userId) =>
        new() { UserId = NormalizeUser(userId), AllCompanions = true };

    public static string NormalizeUser(string? userId) =>
        string.IsNullOrWhiteSpace(userId) ? DefaultUserId : userId.Trim().ToLowerInvariant();

    /// <summary>
    /// Canonical form for user and companion identifiers. Applied on both write and query so that
    /// comparison can be a plain ordinal equality check.
    /// </summary>
    public static string? NormalizeId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : id.Trim().ToLowerInvariant();

    /// <summary>
    /// In-memory equivalent of the storage predicate. The repository pushes the same logic into
    /// its query; this exists for defence-in-depth assertions and for tests.
    /// </summary>
    public bool Admits(MemoryNodeEntity memory)
    {
        if (!string.Equals(memory.UserId, UserId, StringComparison.Ordinal))
            return false;

        if (AllCompanions)
            return true;

        if (memory.Visibility == MemoryVisibility.Global)
            return IncludeGlobal;

        if (CompanionId is null)
            return false;

        // Exact match only — never Contains/StartsWith.
        return memory.CompanionIds.Any(c => string.Equals(c, CompanionId, StringComparison.Ordinal));
    }

    public override string ToString() =>
        AllCompanions ? $"user={UserId}/*" : $"user={UserId}/companion={CompanionId ?? "-"}";
}

/// <summary>
/// Canonical <c>SubjectRef</c> values — who a memory is <em>about</em>, which is independent of who
/// can see it. Without this axis, "the user's favourite colour" and "Aria's favourite colour" are
/// near-identical vectors and one will archive the other.
/// </summary>
public static class SubjectRefs
{
    public const string User = "user";

    /// <summary>The companion themself — persona facts.</summary>
    public static string Companion(string id) => $"companion:{MemoryScope.NormalizeId(id)}";

    /// <summary>The user-and-companion relationship: nicknames, shared jokes, milestones.</summary>
    public static string Relationship(string companionId) => $"relationship:{MemoryScope.NormalizeId(companionId)}";

    /// <summary>A third party in the user's life.</summary>
    public static string Person(string name) => $"person:{MemoryScope.NormalizeId(name)}";

    public static string Normalize(string? subjectRef) =>
        string.IsNullOrWhiteSpace(subjectRef) ? User : subjectRef.Trim().ToLowerInvariant();
}
