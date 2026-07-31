namespace AgenticMemory.Brain.Slots;

/// <summary>How many simultaneous values a predicate can hold.</summary>
public enum SlotCardinality
{
    /// <summary>Exactly one current value. A new value replaces the old, which becomes history.</summary>
    Singular = 0,

    /// <summary>Logically one value, but changing it is surprising enough to be worth confirming
    /// (people change their favourite food, but rarely by accident).</summary>
    SingularSoft = 1,

    /// <summary>Many values coexist. Never supersedes.</summary>
    MultiValued = 2,
}

/// <summary>What to do when a genuine contradiction is detected on a slot.</summary>
public enum ConflictPolicy
{
    /// <summary>The newer assertion wins; the older is retained as superseded history.</summary>
    LatestWins = 0,

    /// <summary>Keep both, record the conflict, and let the companion ask the user about it.</summary>
    EscalateToUser = 1,

    /// <summary>Should never change. Any contradiction is recorded and the original kept.</summary>
    Immutable = 2,
}

public sealed record SlotDefinition(
    string Predicate,
    SlotCardinality Cardinality,
    ConflictPolicy Policy,
    bool NeverAutoRemove = false);

/// <summary>
/// The set of known structured predicates and how conflicts on each are resolved.
///
/// This replaces cosine distance as the supersede trigger. Similarity is only good at proposing
/// candidates; whether replacing a fact is <em>legal</em> is a property of the slot, and at
/// raw cosine 0.60 ("same topic") the old rule was archiving unrelated facts.
///
/// Unknown predicates default to <see cref="SlotCardinality.MultiValued"/> so that anything the
/// registry has not been taught about coexists. Over-retaining is recoverable; deleting is not.
/// </summary>
public sealed class SlotRegistry
{
    private readonly Dictionary<string, SlotDefinition> _slots;

    public SlotRegistry(IEnumerable<SlotDefinition>? overrides = null)
    {
        _slots = new Dictionary<string, SlotDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in Defaults)
            _slots[slot.Predicate] = slot;

        if (overrides is null) return;
        foreach (var slot in overrides)
        {
            if (Normalize(slot.Predicate) is not { } key) continue;
            _slots[key] = slot with { Predicate = key };
        }
    }

    /// <summary>
    /// The safe fallback for any predicate the registry does not know: coexist, never supersede.
    /// </summary>
    public static SlotDefinition Unknown { get; } =
        new("*", SlotCardinality.MultiValued, ConflictPolicy.EscalateToUser);

    public static IReadOnlyList<SlotDefinition> Defaults { get; } =
    [
        new("employer",              SlotCardinality.Singular,     ConflictPolicy.LatestWins),
        new("job_title",             SlotCardinality.Singular,     ConflictPolicy.LatestWins),
        new("city_of_residence",     SlotCardinality.Singular,     ConflictPolicy.LatestWins),
        new("country_of_residence",  SlotCardinality.Singular,     ConflictPolicy.LatestWins),
        new("relationship_status",   SlotCardinality.Singular,     ConflictPolicy.LatestWins),
        new("current_mood",          SlotCardinality.Singular,     ConflictPolicy.LatestWins),
        new("nickname_for_user",     SlotCardinality.Singular,     ConflictPolicy.LatestWins),
        new("pronouns",              SlotCardinality.Singular,     ConflictPolicy.LatestWins),

        // Identity facts that should not drift. A contradiction here is far more likely to be a
        // bad extraction than a real change, so keep the original and surface the conflict.
        new("full_name",             SlotCardinality.Singular,     ConflictPolicy.Immutable,     NeverAutoRemove: true),
        new("birthday",              SlotCardinality.Singular,     ConflictPolicy.Immutable,     NeverAutoRemove: true),

        // Real but slow-moving preferences — worth a "didn't you say you loved ramen?".
        new("favourite_food",        SlotCardinality.SingularSoft, ConflictPolicy.EscalateToUser),
        new("favourite_colour",      SlotCardinality.SingularSoft, ConflictPolicy.EscalateToUser),
        new("favourite_music",       SlotCardinality.SingularSoft, ConflictPolicy.EscalateToUser),

        // Sets. These must never supersede one another — a second pet does not delete the first.
        new("allergies",             SlotCardinality.MultiValued,  ConflictPolicy.EscalateToUser, NeverAutoRemove: true),
        new("medical_condition",     SlotCardinality.MultiValued,  ConflictPolicy.EscalateToUser, NeverAutoRemove: true),
        new("hobbies",               SlotCardinality.MultiValued,  ConflictPolicy.LatestWins),
        new("friends",               SlotCardinality.MultiValued,  ConflictPolicy.LatestWins),
        new("family_member",         SlotCardinality.MultiValued,  ConflictPolicy.LatestWins,     NeverAutoRemove: true),
        new("pets",                  SlotCardinality.MultiValued,  ConflictPolicy.LatestWins),
        new("goals",                 SlotCardinality.MultiValued,  ConflictPolicy.LatestWins),
        new("dislikes",              SlotCardinality.MultiValued,  ConflictPolicy.LatestWins),
        new("shared_joke",           SlotCardinality.MultiValued,  ConflictPolicy.LatestWins),
    ];

    public static string? Normalize(string? predicate) =>
        string.IsNullOrWhiteSpace(predicate)
            ? null
            : predicate.Trim().ToLowerInvariant().Replace(' ', '_');

    /// <summary>
    /// Resolve a predicate. Returns <see cref="Unknown"/> (coexist) for anything unregistered,
    /// including null — an unslotted free-text memory can never supersede another.
    /// </summary>
    public SlotDefinition Resolve(string? predicate)
    {
        var key = Normalize(predicate);
        if (key is null) return Unknown;
        return _slots.TryGetValue(key, out var slot) ? slot : Unknown;
    }

    public bool IsKnown(string? predicate)
    {
        var key = Normalize(predicate);
        return key is not null && _slots.ContainsKey(key);
    }

    public IReadOnlyCollection<SlotDefinition> All => _slots.Values;
}
