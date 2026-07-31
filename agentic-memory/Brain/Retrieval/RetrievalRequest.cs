using AgenticMemory.Brain.Models;

namespace AgenticMemory.Brain.Retrieval;

/// <summary>
/// A retrieval request. Scope is required — there is no unscoped form.
/// </summary>
public sealed record RetrievalRequest
{
    public required string Query { get; init; }
    public required MemoryScope Scope { get; init; }

    public int TopN { get; init; } = 5;

    /// <summary>Exact, case-insensitive tag filter. Applied inside the scoped query, before any
    /// truncation, so it can never produce a false empty result.</summary>
    public IReadOnlyCollection<string>? Tags { get; init; }

    public MemoryType? Type { get; init; }
    public string? SubjectRef { get; init; }

    /// <summary>Restrict to a structured slot. Also used as an exact-match retrieval channel.</summary>
    public string? Predicate { get; init; }

    public Sensitivity MaxSensitivity { get; init; } = Sensitivity.Restricted;

    /// <summary>Prepend always-on identity and persona memories, which a companion turn needs
    /// regardless of what was asked.</summary>
    public bool IncludeCoreContext { get; init; }

    /// <summary>MMR trade-off: 1.0 is pure relevance, lower values push for diversity. Guards
    /// against returning five paraphrases of the same fact.</summary>
    public double DiversityLambda { get; init; } = 0.75;

    /// <summary>Approximate character budget for the returned set. A companion turn needs a set
    /// that fits the context window, not a fixed top-N.</summary>
    public int? CharacterBudget { get; init; }

    /// <summary>Whether to record the retrieval. Off for background/analysis reads so they do not
    /// distort ranking signals.</summary>
    public bool Reinforce { get; init; } = true;

    /// <summary>
    /// Answer as of a past instant: what was true then, including facts since superseded.
    /// "Where was I working last year" is unanswerable from current state alone, because the fact
    /// that replaced the right answer is the one still active.
    /// </summary>
    public DateTime? AsOf { get; init; }

    /// <summary>
    /// Record that this scope's companion drew on the returned memories. Defaults to on and is a
    /// no-op without a companion in scope. Turn off for analysis reads for the same reason as
    /// <see cref="Reinforce"/>.
    /// </summary>
    public bool TrackAwareness { get; init; } = true;

    /// <summary>
    /// How strongly to prefer memories this companion has not already brought up, in [0, 1].
    ///
    /// A penalty on the fused score, never a filter: a fact that answers the question must still be
    /// returned however often it has been mentioned. What this changes is which of several equally
    /// relevant memories a companion reaches for, so a second conversation about the same subject
    /// surfaces something new rather than repeating the opener. Zero by default — repetition is a
    /// conversational judgement the caller owns.
    /// </summary>
    public double NoveltyBias { get; init; }
}

/// <summary>How confident the system is that it actually knows the answer.</summary>
public enum RetrievalConfidence
{
    /// <summary>Nothing relevant. The companion should say it does not remember rather than guess.</summary>
    None = 0,

    /// <summary>Weak evidence from a single channel. Worth hedging: "I think you mentioned…".</summary>
    Low = 1,

    Medium = 2,

    /// <summary>Strong agreement across channels, or an exact structured slot match.</summary>
    High = 3,
}

public sealed record MemoryRetrievalResult
{
    public required IReadOnlyList<ScoredMemory> Results { get; init; }

    /// <summary>Always-on memories included independently of the query.</summary>
    public IReadOnlyList<ScoredMemory> CoreContext { get; init; } = [];

    /// <summary>Open contradictions touching the returned memories, so a companion can ask about
    /// them instead of silently picking a side.</summary>
    public IReadOnlyList<MemoryConflict> Conflicts { get; init; } = [];

    public RetrievalConfidence Confidence { get; init; }

    /// <summary>Size of the scope-filtered candidate set the channels ran over.</summary>
    public int CandidatesConsidered { get; init; }

    public bool SemanticSearchUsed { get; init; }

    /// <summary>Memories whose stored vector could not be compared against the current model.
    /// Non-zero means a re-index is needed; it is surfaced rather than silently absorbed.</summary>
    public int IncomparableEmbeddings { get; init; }
}
