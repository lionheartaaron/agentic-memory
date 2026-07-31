using LiteDB;

namespace AgenticMemory.Brain.Models;

/// <summary>
/// A single memory.
///
/// Five orthogonal axes govern behaviour, and collapsing any of them into free-text tags is what
/// previously caused cross-companion leakage and false conflicts:
///   1. Tenancy   — <see cref="UserId"/>, a hard boundary that is never crossed.
///   2. Visibility— <see cref="Visibility"/> / <see cref="CompanionIds"/>: who may recall it.
///   3. Subject   — <see cref="SubjectRef"/>: who it is <em>about</em>.
///   4. Slot      — <see cref="Predicate"/>: which attribute is asserted; drives conflict handling.
///   5. Type      — <see cref="Type"/>: which lifecycle rules apply.
/// <see cref="Tags"/> remains for soft categorisation only and is never load-bearing.
/// </summary>
public class MemoryNodeEntity : IEquatable<MemoryNodeEntity>
{
    /// <summary>Upper bound on accumulated strength, so that a frequently-recalled old memory
    /// cannot permanently outrank newer ones. Reinforcement is a ranking signal only.</summary>
    public const double MaxBaseStrength = 5.0;

    [BsonId]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Incremented on every persisted mutation. Used for optimistic concurrency: a save
    /// whose expected version no longer matches is rejected rather than silently clobbering.</summary>
    public long Version { get; set; }

    // ── Hard scope — indexed, exact-match, pushed into the query. Never fuzzy. ────────────────

    /// <summary>Owning user. The tenancy boundary.</summary>
    public string UserId { get; set; } = MemoryScope.DefaultUserId;

    /// <summary>Whether every companion knows this, or only the listed ones.</summary>
    public MemoryVisibility Visibility { get; set; } = MemoryVisibility.Global;

    /// <summary>Companions that may recall this. Empty when <see cref="Visibility"/> is Global.
    /// Values are normalised via <see cref="MemoryScope.NormalizeId"/>.</summary>
    public List<string> CompanionIds { get; set; } = [];

    // ── Subject and slot — drive conflict resolution ──────────────────────────────────────────

    /// <summary>Who this memory is about: "user", "companion:aria", "relationship:aria",
    /// "person:mia". Distinct from visibility.</summary>
    public string SubjectRef { get; set; } = SubjectRefs.User;

    /// <summary>Structured attribute being asserted ("employer", "favourite_food"). Null for
    /// unstructured memories, which can never supersede anything.</summary>
    public string? Predicate { get; set; }

    /// <summary>Normalised value, used to tell "same slot, same value" (duplicate) from
    /// "same slot, different value" (contradiction), and to key multi-valued slots.</summary>
    public string? ValueKey { get; set; }

    // ── Classification ────────────────────────────────────────────────────────────────────────

    public MemoryType Type { get; set; } = MemoryType.Semantic;
    public Sensitivity Sensitivity { get; set; } = Sensitivity.Normal;

    /// <summary>Soft, free-form categorisation. Must never be used to enforce a privacy boundary.</summary>
    public List<string> Tags { get; set; } = [];

    // ── Content ───────────────────────────────────────────────────────────────────────────────

    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    /// <summary>What the user actually said, preserved verbatim for provenance and for quoting back.</summary>
    public string? VerbatimQuote { get; set; }

    /// <summary>Short normalised key kept under LiteDB's index key limit. Indexed.</summary>
    public string ContentNormalized { get; set; } = string.Empty;

    /// <summary>Full lowercased searchable text including <see cref="Content"/>. Deliberately
    /// <em>not</em> indexed — the retrieval pipeline scores it in memory over an already
    /// scope-filtered candidate set, so no index key limit applies and content is searchable.</summary>
    public string SearchText { get; set; } = string.Empty;

    /// <summary>Trigrams over title/summary/tags for typo tolerance. Not indexed: one index entry
    /// per trigram per memory produced tens of millions of entries at modest corpus sizes.</summary>
    public List<string> Trigrams { get; set; } = [];

    // ── Provenance — arbitrates conflicts ─────────────────────────────────────────────────────

    public MemorySource Source { get; set; } = MemorySource.UserStated;
    public double Confidence { get; set; } = 1.0;
    public string? ConversationId { get; set; }
    public string? MessageId { get; set; }

    // ── Bitemporal ────────────────────────────────────────────────────────────────────────────

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the system learned this.</summary>
    public DateTime IngestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When it was true in the world, if known. Lets "I dated Sam in 2019" and
    /// "I'm dating Sam" be distinguished.</summary>
    public DateTime? EventTime { get; set; }

    public DateTime ValidFrom { get; set; } = DateTime.UtcNow;
    public DateTime? ValidUntil { get; set; }

    /// <summary>Expiry for <see cref="MemoryType.Ephemeral"/> memories. Enforced by the scope
    /// predicate on every read and by the maintenance sweep.</summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;

    // ── Lifecycle ─────────────────────────────────────────────────────────────────────────────

    public MemoryState State { get; set; } = MemoryState.Active;
    public Guid? SupersededBy { get; set; }
    public List<Guid> SupersededIds { get; set; } = [];
    public Guid? MergedInto { get; set; }

    /// <summary>Related memories, for graph expansion during retrieval.</summary>
    public List<Guid> LinkedNodeIds { get; set; } = [];

    // ── Ranking signals only — never retention ────────────────────────────────────────────────

    public double Importance { get; set; } = 0.5;
    public double BaseStrength { get; set; } = 1.0;
    public double DecayRate { get; set; } = 0.1;
    public int AccessCount { get; set; }
    public bool IsPinned { get; set; }

    // ── Embedding, self-describing ────────────────────────────────────────────────────────────

    public byte[]? EmbeddingBytes { get; set; }

    /// <summary>Model that produced <see cref="EmbeddingBytes"/>. Vectors from different models
    /// are incomparable; without this a model swap silently degraded every similarity to a
    /// constant 0.5.</summary>
    public string? EmbeddingModel { get; set; }

    public int EmbeddingDim { get; set; }

    // ── Derived ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Backwards-compatible view of <see cref="State"/>. Not persisted — <see cref="State"/>
    /// is the source of truth.</summary>
    [BsonIgnore]
    public bool IsArchived
    {
        get => State != MemoryState.Active;
        set
        {
            if (value)
            {
                if (State == MemoryState.Active) State = MemoryState.Archived;
            }
            else
            {
                State = MemoryState.Active;
            }
        }
    }

    [BsonIgnore]
    public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;

    /// <summary>Retrievable right now: active, and not past its expiry.</summary>
    [BsonIgnore]
    public bool IsCurrent => State == MemoryState.Active && !IsExpired;

    /// <summary>
    /// Ranking strength with exponential time decay. This affects ordering only — it must never
    /// determine whether a memory is retained.
    /// </summary>
    public double GetCurrentStrength()
    {
        if (IsPinned) return BaseStrength;

        var daysSinceAccess = (DateTime.UtcNow - LastAccessedAt).TotalDays;
        if (daysSinceAccess <= 0) return BaseStrength;

        var effectiveDecayRate = DecayRate * (1.0 - Importance * 0.5);
        return BaseStrength * Math.Exp(-effectiveDecayRate * daysSinceAccess);
    }

    /// <summary>
    /// Record a retrieval. Strength saturates at <see cref="MaxBaseStrength"/> so that an old,
    /// frequently-recalled memory cannot crowd out newer ones indefinitely.
    /// </summary>
    public void Reinforce(double reinforcementFactor = 0.1)
    {
        LastAccessedAt = DateTime.UtcNow;
        AccessCount++;
        BaseStrength = Math.Min(MaxBaseStrength, BaseStrength + reinforcementFactor / Math.Sqrt(AccessCount));
    }

    public float[]? GetEmbedding()
    {
        if (EmbeddingBytes is null || EmbeddingBytes.Length == 0)
            return null;

        var floats = new float[EmbeddingBytes.Length / sizeof(float)];
        Buffer.BlockCopy(EmbeddingBytes, 0, floats, 0, EmbeddingBytes.Length);
        return floats;
    }

    public void SetEmbedding(float[] embedding, string? modelId = null)
    {
        EmbeddingBytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, EmbeddingBytes, 0, EmbeddingBytes.Length);
        EmbeddingDim = embedding.Length;
        if (modelId is not null) EmbeddingModel = modelId;
    }

    /// <summary>
    /// Whether this memory's vector can be meaningfully compared against the given model output.
    /// </summary>
    public bool HasComparableEmbedding(string? modelId, int dimensions) =>
        EmbeddingBytes is { Length: > 0 }
        && EmbeddingDim == dimensions
        && (EmbeddingModel is null || modelId is null || string.Equals(EmbeddingModel, modelId, StringComparison.Ordinal));

    #region IEquatable<MemoryNodeEntity>

    public bool Equals(MemoryNodeEntity? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id == other.Id;
    }

    public override bool Equals(object? obj) => Equals(obj as MemoryNodeEntity);

    public override int GetHashCode() => Id.GetHashCode();

    public static bool operator ==(MemoryNodeEntity? left, MemoryNodeEntity? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(MemoryNodeEntity? left, MemoryNodeEntity? right) => !(left == right);

    #endregion
}
