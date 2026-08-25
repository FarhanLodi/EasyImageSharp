using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>
/// AI operations on <see cref="Image{TPixel}"/>. Every operation resolves its model through the
/// <see cref="ImageAiSession"/> (download + verify on first use, cached afterwards). The <c>Async</c> variants
/// perform the model download asynchronously and run inference on the thread pool; the synchronous variants
/// block the calling thread. Mutating operations work in place on every frame; use <c>Clone()</c> first to keep
/// the original.
/// </summary>
public static class AiImageExtensions
{
    // ----- Orientation -----

    /// <summary>Detects whether the page (root frame) is rotated by 0 / 90 / 180 / 270 degrees (PP-LCNet doc-ori).</summary>
    public static async Task<OrientationResult> DetectOrientationAsync<TPixel>(
        this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        InferenceSession session = await ai.GetSessionAsync(ModelRegistry.DocumentOrientation, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => AiOperations.DetectOrientation(session, image), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="DetectOrientationAsync{TPixel}"/>.</summary>
    public static OrientationResult DetectOrientation<TPixel>(this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        return AiOperations.DetectOrientation(ai.GetSession(ModelRegistry.DocumentOrientation, cancellationToken), image);
    }

    /// <summary>
    /// Detects the page orientation and losslessly rotates the image upright in place (every frame is classified and
    /// rotated individually). Returns the rotation applied to the root frame.
    /// </summary>
    public static async Task<RotateMode> AutoOrientAsync<TPixel>(
        this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        InferenceSession session = await ai.GetSessionAsync(ModelRegistry.DocumentOrientation, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => AiOperations.AutoOrient(session, image), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="AutoOrientAsync{TPixel}"/>.</summary>
    public static RotateMode AutoOrient<TPixel>(this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        return AiOperations.AutoOrient(ai.GetSession(ModelRegistry.DocumentOrientation, cancellationToken), image);
    }

    // ----- Dewarp -----

    /// <summary>Flattens a curled / keystoned page in place (UVDoc); the result keeps the source dimensions.</summary>
    public static async Task DewarpDocumentAsync<TPixel>(this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        InferenceSession session = await ai.GetSessionAsync(ModelRegistry.DocumentDewarp, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => AiOperations.Dewarp(session, image, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="DewarpDocumentAsync{TPixel}"/>.</summary>
    public static void DewarpDocument<TPixel>(this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        AiOperations.Dewarp(ai.GetSession(ModelRegistry.DocumentDewarp, cancellationToken), image, cancellationToken);
    }

    // ----- Super-resolution -----

    /// <summary>
    /// Upscales the image in place by <paramref name="factor"/> with the super-resolution network (Real-ESRGAN x4
    /// contract, tiled 256 px with 16 px overlap). When the network's native scale differs from
    /// <paramref name="factor"/> the result is resampled to exactly <c>factor</c> times the source size.
    /// </summary>
    public static async Task UpscaleAsync<TPixel>(this Image<TPixel> image, ImageAiSession ai, int factor = 4, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        InferenceSession session = await ai.GetSessionAsync(ModelRegistry.SuperResolutionX4, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => AiOperations.Upscale(session, image, factor, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="UpscaleAsync{TPixel}"/>.</summary>
    public static void Upscale<TPixel>(this Image<TPixel> image, ImageAiSession ai, int factor = 4, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        AiOperations.Upscale(ai.GetSession(ModelRegistry.SuperResolutionX4, cancellationToken), image, factor, cancellationToken);
    }

    // ----- Denoise -----

    /// <summary>
    /// Removes sensor / scan noise in place with the learned grayscale denoiser (DnCNN residual contract, tiled).
    /// The result is grayscale (the network operates on luminance).
    /// </summary>
    public static async Task DenoiseAIAsync<TPixel>(this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        InferenceSession session = await ai.GetSessionAsync(ModelRegistry.DenoiseGray, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => AiOperations.Denoise(session, image, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="DenoiseAIAsync{TPixel}"/>.</summary>
    public static void DenoiseAI<TPixel>(this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        AiOperations.Denoise(ai.GetSession(ModelRegistry.DenoiseGray, cancellationToken), image, cancellationToken);
    }

    // ----- Saliency / background removal -----

    /// <summary>Returns the salient-object mask of the root frame at the image size (U2-Net-p; 255 = foreground). The caller owns the result.</summary>
    public static async Task<Image<L8>> GetSaliencyMaskAsync<TPixel>(this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        InferenceSession session = await ai.GetSessionAsync(ModelRegistry.Saliency, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => AiOperations.SaliencyMask(session, image), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="GetSaliencyMaskAsync{TPixel}"/>.</summary>
    public static Image<L8> GetSaliencyMask<TPixel>(this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        return AiOperations.SaliencyMask(ai.GetSession(ModelRegistry.Saliency, cancellationToken), image);
    }

    /// <summary>
    /// Removes the background in place: the saliency mask becomes the alpha channel on formats that have one, or the
    /// background is blended to white (or <see cref="BackgroundRemovalOptions.BackgroundColor"/>) otherwise; optionally
    /// crops to the foreground.
    /// </summary>
    public static async Task RemoveBackgroundAsync<TPixel>(
        this Image<TPixel> image, ImageAiSession ai, BackgroundRemovalOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        InferenceSession session = await ai.GetSessionAsync(ModelRegistry.Saliency, cancellationToken).ConfigureAwait(false);
        BackgroundRemovalOptions effective = options ?? BackgroundRemovalOptions.Default;
        await Task.Run(() => AiOperations.RemoveBackground(session, image, effective), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="RemoveBackgroundAsync{TPixel}"/>.</summary>
    public static void RemoveBackground<TPixel>(
        this Image<TPixel> image, ImageAiSession ai, BackgroundRemovalOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        AiOperations.RemoveBackground(ai.GetSession(ModelRegistry.Saliency, cancellationToken), image, options ?? BackgroundRemovalOptions.Default);
    }

    // ----- Binarisation -----

    /// <summary>Binarises in place with a learned per-pixel threshold map (SauvolaNet contract): white where luminance is at or above the threshold.</summary>
    public static async Task BinarizeAIAsync<TPixel>(this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        InferenceSession session = await ai.GetSessionAsync(ModelRegistry.Binarization, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => AiOperations.Binarize(session, image, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="BinarizeAIAsync{TPixel}"/>.</summary>
    public static void BinarizeAI<TPixel>(this Image<TPixel> image, ImageAiSession ai, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Check(image, ai);
        AiOperations.Binarize(ai.GetSession(ModelRegistry.Binarization, cancellationToken), image, cancellationToken);
    }

    private static void Check<TPixel>(Image<TPixel> image, ImageAiSession ai)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(ai);
    }
}
