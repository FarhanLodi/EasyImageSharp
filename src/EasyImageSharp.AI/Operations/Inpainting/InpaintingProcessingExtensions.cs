using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.AI;

/// <summary>
/// Inpainting inside a <c>Mutate</c> / <c>Clone</c> pipeline:
/// <c>photo.Mutate(ctx =&gt; ctx.Inpaint(ai, mask).Upscale(ai, 2))</c>. Pipelines are synchronous, so the first call
/// may block while the model downloads; call <see cref="ImageAiSession.WarmUpAsync(IEnumerable{ModelDescriptor}, CancellationToken)"/>
/// with <see cref="ModelRegistry.Inpainting"/> beforehand to avoid that. Supported for images in the built-in pixel
/// formats (Rgba32, Rgb24, Bgra32, Bgr24, L8).
/// </summary>
public static class InpaintingProcessingExtensions
{
    /// <summary>Fills the pixels where <paramref name="mask"/> is at or above the threshold (see <see cref="InpaintingExtensions"/>).</summary>
    public static IImageProcessingContext Inpaint(this IImageProcessingContext context, ImageAiSession ai, Image<L8> mask, InpaintingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(mask);
        ProcessingContextBridge.Dispatch(context, new MaskVisitor(ai, mask, options));
        return context;
    }

    /// <summary>Fills the given rectangles (see <see cref="InpaintingExtensions"/>).</summary>
    public static IImageProcessingContext InpaintRegions(this IImageProcessingContext context, ImageAiSession ai, IEnumerable<Rectangle> regions, InpaintingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(regions);
        ProcessingContextBridge.Dispatch(context, new RegionsVisitor(ai, regions.ToArray(), options));
        return context;
    }

    private sealed class MaskVisitor(ImageAiSession ai, Image<L8> mask, InpaintingOptions? options) : IImageVisitor
    {
        public void Visit<TPixel>(Image<TPixel> image)
            where TPixel : unmanaged, IPixel<TPixel>
            => image.Inpaint(ai, mask, options);
    }

    private sealed class RegionsVisitor(ImageAiSession ai, Rectangle[] regions, InpaintingOptions? options) : IImageVisitor
    {
        public void Visit<TPixel>(Image<TPixel> image)
            where TPixel : unmanaged, IPixel<TPixel>
            => image.InpaintRegions(ai, regions, options);
    }
}
