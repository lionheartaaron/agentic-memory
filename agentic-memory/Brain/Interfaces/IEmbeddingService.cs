namespace AgenticMemory.Brain.Interfaces;

/// <summary>
/// Service for generating text embeddings using a local model
/// </summary>
public interface IEmbeddingService : IDisposable
{
    /// <summary>
    /// Generate an embedding vector for the given text
    /// </summary>
    /// <param name="text">The text to embed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The embedding vector as a float array</returns>
    Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// The number of dimensions in the embedding vectors
    /// </summary>
    int Dimensions { get; }

    /// <summary>
    /// Whether the embedding service is available and ready
    /// </summary>
    bool IsAvailable { get; }
}
