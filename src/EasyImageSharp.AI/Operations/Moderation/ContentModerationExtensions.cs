using EasyImageSharp.PixelFormats;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>
/// Content moderation: estimates whether an image (its root frame) contains sexual / explicit ("not safe for work")
/// material, with the classifier in <see cref="ModelRegistry.ContentModeration"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is a small statistical classifier (ViT-Tiny at 384x384, trained by its authors on a proprietary set of photos,
/// drawings, memes and AI-generated images). It makes mistakes in both directions, it only covers sexual / explicit
/// content (not violence, gore, hate symbols, or anything else), and it has no notion of context such as medical or
/// artistic intent. It is <b>not a guarantee</b> and must not be the only safeguard where a miss matters; tune the
/// threshold of <see cref="ContentClassification.IsUnsafe(float)"/> on your own data and keep a human review path.
/// </para>
/// <para>
/// Preprocessing follows the reference timm evaluation transform: the shorter side is resized to 384 px with bicubic
/// sampling, the centred 384x384 square is cropped and RGB values are scaled to -1..1. Content outside the centred square
/// of a strongly non-square image is not seen by the model; classify crops (or tiles) separately when that matters.
/// Alpha is ignored.
/// </para>
/// </remarks>
public static class ContentModerationExtensions
{
    /// <summary>
    /// Classifies the root frame and returns the class probabilities. The model download runs asynchronously and
    /// inference runs on the thread pool.
    /// </summary>
    public static async Task<ContentClassification> ClassifyContentAsync<TPixel>(
        this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(ai);
        InferenceSession session = await ai.GetSessionAsync(ModelRegistry.ContentModeration, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => ContentModerationRunner.Classify(session, image, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="ClassifyContentAsync{TPixel}"/>.</summary>
    public static ContentClassification ClassifyContent<TPixel>(this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(ai);
        return ContentModerationRunner.Classify(ai.GetSession(ModelRegistry.ContentModeration, cancellationToken), image, cancellationToken);
    }
}
