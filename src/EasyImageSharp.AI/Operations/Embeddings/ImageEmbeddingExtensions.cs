using EasyImageSharp.PixelFormats;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>
/// Image embeddings for similarity search, de-duplication and clustering, computed with the CLIP ViT-B/32 image tower
/// (<see cref="ModelRegistry.ImageEmbedding"/>). The embedding describes the root frame; compare two embeddings with
/// <see cref="ImageEmbeddings.CosineSimilarity(ReadOnlySpan{float}, ReadOnlySpan{float})"/>.
/// </summary>
/// <remarks>
/// <para>
/// Preprocessing follows the reference CLIP evaluation transform: the shorter side is resized to 224 px with bicubic
/// sampling (the longer side is truncated), the centred 224x224 square is cropped, and RGB values are normalised with the
/// CLIP mean / std. Content outside the centred square of a strongly non-square image does not contribute; alpha is
/// ignored. Results match the reference pipeline to within resampling differences (cosine similarity above 0.99), not
/// bit for bit.
/// </para>
/// <para>
/// Only the image tower is provided. Text-to-image search needs the CLIP text tower and its byte-level BPE tokenizer,
/// which this package does not ship; embeddings produced here are compatible with any CLIP ViT-B/32 text embedding
/// computed elsewhere from the same <c>openai/clip-vit-base-patch32</c> weights (after L2 normalisation).
/// </para>
/// <para>
/// The upstream model card describes CLIP as a research model and asks deployers to test it in their own domain;
/// similarity scores carry the biases of its web-scale training data.
/// </para>
/// </remarks>
public static class ImageEmbeddingExtensions
{
    /// <summary>
    /// Computes the L2-normalised (unit length) embedding of the root frame: 512 floats for the built-in model. The model
    /// download runs asynchronously and inference runs on the thread pool.
    /// </summary>
    public static async Task<float[]> GetImageEmbeddingAsync<TPixel>(
        this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(ai);
        InferenceSession session = await ai.GetSessionAsync(ModelRegistry.ImageEmbedding, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => ImageEmbeddingRunner.Embed(session, image, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="GetImageEmbeddingAsync{TPixel}"/>.</summary>
    public static float[] GetImageEmbedding<TPixel>(this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(ai);
        return ImageEmbeddingRunner.Embed(ai.GetSession(ModelRegistry.ImageEmbedding, cancellationToken), image, cancellationToken);
    }
}
