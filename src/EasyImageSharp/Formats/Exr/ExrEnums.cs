namespace EasyImageSharp.Formats.Exr;

/// <summary>
/// The OpenEXR compression methods, with the values the format stores in the <c>compression</c> attribute.
/// <see cref="ExrDecoder"/> reads <see cref="None"/>, <see cref="Rle"/>, <see cref="Zips"/>, <see cref="Zip"/>,
/// <see cref="Piz"/> and <see cref="Pxr24"/>; the other methods are reported by <c>Identify</c> but decoding them
/// throws <see cref="NotSupportedException"/>.
/// </summary>
public enum ExrCompression
{
    /// <summary>Uncompressed.</summary>
    None = 0,

    /// <summary>Byte-wise run-length encoding of one scanline per chunk, after a byte predictor.</summary>
    Rle = 1,

    /// <summary>zlib deflate of one scanline per chunk, after a byte predictor.</summary>
    Zips = 2,

    /// <summary>zlib deflate of 16 scanlines per chunk, after a byte predictor.</summary>
    Zip = 3,

    /// <summary>Haar wavelet plus Huffman coding of 32 scanlines per chunk; lossless.</summary>
    Piz = 4,

    /// <summary>Float samples rounded to 24 bits, then differenced and deflated, 16 scanlines per chunk; lossy for float.</summary>
    Pxr24 = 5,

    /// <summary>Fixed-rate 4x4 block compression of half samples (not decoded).</summary>
    B44 = 6,

    /// <summary>B44 with flat-block optimisation (not decoded).</summary>
    B44a = 7,

    /// <summary>DCT-based lossy compression, 32 scanlines per chunk (not decoded).</summary>
    Dwaa = 8,

    /// <summary>DCT-based lossy compression, 256 scanlines per chunk (not decoded).</summary>
    Dwab = 9,
}

/// <summary>The sample types an OpenEXR channel can hold.</summary>
public enum ExrPixelType
{
    /// <summary>32-bit unsigned integer, decoded to its numeric value as a float.</summary>
    UInt = 0,

    /// <summary>16-bit IEEE 754 half-precision float.</summary>
    Half = 1,

    /// <summary>32-bit IEEE 754 single-precision float.</summary>
    Float = 2,
}

/// <summary>The order in which an OpenEXR file stores its chunks.</summary>
public enum ExrLineOrder
{
    /// <summary>Chunks stored from the top of the data window down.</summary>
    IncreasingY = 0,

    /// <summary>Chunks stored from the bottom of the data window up.</summary>
    DecreasingY = 1,

    /// <summary>Tiles stored in no particular order.</summary>
    RandomY = 2,
}

/// <summary>The resolution levels a tiled OpenEXR file carries. <see cref="ExrDecoder"/> always decodes the full-resolution level.</summary>
public enum ExrLevelMode
{
    /// <summary>A single full-resolution level.</summary>
    OneLevel = 0,

    /// <summary>Full resolution plus successively halved levels in both directions.</summary>
    MipmapLevels = 1,

    /// <summary>Every combination of independently halved widths and heights.</summary>
    RipmapLevels = 2,
}

/// <summary>Which channels <see cref="ExrEncoder"/> writes.</summary>
public enum ExrChannelLayout
{
    /// <summary>Red, green and blue (<c>R</c>, <c>G</c>, <c>B</c>).</summary>
    Rgb = 0,

    /// <summary>Red, green, blue and alpha (<c>R</c>, <c>G</c>, <c>B</c>, <c>A</c>).</summary>
    Rgba = 1,

    /// <summary>Luminance only (<c>Y</c>), the Rec. 709 weighted sum of linear red, green and blue.</summary>
    Luminance = 2,

    /// <summary>Luminance and alpha (<c>Y</c>, <c>A</c>).</summary>
    LuminanceAlpha = 3,
}
