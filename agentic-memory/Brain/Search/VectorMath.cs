namespace AgenticMemory.Brain.Search;

/// <summary>
/// Vector mathematics utilities for embedding similarity calculations
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// Calculate the cosine similarity between two vectors
    /// </summary>
    /// <param name="a">First vector</param>
    /// <param name="b">Second vector</param>
    /// <returns>Cosine similarity in range [-1, 1], or 0 if vectors are invalid</returns>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a is null || b is null || a.Length != b.Length || a.Length == 0)
            return 0f;

        float dotProduct = 0f;
        float magnitudeA = 0f;
        float magnitudeB = 0f;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            magnitudeA += a[i] * a[i];
            magnitudeB += b[i] * b[i];
        }

        magnitudeA = MathF.Sqrt(magnitudeA);
        magnitudeB = MathF.Sqrt(magnitudeB);

        if (magnitudeA < float.Epsilon || magnitudeB < float.Epsilon)
            return 0f;

        return dotProduct / (magnitudeA * magnitudeB);
    }

    /// <summary>
    /// Calculate the cosine similarity and normalize to [0, 1] range
    /// </summary>
    /// <param name="a">First vector</param>
    /// <param name="b">Second vector</param>
    /// <returns>Normalized similarity in range [0, 1]</returns>
    public static float NormalizedCosineSimilarity(float[] a, float[] b)
    {
        // Convert from [-1, 1] to [0, 1]
        return (CosineSimilarity(a, b) + 1f) / 2f;
    }

    /// <summary>
    /// Calculate the Euclidean distance between two vectors
    /// </summary>
    /// <param name="a">First vector</param>
    /// <param name="b">Second vector</param>
    /// <returns>Euclidean distance, or float.MaxValue if vectors are invalid</returns>
    public static float EuclideanDistance(float[] a, float[] b)
    {
        if (a is null || b is null || a.Length != b.Length || a.Length == 0)
            return float.MaxValue;

        float sum = 0f;

        for (int i = 0; i < a.Length; i++)
        {
            float diff = a[i] - b[i];
            sum += diff * diff;
        }

        return MathF.Sqrt(sum);
    }

    /// <summary>
    /// Normalize a vector to unit length
    /// </summary>
    /// <param name="vector">Vector to normalize</param>
    /// <returns>New normalized vector, or empty array if input is invalid</returns>
    public static float[] Normalize(float[] vector)
    {
        if (vector is null || vector.Length == 0)
            return [];

        float magnitude = 0f;
        for (int i = 0; i < vector.Length; i++)
        {
            magnitude += vector[i] * vector[i];
        }

        magnitude = MathF.Sqrt(magnitude);

        if (magnitude < float.Epsilon)
            return new float[vector.Length];

        var normalized = new float[vector.Length];
        for (int i = 0; i < vector.Length; i++)
        {
            normalized[i] = vector[i] / magnitude;
        }

        return normalized;
    }
}
