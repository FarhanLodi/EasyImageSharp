using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing.Quantization;

namespace EasyImageSharp.Processing.Dithering;

/// <summary>
/// A dithering algorithm used while reducing an image to a palette: it decides, pixel by pixel, which palette
/// entry to use so that the quantization error is spread out (error diffusion) or broken up by a threshold
/// pattern (ordered dithering) instead of showing as banding.
/// </summary>
public interface IDither
{
    /// <summary>
    /// Dithers the pixels of <paramref name="frame"/> inside <paramref name="bounds"/> to the palette behind
    /// <paramref name="paletteMap"/>.
    /// </summary>
    /// <param name="frame">The source pixels. Left untouched unless <paramref name="replacePixels"/> is true.</param>
    /// <param name="bounds">The region to process; must lie inside the frame.</param>
    /// <param name="paletteMap">Resolves colours to palette entries.</param>
    /// <param name="scale">Dither strength, 0 (none) to 1 (full).</param>
    /// <param name="indices">
    /// Receives the chosen palette index of every pixel in the region, row-major (<c>bounds.Width * bounds.Height</c>
    /// bytes); pass an empty buffer when indices are not needed.
    /// </param>
    /// <param name="replacePixels">When true, every processed pixel is overwritten with its chosen palette colour.</param>
    void Apply<TPixel>(
        ImageFrame<TPixel> frame, Rectangle bounds, IPaletteMap paletteMap, float scale, Memory<byte> indices, bool replacePixels)
        where TPixel : unmanaged, IPixel<TPixel>;
}
