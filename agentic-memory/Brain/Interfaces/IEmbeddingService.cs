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
    /// Identifies the model producing these vectors, e.g. "all-MiniLM-L6-v2".
    ///
    /// Stored alongside every embedding so that vectors from different models are recognised as
    /// incomparable rather than silently compared.
    /// </summary>
    string ModelId { get; }

    /// <summary>
    /// Whether the embedding service is available and ready
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Whether the model recognises <paramref name="term"/> as a word in its own right, rather than
    /// reassembling it from sub-word fragments or falling back to the unknown token.
    ///
    /// This is how a meaningless query is told apart from a merely unanswerable one. A sentence
    /// transformer given invented tokens still returns a perfectly well-formed vector, and that
    /// vector sits near the centre of the corpus — so gibberish scores <em>above</em> many genuine
    /// matches and lifts every similarity at once. Knowing that the words themselves are real is the
    /// signal the geometry cannot supply.
    ///
    /// Defaults to true, so an implementation without vocabulary information never causes a query to
    /// be treated as nonsense.
    /// </summary>
    bool IsKnownTerm(string term) => true;
}
