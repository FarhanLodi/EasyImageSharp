using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.AI;

/// <summary>
/// Face redaction inside a <c>Mutate</c> / <c>Clone</c> pipeline:
/// <c>photo.Mutate(ctx =&gt; ctx.RedactFaces(ai).Resize(1600, 0))</c>. Pipelines are synchronous, so the
/// first call may block while the model downloads; warm the session up beforehand to avoid that. Supported for images
/// in the built-in pixel formats (Rgba32, Rgb24, Bgra32, Bgr24, L8).
/// </summary>
public static class FaceProcessingExtensions
{
    /// <summary>Detects and blurs every face (see <see cref="FaceImageExtensions.RedactFaces{TPixel}(Image{TPixel}, ImageAiSession, FaceRedactionOptions?, CancellationToken)"/>).</summary>
    public static IImageProcessingContext RedactFaces(this IImageProcessingContext context, ImageAiSession ai)
        => context.RedactFaces(ai, new FaceRedactionOptions());

    /// <summary>Detects and redacts every face with explicit options (style, padding, detection).</summary>
    public static IImageProcessingContext RedactFaces(this IImageProcessingContext context, ImageAiSession ai, FaceRedactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ProcessingContextBridge.Dispatch(context, new RedactFacesVisitor(ai, options));
        return context;
    }

    private sealed class RedactFacesVisitor(ImageAiSession ai, FaceRedactionOptions options) : IImageVisitor
    {
        public void Visit<TPixel>(Image<TPixel> image)
            where TPixel : unmanaged, IPixel<TPixel>
            => image.RedactFaces(ai, options);
    }
}
