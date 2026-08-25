using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.AI;

/// <summary>
/// AI operations inside a <c>Mutate</c> / <c>Clone</c> pipeline, so they can be chained with the classical core
/// operators: <c>page.Mutate(ctx =&gt; ctx.RemoveBackground(ai).AutoOrient(ai).DewarpDocument(ai).Deskew().DenoiseAI(ai).SauvolaThreshold())</c>.
/// Pipelines are synchronous, so the first call of each operation may block while its model downloads; call
/// <see cref="ImageAiSession.WarmUpAsync(CancellationToken)"/> beforehand to avoid that. Supported for images in
/// the built-in pixel formats (Rgba32, Rgb24, Bgra32, Bgr24, L8).
/// </summary>
public static class AiProcessingExtensions
{
    /// <summary>Detects the page orientation and losslessly rotates the image upright.</summary>
    public static IImageProcessingContext AutoOrient(this IImageProcessingContext context, ImageAiSession ai)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ai);
        ProcessingContextBridge.Dispatch(context, new AutoOrientVisitor(ai));
        return context;
    }

    /// <summary>Flattens a curled / keystoned page (UVDoc); the size is unchanged.</summary>
    public static IImageProcessingContext DewarpDocument(this IImageProcessingContext context, ImageAiSession ai)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ai);
        ProcessingContextBridge.Dispatch(context, new DewarpVisitor(ai));
        return context;
    }

    /// <summary>Upscales by <paramref name="factor"/> with the super-resolution network (see <see cref="AiImageExtensions.UpscaleAsync{TPixel}"/>).</summary>
    public static IImageProcessingContext Upscale(this IImageProcessingContext context, ImageAiSession ai, int factor = 4)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ai);
        ProcessingContextBridge.Dispatch(context, new UpscaleVisitor(ai, factor));
        return context;
    }

    /// <summary>Removes noise with the learned grayscale denoiser; the result is grayscale.</summary>
    public static IImageProcessingContext DenoiseAI(this IImageProcessingContext context, ImageAiSession ai)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ai);
        ProcessingContextBridge.Dispatch(context, new DenoiseVisitor(ai));
        return context;
    }

    /// <summary>Removes the background (alpha on alpha formats, white otherwise).</summary>
    public static IImageProcessingContext RemoveBackground(this IImageProcessingContext context, ImageAiSession ai)
        => context.RemoveBackground(ai, BackgroundRemovalOptions.Default);

    /// <summary>Removes the background with explicit options (background colour, crop to foreground).</summary>
    public static IImageProcessingContext RemoveBackground(this IImageProcessingContext context, ImageAiSession ai, BackgroundRemovalOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(options);
        ProcessingContextBridge.Dispatch(context, new RemoveBackgroundVisitor(ai, options));
        return context;
    }

    /// <summary>Binarises with a learned per-pixel threshold map.</summary>
    public static IImageProcessingContext BinarizeAI(this IImageProcessingContext context, ImageAiSession ai)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ai);
        ProcessingContextBridge.Dispatch(context, new BinarizeVisitor(ai));
        return context;
    }

    // ----- Visitors: bind the runtime pixel type and forward to the Image<TPixel> operations -----

    private sealed class AutoOrientVisitor(ImageAiSession ai) : IImageVisitor
    {
        public void Visit<TPixel>(Image<TPixel> image)
            where TPixel : unmanaged, IPixel<TPixel>
            => image.AutoOrient(ai);
    }

    private sealed class DewarpVisitor(ImageAiSession ai) : IImageVisitor
    {
        public void Visit<TPixel>(Image<TPixel> image)
            where TPixel : unmanaged, IPixel<TPixel>
            => image.DewarpDocument(ai);
    }

    private sealed class UpscaleVisitor(ImageAiSession ai, int factor) : IImageVisitor
    {
        public void Visit<TPixel>(Image<TPixel> image)
            where TPixel : unmanaged, IPixel<TPixel>
            => image.Upscale(ai, factor);
    }

    private sealed class DenoiseVisitor(ImageAiSession ai) : IImageVisitor
    {
        public void Visit<TPixel>(Image<TPixel> image)
            where TPixel : unmanaged, IPixel<TPixel>
            => image.DenoiseAI(ai);
    }

    private sealed class RemoveBackgroundVisitor(ImageAiSession ai, BackgroundRemovalOptions options) : IImageVisitor
    {
        public void Visit<TPixel>(Image<TPixel> image)
            where TPixel : unmanaged, IPixel<TPixel>
            => image.RemoveBackground(ai, options);
    }

    private sealed class BinarizeVisitor(ImageAiSession ai) : IImageVisitor
    {
        public void Visit<TPixel>(Image<TPixel> image)
            where TPixel : unmanaged, IPixel<TPixel>
            => image.BinarizeAI(ai);
    }
}
