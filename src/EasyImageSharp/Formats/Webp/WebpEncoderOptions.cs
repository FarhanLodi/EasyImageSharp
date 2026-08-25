namespace EasyImageSharp.Formats.Webp;

/// <summary>Which WebP bitstream the encoder writes for the image data.</summary>
public enum WebpFileFormat
{
    /// <summary>
    /// Let the encoder decide: images that reproduce well as graphics (at most 256 distinct colours) and every
    /// image in a build without a lossy encoder are written losslessly; anything else is written as lossy VP8.
    /// </summary>
    Auto = 0,

    /// <summary>Always write the VP8L lossless bitstream, which reproduces every pixel exactly.</summary>
    Lossless = 1,

    /// <summary>Always write the lossy VP8 bitstream (with an ALPH chunk when the image has transparency).</summary>
    Lossy = 2,
}

/// <summary>How the alpha plane of a lossy WebP frame is stored in its ALPH chunk.</summary>
public enum WebpAlphaCompression
{
    /// <summary>Store the filtered alpha plane uncompressed (compression method 0).</summary>
    None = 0,

    /// <summary>
    /// Store the filtered alpha plane as the green channel of a VP8L image (compression method 1). The encoder
    /// falls back to <see cref="None"/> for the rare plane that compresses to more bytes than it occupies raw.
    /// </summary>
    Lossless = 1,
}

/// <summary>What the encoder does with the colour channels of fully transparent pixels.</summary>
public enum WebpTransparentColorMode
{
    /// <summary>Keep the colour of fully transparent pixels exactly as it is.</summary>
    Preserve = 0,

    /// <summary>
    /// Replace the colour of fully transparent pixels with black. The pixels are invisible either way, and a
    /// single colour behind them compresses far better than whatever noise the image happens to carry there.
    /// </summary>
    Clear = 1,
}
