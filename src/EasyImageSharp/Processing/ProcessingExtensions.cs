using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Entry points and convenience overloads for the processing pipeline.</summary>
public static partial class ProcessingExtensions
{
    /// <summary>Applies the given operations to the image in place.</summary>
    public static void Mutate<TPixel>(this Image<TPixel> source, Action<IImageProcessingContext> operation)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(operation);
        source.EnsureNotDisposed();
        operation(new ImageProcessingContext<TPixel>(source));
    }

    /// <summary>Applies the given operations to a deep copy of the image and returns the copy.</summary>
    public static Image<TPixel> Clone<TPixel>(this Image<TPixel> source, Action<IImageProcessingContext> operation)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(operation);

        // Copy-on-write: the clone starts out sharing the source's pixel buffers and the context duplicates
        // them only if an operation writes in place. A size-changing operation (resize, crop, rotate) builds
        // its own buffer anyway, so the source is never copied for it.
        Image<TPixel> clone = FrameFactory.ShallowClone(source);
        var context = new ImageProcessingContext<TPixel>(clone, sharedBuffers: true);
        operation(context);
        context.EnsureOwnBuffers();
        return clone;
    }

    // ----- Convenience overloads -----

    /// <summary>Resizes to the exact target size with the default (bicubic) resampler.</summary>
    public static IImageProcessingContext Resize(this IImageProcessingContext context, int width, int height)
        => context.Resize(new ResizeOptions { Size = new Size(width, height) });

    public static IImageProcessingContext Resize(this IImageProcessingContext context, int width, int height, IResampler sampler)
        => context.Resize(new ResizeOptions { Size = new Size(width, height), Sampler = sampler });

    public static IImageProcessingContext Resize(this IImageProcessingContext context, Size size)
        => context.Resize(new ResizeOptions { Size = size });

    /// <summary>Lossless clockwise rotation in multiples of 90 degrees.</summary>
    public static IImageProcessingContext Rotate(this IImageProcessingContext context, RotateMode rotateMode)
        => rotateMode == RotateMode.None ? context : context.Rotate((float)(int)rotateMode);

    public static IImageProcessingContext RotateFlip(
        this IImageProcessingContext context, RotateMode rotateMode, FlipMode flipMode)
        => context.Rotate(rotateMode).Flip(flipMode);

    /// <summary>Centers the image on a transparent-black canvas of the given size.</summary>
    public static IImageProcessingContext Pad(this IImageProcessingContext context, int width, int height)
        => context.Pad(width, height, Rgba32.Transparent);

    /// <summary>Centers the image on a canvas of the given size filled with <paramref name="backgroundColor"/>.</summary>
    public static IImageProcessingContext Pad(this IImageProcessingContext context, int width, int height, Color backgroundColor)
        => context.Pad(width, height, backgroundColor.ToRgba32());

    /// <summary>Bradley adaptive threshold with an automatic window size and a 0.85 threshold limit.</summary>
    public static IImageProcessingContext AdaptiveThreshold(this IImageProcessingContext context)
        => context.AdaptiveThreshold(0, 0.85f);

    /// <summary>Sauvola threshold with a 25-pixel window and k = 0.2.</summary>
    public static IImageProcessingContext SauvolaThreshold(this IImageProcessingContext context)
        => context.SauvolaThreshold(25, 0.2f);

    /// <summary>Deskews searching within ±15 degrees.</summary>
    public static IImageProcessingContext Deskew(this IImageProcessingContext context)
        => context.Deskew(15f);

    /// <summary>Alpha-blends another image over this one at the top-left corner.</summary>
    public static IImageProcessingContext DrawImage(this IImageProcessingContext context, Image image, float opacity)
        => context.DrawImage(image, new Point(0, 0), opacity);
}
