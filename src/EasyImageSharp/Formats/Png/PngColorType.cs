namespace EasyImageSharp.Formats.Png;

/// <summary>The PNG colour types (IHDR colour type field).</summary>
public enum PngColorType : byte
{
    /// <summary>One luminance sample per pixel; bit depths 1, 2, 4, 8 or 16.</summary>
    Grayscale = 0,

    /// <summary>Red, green and blue samples; bit depths 8 or 16.</summary>
    Rgb = 2,

    /// <summary>One palette index per pixel with a PLTE (and optional tRNS) chunk; bit depths 1, 2, 4 or 8.</summary>
    Palette = 3,

    /// <summary>Luminance plus alpha; bit depths 8 or 16.</summary>
    GrayscaleWithAlpha = 4,

    /// <summary>Red, green, blue plus alpha; bit depths 8 or 16.</summary>
    RgbWithAlpha = 6,
}
