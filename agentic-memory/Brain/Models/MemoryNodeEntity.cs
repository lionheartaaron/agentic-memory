using LiteDB;

namespace AgenticMemory.Brain.Models;

/// <summary>
/// LiteDB entity for memory nodes with fuzzy search support, time decay, and reinforcement
/// </summary>
public class MemoryNodeEntity
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
    /// Calculate the current strength with exponential time decay
    /// Strength = BaseStrength * e^(-DecayRate * DaysSinceAccess)
    /// </summary>
    public double GetCurrentStrength()
    {
        var daysSinceAccess = (DateTime.UtcNow - LastAccessedAt).TotalDays;
        return BaseStrength * Math.Exp(-DecayRate * daysSinceAccess);
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

    /// <summary>
    /// Convert to the handler model for API responses
    /// </summary>
    public AgenticMemory.Http.Handlers.MemoryNode ToHandlerModel()
    {
        return new AgenticMemory.Http.Handlers.MemoryNode
        {
            Id = Id,
            Title = Title,
            Summary = Summary,
            Content = Content,
            Tags = Tags,
            CreatedAt = CreatedAt,
            LastAccessedAt = LastAccessedAt,
            ReinforcementScore = GetCurrentStrength(),
            LinkedNodeIds = LinkedNodeIds,
            Embedding = GetEmbedding()
        };
    }

    /// <summary>
    /// Create from the handler create request
    /// </summary>
    public static MemoryNodeEntity FromCreateRequest(AgenticMemory.Http.Handlers.CreateMemoryRequest request)
    {
        var entity = new MemoryNodeEntity
        {
            Title = request.Title,
            Summary = request.Summary,
            Content = request.Content ?? string.Empty,
            Tags = request.Tags ?? []
        };

        // Generate normalized content for search
        entity.ContentNormalized = $"{entity.Title} {entity.Summary} {entity.Content}".ToLowerInvariant().Trim();

        return entity;
    }
}
