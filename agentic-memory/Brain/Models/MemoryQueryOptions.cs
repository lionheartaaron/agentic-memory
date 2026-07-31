namespace AgenticMemory.Brain.Models;

/// <summary>
/// Filters applied <em>inside</em> a scoped query.
///
/// Everything here is applied before any truncation. Applying a filter after taking a top-N
/// candidate list is what previously let a tag-filtered search return nothing while hundreds of
/// matching memories existed.
/// </summary>
public sealed record MemoryQueryOptions
{
    public static MemoryQueryOptions Default { get; } = new();

    /// <summary>Include superseded, archived and merged memories. Forgotten memories are only
    /// ever returned when <see cref="IncludeForgotten"/> is also set.</summary>
    public bool IncludeNonCurrent { get; init; }

    /// <summary>Include tombstoned memories the user asked to forget. Administrative use only.</summary>
    public bool IncludeForgotten { get; init; }

    /// <summary>Include memories whose <see cref="MemoryNodeEntity.ExpiresAt"/> has passed.</summary>
    public bool IncludeExpired { get; init; }

    public MemoryType? Type { get; init; }
    public string? SubjectRef { get; init; }
    public string? Predicate { get; init; }

    /// <summary>Memories must carry at least one of these tags. Exact, case-insensitive match —
    /// never substring.</summary>
    public IReadOnlyCollection<string>? Tags { get; init; }

    /// <summary>Drop anything more sensitive than this.</summary>
    public Sensitivity? MaxSensitivity { get; init; }

    /// <summary>
    /// Answer as of a past instant on the valid-time axis: return what was true then, not what is
    /// true now.
    ///
    /// A memory qualifies when its validity window contains the instant — <c>ValidFrom &lt;= AsOf</c>
    /// and it had not yet been superseded. This is what makes the bitemporal fields load-bearing
    /// rather than decorative: without it, "where was I working last year" can only be answered from
    /// the current value, which is precisely the fact that replaced the right one. Superseded
    /// memories are included automatically; a memory the user asked to forget still is not.
    /// </summary>
    public DateTime? AsOf { get; init; }

    /// <summary>Applied last, after every filter above.</summary>
    public int? Limit { get; init; }
}
