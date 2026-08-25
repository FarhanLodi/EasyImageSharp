namespace EasyImageSharp.Formats.Png;

/// <summary>How the encoder treats the colour channels of fully transparent pixels.</summary>
public enum PngTransparentColorMode
{
    /// <summary>Colour values of fully transparent pixels are written unchanged.</summary>
    Preserve,

    /// <summary>
    /// Fully transparent pixels are written as transparent black, which compresses better; their hidden colour is
    /// lost. Only affects colour types with an alpha channel.
    /// </summary>
    Clear,
}
