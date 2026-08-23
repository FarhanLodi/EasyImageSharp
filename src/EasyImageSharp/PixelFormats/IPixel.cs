using System.Numerics;

namespace EasyImageSharp.PixelFormats;

/// <summary>
/// The contract every pixel format implements. <see cref="Rgba32"/> acts as the universal 8-bit
/// interchange format between pixel types and codecs, while <see cref="Vector4"/> is the
/// interchange format that preserves the full precision of formats wider than 8 bits per component.
/// </summary>
/// <typeparam name="TSelf">The implementing pixel type.</typeparam>
public interface IPixel<TSelf>
    where TSelf : unmanaged, IPixel<TSelf>
{
    /// <summary>Creates a pixel of this format from an <see cref="Rgba32"/> value.</summary>
    /// <param name="source">The 8-bit RGBA source value.</param>
    /// <returns>The converted pixel.</returns>
    static abstract TSelf FromRgba32(Rgba32 source);

    /// <summary>Converts this pixel to its <see cref="Rgba32"/> representation.</summary>
    /// <returns>The 8-bit RGBA representation, rounded to the nearest value for wider formats.</returns>
    Rgba32 ToRgba32();

    /// <summary>
    /// Creates a pixel of this format from normalised components in the order red, green, blue, alpha.
    /// Formats backed by integer components clamp the input to the 0-1 range; floating point formats
    /// such as <see cref="RgbaVector"/> store it unchanged.
    /// </summary>
    /// <param name="source">The normalised components, alpha last.</param>
    /// <returns>The converted pixel.</returns>
    static abstract TSelf FromScaledVector4(Vector4 source);

    /// <summary>
    /// Converts this pixel to normalised components in the order red, green, blue, alpha, where 0
    /// is the minimum and 1 the maximum representable value of each component. Formats without an
    /// alpha component report an alpha of 1.
    /// </summary>
    /// <returns>The normalised components, alpha last.</returns>
    Vector4 ToScaledVector4();
}
