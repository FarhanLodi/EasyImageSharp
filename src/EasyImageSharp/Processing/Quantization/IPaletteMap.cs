using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing.Quantization;

/// <summary>
/// Maps colours to the entries of a fixed palette. Quantizers hand an implementation to
/// <see cref="Dithering.IDither"/> so the dither can resolve perturbed colours; implementations must be safe
/// for concurrent use from several threads.
/// </summary>
public interface IPaletteMap
{
    /// <summary>The palette being matched against.</summary>
    ReadOnlySpan<Rgba32> Palette { get; }

    /// <summary>Returns the palette index chosen for <paramref name="color"/> and the palette colour at that index.</summary>
    int GetPaletteIndex(Rgba32 color, out Rgba32 match);
}
