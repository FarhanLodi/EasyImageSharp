namespace EasyImageSharp.AI;

/// <summary>
/// Vector helpers for the embeddings returned by <see cref="ImageEmbeddingExtensions.GetImageEmbedding{TPixel}"/>:
/// L2 normalisation and cosine similarity, accumulated in double precision.
/// </summary>
public static class ImageEmbeddings
{
    /// <summary>
    /// The cosine similarity of two vectors of the same length, in -1..1: <c>dot(a, b) / (|a| |b|)</c>. For the
    /// L2-normalised embeddings of <c>GetImageEmbedding</c> this is simply their dot product; higher means more alike.
    /// Returns 0 when either vector is all zeros.
    /// </summary>
    /// <exception cref="ArgumentException">The vectors are empty or differ in length.</exception>
    public static float CosineSimilarity(ReadOnlySpan<float> first, ReadOnlySpan<float> second)
    {
        if (first.IsEmpty || first.Length != second.Length)
        {
            throw new ArgumentException(
                $"Cosine similarity needs two non-empty vectors of the same length; got {first.Length} and {second.Length} values.");
        }

        double dot = 0, firstNorm = 0, secondNorm = 0;
        for (int i = 0; i < first.Length; i++)
        {
            double a = first[i];
            double b = second[i];
            dot += a * b;
            firstNorm += a * a;
            secondNorm += b * b;
        }

        if (firstNorm == 0 || secondNorm == 0)
        {
            return 0f;
        }

        return (float)Math.Clamp(dot / (Math.Sqrt(firstNorm) * Math.Sqrt(secondNorm)), -1.0, 1.0);
    }

    /// <summary>
    /// Returns an L2-normalised copy of <paramref name="vector"/> (unit length), for example to re-normalise the mean of
    /// several embeddings before comparing it with others.
    /// </summary>
    /// <exception cref="ArgumentException">The vector is empty, all zeros or contains a non-finite value.</exception>
    public static float[] Normalize(ReadOnlySpan<float> vector)
    {
        if (vector.IsEmpty)
        {
            throw new ArgumentException("Cannot normalise an empty vector.", nameof(vector));
        }

        double sum = 0;
        foreach (float value in vector)
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentException("Cannot normalise a vector that contains a non-finite value.", nameof(vector));
            }

            sum += (double)value * value;
        }

        if (sum == 0)
        {
            throw new ArgumentException("Cannot normalise an all-zero vector.", nameof(vector));
        }

        double scale = 1.0 / Math.Sqrt(sum);
        var result = new float[vector.Length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (float)(vector[i] * scale);
        }

        return result;
    }
}
