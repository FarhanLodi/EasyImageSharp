using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Palette analysis of an image (root frame, no mutation).</summary>
public static partial class ProcessingExtensions
{
    /// <summary>
    /// Finds up to <paramref name="count"/> dominant colours of the root frame with their population shares, the
    /// most common first. Pixels are point-sampled on a bounded grid, near-transparent ones are skipped, and the rest
    /// are clustered by weighted k-means in the perceptual OKLab space (seeded deterministically, so the same image
    /// always gives the same result). Fewer colours are returned when the image has fewer distinct colours, and an
    /// empty list when every pixel is transparent. Colours are compared at 8 bits per channel.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is outside 1-256.</exception>
    public static IReadOnlyList<DominantColor> GetDominantColors<TPixel>(this Image<TPixel> image, int count, DominantColorOptions? options = null)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        image.EnsureNotDisposed();
        if (count is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "The colour count must be between 1 and 256.");
        }

        return DominantColorOps.Find(image.Frames.RootFrame, count, options ?? new DominantColorOptions());
    }
}
