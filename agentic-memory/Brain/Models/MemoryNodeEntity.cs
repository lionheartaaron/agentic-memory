using LiteDB;

namespace AgenticMemory.Brain.Models;

/// <summary>
/// LiteDB entity for memory nodes with fuzzy search support, time decay, and reinforcement
/// </summary>
public class MemoryNodeEntity : IEquatable<MemoryNodeEntity>
{
    /// <summary>
    /// Unique identifier for the memory node
    /// </summary>
    [BsonId]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Title of the memory node
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Brief summary of the memory content
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Full content of the memory node
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Normalized content for text search (lowercase, trimmed)
    /// </summary>
    public string ContentNormalized { get; set; } = string.Empty;

    /// <summary>
    /// Tags for categorization
    /// </summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Trigrams generated from content for fuzzy matching
    /// </summary>
    public List<string> Trigrams { get; set; } = [];

    /// <summary>
    /// When the memory was first created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last time this memory was accessed or reinforced
    /// </summary>
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Base strength score before decay (starts at 1.0)
    /// </summary>
    public double BaseStrength { get; set; } = 1.0;

    /// <summary>
    /// Decay rate per day (lower = slower decay)
    /// </summary>
    public double DecayRate { get; set; } = 0.1;

    /// <summary>
    /// Embedding vector stored as bytes for efficient storage
    /// </summary>
    public byte[]? EmbeddingBytes { get; set; }

    /// <summary>
    /// Linked memory node IDs for graph relationships
    /// </summary>
    public List<Guid> LinkedNodeIds { get; set; } = [];

    /// <summary>
    /// Number of times this memory has been accessed/reinforced
    /// </summary>
    public int AccessCount { get; set; } = 0;

    /// <summary>
    /// Whether this memory has been archived (soft deleted or superseded)
    /// </summary>
    public bool IsArchived { get; set; } = false;

    /// <summary>
    /// ID of the memory that superseded this one (if archived due to consolidation)
    /// </summary>
    public Guid? SupersededBy { get; set; }

    /// <summary>
    /// When this memory became valid/current (for temporal tracking)
    /// </summary>
    public DateTime ValidFrom { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this memory was superseded (null if still current)
    /// </summary>
    public DateTime? ValidUntil { get; set; }

    /// <summary>
    /// IDs of memories that this memory superseded
    /// </summary>
    public List<Guid> SupersededIds { get; set; } = [];

    /// <summary>
    /// Whether this memory is currently active (not archived and not superseded)
    /// </summary>
    public bool IsCurrent => ValidUntil is null && !IsArchived;

    /// <summary>
    /// Optional expiration time - memory will be auto-deleted after this time
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Initial importance score (0.0-1.0) - affects decay rate and search ranking
    /// </summary>
    public double Importance { get; set; } = 0.5;

    /// <summary>
    /// Whether this memory is pinned (never decays)
    /// </summary>
    public bool IsPinned { get; set; } = false;

    /// <summary>
    /// Check if this memory has expired
    /// </summary>
    public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;

    /// <summary>
    /// Calculate the current strength with exponential time decay
    /// Strength = BaseStrength * e^(-DecayRate * DaysSinceAccess)
    /// Pinned memories don't decay. Importance affects effective decay rate.
    /// </summary>
    public double GetCurrentStrength()
    {
        if (IsPinned) return BaseStrength;
        
        var daysSinceAccess = (DateTime.UtcNow - LastAccessedAt).TotalDays;
        var effectiveDecayRate = DecayRate * (1.0 - Importance * 0.5); // Higher importance = slower decay
        return BaseStrength * Math.Exp(-effectiveDecayRate * daysSinceAccess);
    }

    /// <summary>
    /// Reinforce this memory by updating access time and increasing strength
    /// </summary>
    public void Reinforce(double reinforcementFactor = 0.1)
    {
        LastAccessedAt = DateTime.UtcNow;
        AccessCount++;
        // Increase base strength with diminishing returns
        BaseStrength += reinforcementFactor / Math.Sqrt(AccessCount);
    }

    /// <summary>
    /// Get the embedding as a float array
    /// </summary>
    public float[]? GetEmbedding()
    {
        if (EmbeddingBytes is null || EmbeddingBytes.Length == 0)
            return null;

        var floats = new float[EmbeddingBytes.Length / sizeof(float)];
        Buffer.BlockCopy(EmbeddingBytes, 0, floats, 0, EmbeddingBytes.Length);
        return floats;
    }

    /// <summary>
    /// Set the embedding from a float array
    /// </summary>
    public void SetEmbedding(float[] embedding)
    {
        EmbeddingBytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, EmbeddingBytes, 0, EmbeddingBytes.Length);
    }

    #region IEquatable<MemoryNodeEntity>

    /// <summary>
    /// Determines whether the specified MemoryNodeEntity is equal to the current instance based on Id.
    /// </summary>
    public bool Equals(MemoryNodeEntity? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id == other.Id;
    }

    /// <summary>
    /// Determines whether the specified object is equal to the current instance.
    /// </summary>
    public override bool Equals(object? obj)
    {
        return Equals(obj as MemoryNodeEntity);
    }

    /// <summary>
    /// Returns a hash code based on the Id.
    /// </summary>
    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }

    public static bool operator ==(MemoryNodeEntity? left, MemoryNodeEntity? right)
    {
        if (left is null) return right is null;
        return left.Equals(right);
    }

    public static bool operator !=(MemoryNodeEntity? left, MemoryNodeEntity? right)
    {
        return !(left == right);
    }

    #endregion
}
