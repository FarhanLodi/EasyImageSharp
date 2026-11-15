using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>
/// Face detection and redaction on <see cref="Image{TPixel}"/> with the YuNet detector
/// (<see cref="ModelRegistry.FaceDetection"/>, MIT, about 230 KB). The <c>Async</c> variants download the model
/// asynchronously on first use and run inference on the thread pool; the synchronous variants block. Coordinates are
/// source-image pixels of the root frame.
/// </summary>
public static class FaceImageExtensions
{
    /// <summary>
    /// Finds the faces on the root frame: boxes, five landmarks and confidences in source-image pixels, sorted by
    /// descending confidence. See <see cref="FaceDetectionOptions"/> for how image size affects which faces are found.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <exception cref="ModelContractException">The model is not a YuNet-compatible export.</exception>
    public static async Task<IReadOnlyList<DetectedFace>> DetectFacesAsync<TPixel>(
        this Image<TPixel> image, ImageAiSession ai, FaceDetectionOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        FaceDetectionOptions effective = Check(image, ai, options);
        InferenceSession session = await ai.GetSessionAsync(ModelRegistry.FaceDetection, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => FaceDetector.Detect(session, image, effective, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="DetectFacesAsync{TPixel}"/>.</summary>
    public static IReadOnlyList<DetectedFace> DetectFaces<TPixel>(
        this Image<TPixel> image, ImageAiSession ai, FaceDetectionOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        FaceDetectionOptions effective = Check(image, ai, options);
        return FaceDetector.Detect(ai.GetSession(ModelRegistry.FaceDetection, cancellationToken), image, effective, cancellationToken);
    }

    /// <summary>
    /// Detects the faces on every frame (each frame on its own) and obscures them in place with the core blur,
    /// pixelate or fill operations. Returns the faces found on the root frame (before padding). Read the remarks on
    /// <see cref="FaceRedactionOptions"/> before relying on it for anonymisation.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static async Task<IReadOnlyList<DetectedFace>> RedactFacesAsync<TPixel>(
        this Image<TPixel> image, ImageAiSession ai, FaceRedactionOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        FaceRedactionOptions effective = Check(image, ai, options);
        InferenceSession session = await ai.GetSessionAsync(ModelRegistry.FaceDetection, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => FaceRedactor.DetectAndRedact(session, image, effective, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="RedactFacesAsync{TPixel}(Image{TPixel}, ImageAiSession, FaceRedactionOptions?, CancellationToken)"/>.</summary>
    public static IReadOnlyList<DetectedFace> RedactFaces<TPixel>(
        this Image<TPixel> image, ImageAiSession ai, FaceRedactionOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        FaceRedactionOptions effective = Check(image, ai, options);
        return FaceRedactor.DetectAndRedact(ai.GetSession(ModelRegistry.FaceDetection, cancellationToken), image, effective, cancellationToken);
    }

    /// <summary>
    /// Obscures the given face regions in place on every frame, without running a detector: for a detect, review,
    /// redact workflow, or regions found by other means. <see cref="FaceRedactionOptions.Detection"/> is ignored.
    /// </summary>
    public static void RedactFaceRegions<TPixel>(
        this Image<TPixel> image, IReadOnlyList<DetectedFace> faces, FaceRedactionOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(faces);
        FaceRedactionOptions effective = options ?? new FaceRedactionOptions();
        effective.Validate();
        foreach (DetectedFace? face in faces)
        {
            ArgumentNullException.ThrowIfNull(face, nameof(faces));
        }

        FaceRedactor.Redact(image, faces, effective, cancellationToken);
    }

    private static FaceDetectionOptions Check<TPixel>(Image<TPixel> image, ImageAiSession ai, FaceDetectionOptions? options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(ai);
        FaceDetectionOptions effective = options ?? new FaceDetectionOptions();
        effective.Validate();
        return effective;
    }

    private static FaceRedactionOptions Check<TPixel>(Image<TPixel> image, ImageAiSession ai, FaceRedactionOptions? options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(ai);
        FaceRedactionOptions effective = options ?? new FaceRedactionOptions();
        effective.Validate();
        return effective;
    }
}
