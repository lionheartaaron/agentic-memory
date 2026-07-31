using System.Numerics;

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

        var (dotProduct, magnitudeA, magnitudeB) = Accumulate(a, b);

        magnitudeA = MathF.Sqrt(magnitudeA);
        magnitudeB = MathF.Sqrt(magnitudeB);

        if (magnitudeA < float.Epsilon || magnitudeB < float.Epsilon)
            return 0f;

        return dotProduct / (magnitudeA * magnitudeB);
    }

    /// <summary>
    /// The three sums cosine similarity needs, computed with hardware SIMD where available.
    ///
    /// <see cref="Vector{T}"/> widens to whatever the running CPU supports (8 floats per step under
    /// AVX2, 16 under AVX-512) and degrades to a scalar loop on hardware that has neither, so there
    /// is no separate fallback path to keep correct. The remainder past the last full block is
    /// handled scalar-side.
    /// </summary>
    private static (float Dot, float MagA, float MagB) Accumulate(float[] a, float[] b)
    {
        var i = 0;
        float dot = 0f, magA = 0f, magB = 0f;

        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var dotV = Vector<float>.Zero;
            var magAV = Vector<float>.Zero;
            var magBV = Vector<float>.Zero;

            var limit = a.Length - a.Length % Vector<float>.Count;
            for (; i < limit; i += Vector<float>.Count)
            {
                var va = new Vector<float>(a, i);
                var vb = new Vector<float>(b, i);

                dotV  += va * vb;
                magAV += va * va;
                magBV += vb * vb;
            }

            dot  = Vector.Dot(dotV,  Vector<float>.One);
            magA = Vector.Dot(magAV, Vector<float>.One);
            magB = Vector.Dot(magBV, Vector<float>.One);
        }

        for (; i < a.Length; i++)
        {
            dot  += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        return (dot, magA, magB);
    }

    /// <summary>
    /// Dot product of two vectors already scaled to unit length, which for such vectors <em>is</em>
    /// the cosine similarity.
    ///
    /// Normalising once at cache-population time and reusing the result turns the per-comparison cost
    /// from three accumulations plus two square roots into one accumulation. Over a scoped set of a
    /// few thousand memories, scored on every query, that is the difference between the vector
    /// channel being free and being the dominant cost of a search.
    /// </summary>
    public static float DotProduct(float[] a, float[] b)
    {
        if (a is null || b is null || a.Length != b.Length || a.Length == 0)
            return 0f;

        var i = 0;
        var sum = 0f;

        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            var limit = a.Length - a.Length % Vector<float>.Count;

            for (; i < limit; i += Vector<float>.Count)
                acc += new Vector<float>(a, i) * new Vector<float>(b, i);

            sum = Vector.Dot(acc, Vector<float>.One);
        }

        for (; i < a.Length; i++)
            sum += a[i] * b[i];

        return sum;
    }

    /// <summary>
    /// Cosine similarity between two unit vectors, reporting comparability rather than hiding it.
    /// Mirrors <see cref="TryCosineSimilarity"/> but skips the magnitude work.
    /// </summary>
    public static bool TryUnitSimilarity(float[]? a, float[]? b, out float similarity)
    {
        similarity = 0f;
        if (a is null || b is null || a.Length == 0 || b.Length != a.Length)
            return false;

        similarity = Math.Clamp(DotProduct(a, b), -1f, 1f);
        return true;
    }

    /// <summary>
    /// Cosine similarity that reports comparability instead of hiding it.
    /// </summary>
    /// <returns>False when the vectors are missing, empty, or of different lengths — cases where
    /// there is no meaningful similarity rather than a similarity of zero.</returns>
    public static bool TryCosineSimilarity(float[]? a, float[]? b, out float similarity)
    {
        similarity = 0f;
        if (a is null || b is null || a.Length == 0 || b.Length != a.Length)
            return false;

        similarity = CosineSimilarity(a, b);
        return true;
    }

    /// <summary>
    /// Cosine similarity mapped to [0, 1], or null when the vectors are not comparable.
    ///
    /// Returning null matters: this previously folded a dimension mismatch into <c>CosineSimilarity</c>'s
    /// zero return, which the (x+1)/2 mapping then turned into a perfectly ordinary-looking 0.5.
    /// Swapping embedding models therefore degraded every comparison to a constant, silently, and
    /// 0.5 sits above the old "related memory" threshold.
    /// </summary>
    public static float? NormalizedCosineSimilarity(float[]? a, float[]? b)
    {
        if (!TryCosineSimilarity(a, b, out var cosine))
            return null;

        // Convert from [-1, 1] to [0, 1]
        return (cosine + 1f) / 2f;
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

        var magnitude = MathF.Sqrt(DotProduct(vector, vector));

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
