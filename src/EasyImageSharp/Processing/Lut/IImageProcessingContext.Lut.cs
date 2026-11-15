namespace EasyImageSharp.Processing;

/// <summary>Colour lookup tables (<c>.cube</c> files and Hald CLUTs).</summary>
public partial interface IImageProcessingContext
{
    /// <summary>
    /// Maps every pixel's colour through <paramref name="lut"/> (its 1D shaper, then its 3D lattice sampled with
    /// <paramref name="interpolation"/>) and blends the result with the original by <paramref name="amount"/>
    /// (0 keeps the image, 1 applies the LUT fully). Alpha is kept. Formats wider than 8 bits per channel are
    /// processed at their full precision; 8-bit formats are mapped in single precision and rounded once.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="lut"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is not in 0-1 or <paramref name="interpolation"/> is unknown.</exception>
    /// <exception cref="NotSupportedException">The processing context does not implement colour lookup tables.</exception>
    IImageProcessingContext ApplyLut(ColorLut lut, LutInterpolation interpolation, float amount)
        => throw new NotSupportedException("This processing context does not support colour lookup tables.");
}
