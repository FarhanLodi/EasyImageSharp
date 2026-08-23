namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// The colour model and chroma layout <see cref="JpegEncoder"/> writes. YCbCr variants convert RGB with the
/// JFIF (ITU-R BT.601 full-range) matrix; the ratio names follow the usual J:a:b notation and describe how many
/// chroma samples are kept per 4x2 block of luma samples (subsampled chroma is produced by box-averaging).
/// </summary>
public enum JpegEncodingColor
{
    /// <summary>YCbCr without chroma subsampling (sampling factors 1x1 for every component). The default for colour images.</summary>
    YCbCrRatio444,

    /// <summary>YCbCr with chroma halved horizontally (luma 2x1, chroma 1x1).</summary>
    YCbCrRatio422,

    /// <summary>YCbCr with chroma halved horizontally and vertically (luma 2x2, chroma 1x1); the usual "photo" layout.</summary>
    YCbCrRatio420,

    /// <summary>YCbCr with chroma quartered horizontally (luma 4x1, chroma 1x1).</summary>
    YCbCrRatio411,

    /// <summary>YCbCr with chroma quartered horizontally and halved vertically (luma 4x2, chroma 1x1).</summary>
    YCbCrRatio410,

    /// <summary>A single luminance component (grayscale). The default for <see cref="PixelFormats.L8"/> images.</summary>
    Luminance,

    /// <summary>Three RGB components stored without colour conversion, flagged by an Adobe APP14 segment (transform 0).</summary>
    Rgb,

    /// <summary>
    /// Four CMYK components stored inverted (255 minus ink coverage, the Adobe convention) with an Adobe APP14
    /// segment (transform 0). RGB sources are separated with K = 255 - max(R, G, B).
    /// </summary>
    Cmyk,

    /// <summary>
    /// Four components: the CMY inks YCbCr-transformed plus K, with an Adobe APP14 segment (transform 2). No chroma subsampling.
    /// </summary>
    Ycck,
}
