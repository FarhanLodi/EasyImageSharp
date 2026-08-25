using EasyImageSharp.Processing.Dithering;
using EasyImageSharp.Processing.Quantization;

namespace EasyImageSharp.Processing;

/// <summary>Quantization and dithering operations.</summary>
public partial interface IImageProcessingContext
{
    /// <summary>
    /// Reduces every frame to the palette produced by <paramref name="quantizer"/> (dithering as its options say),
    /// replacing each pixel with its palette colour. The pixel format is unchanged.
    /// </summary>
    IImageProcessingContext Quantize(IQuantizer quantizer);

    /// <summary>
    /// Dithers every frame to <paramref name="palette"/>: each pixel is replaced by a palette colour chosen by
    /// <paramref name="dither"/>, with the dither strength scaled by <paramref name="ditherScale"/> (0-1).
    /// </summary>
    IImageProcessingContext Dither(IDither dither, float ditherScale, ReadOnlyMemory<Color> palette);

    /// <summary>
    /// Dithers every frame to two colours by luminance: pixels resolve to <paramref name="upperColor"/> when
    /// their (dither-adjusted) luminance is at least 50% and to <paramref name="lowerColor"/> otherwise.
    /// </summary>
    IImageProcessingContext BinaryDither(IDither dither, Color upperColor, Color lowerColor);
}
