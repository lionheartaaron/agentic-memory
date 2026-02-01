namespace AgenticMemory.Brain.Embeddings;

using AgenticMemory.Brain.Interfaces;

/// <summary>
/// Null object pattern implementation of IEmbeddingService for when embeddings are disabled or unavailable.
/// </summary>
public sealed class NullEmbeddingService : IEmbeddingService
{
    public static readonly NullEmbeddingService Instance = new();

    private NullEmbeddingService() { }

    public int Dimensions => 0;
    public bool IsAvailable => false;

    public Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<float>());

    public void Dispose() { }
}
