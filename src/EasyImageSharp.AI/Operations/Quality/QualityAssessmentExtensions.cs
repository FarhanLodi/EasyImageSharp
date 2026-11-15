using EasyImageSharp.PixelFormats;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>
/// Learned, no-reference image quality: NIMA technical and aesthetic opinion scores (1-10) of the root frame, predicted
/// by the two MobileNet heads in <see cref="ModelRegistry.QualityAssessment"/>. For full-reference or classical
/// sharpness / noise measurements use the core library's quality metrics instead.
/// </summary>
/// <remarks>
/// Preprocessing follows the upstream evaluation pipeline exactly: the frame is stretched (not cropped) to 224x224 with
/// nearest-neighbour sampling and RGB values are scaled to -1..1. The networks therefore see a 224 px thumbnail: they
/// respond to blur, noise, compression, exposure and composition at that scale, not to pixel-level detail of a large
/// photograph. Scores are predictions of a mean human rating and are most useful for ranking similar images.
/// </remarks>
public static class QualityAssessmentExtensions
{
    /// <summary>
    /// Predicts the technical and aesthetic quality of the root frame. The model download runs asynchronously and
    /// inference runs on the thread pool.
    /// </summary>
    public static async Task<QualityAssessment> AssessQualityAsync<TPixel>(
        this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(ai);
        InferenceSession session = await ai.GetSessionAsync(ModelRegistry.QualityAssessment, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => QualityAssessmentRunner.Assess(session, image, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="AssessQualityAsync{TPixel}"/>.</summary>
    public static QualityAssessment AssessQuality<TPixel>(this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(ai);
        return QualityAssessmentRunner.Assess(ai.GetSession(ModelRegistry.QualityAssessment, cancellationToken), image, cancellationToken);
    }
}
