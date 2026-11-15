using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Convenience overloads for content-aware cropping.</summary>
public static partial class ProcessingExtensions
{
    /// <summary>Smart crop to <paramref name="width"/> x <paramref name="height"/> with the default <see cref="SmartCropOptions"/>.</summary>
    public static IImageProcessingContext SmartCrop(this IImageProcessingContext context, int width, int height)
        => context.SmartCrop(width, height, new SmartCropOptions());

    /// <summary>
    /// Finds the best crop of the root frame with the aspect ratio <paramref name="width"/> : <paramref name="height"/>
    /// without changing the image; see <see cref="IImageProcessingContext.FindSmartCrop(int, int, SmartCropOptions)"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    public static Rectangle FindSmartCrop<TPixel>(this Image<TPixel> image, int width, int height, SmartCropOptions? options = null)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        image.EnsureNotDisposed();
        return SmartCropOps.Find(image.Frames.RootFrame, width, height, options ?? new SmartCropOptions());
    }
}
