using EasyImageSharp.Metadata;

namespace EasyImageSharp.Avif;

/// <summary>The YUV chroma layout of an AVIF image.</summary>
public enum AvifChromaSubsampling
{
    /// <summary>Full-resolution chroma (4:4:4). Required for lossless RGB round trips.</summary>
    Yuv444 = 1,

    /// <summary>Chroma halved horizontally (4:2:2).</summary>
    Yuv422 = 2,

    /// <summary>Chroma halved in both directions (4:2:0); the smallest files for photographic content.</summary>
    Yuv420 = 3,

    /// <summary>Luma only (4:0:0, monochrome).</summary>
    Yuv400 = 4,
}

/// <summary>
/// AVIF-specific image metadata read from the container and the AV1 sequence header. The CICP values are
/// reported as stored (ITU-T H.273 code points); the decoder does not apply them beyond the YUV to RGB matrix.
/// </summary>
public sealed class AvifMetadata : IFormatMetadata
{
    /// <summary>Creates AVIF metadata with default values.</summary>
    public AvifMetadata()
    {
    }

    private AvifMetadata(AvifMetadata other)
    {
        this.BitDepth = other.BitDepth;
        this.ChromaSubsampling = other.ChromaSubsampling;
        this.FullRange = other.FullRange;
        this.HasAlpha = other.HasAlpha;
        this.AlphaPremultiplied = other.AlphaPremultiplied;
        this.ColorPrimaries = other.ColorPrimaries;
        this.TransferCharacteristics = other.TransferCharacteristics;
        this.MatrixCoefficients = other.MatrixCoefficients;
        this.IsAnimated = other.IsAnimated;
        this.RepetitionCount = other.RepetitionCount;
        this.Timescale = other.Timescale;
        this.RotationDegrees = other.RotationDegrees;
        this.MirrorAxis = other.MirrorAxis;
    }

    /// <summary>The coded bit depth of the image: 8, 10 or 12.</summary>
    public int BitDepth { get; set; }

    /// <summary>The coded chroma layout, or <see langword="null"/> for an alpha-only image.</summary>
    public AvifChromaSubsampling? ChromaSubsampling { get; set; }

    /// <summary>True when the YUV samples use the full range; false for limited ("studio") range.</summary>
    public bool FullRange { get; set; }

    /// <summary>True when the file carries an alpha auxiliary image.</summary>
    public bool HasAlpha { get; set; }

    /// <summary>True when the stored colour is premultiplied by alpha. Decoded pixels are always un-premultiplied.</summary>
    public bool AlphaPremultiplied { get; set; }

    /// <summary>The H.273 colour primaries code point (1 = BT.709/sRGB, 9 = BT.2020, 12 = Display P3, 2 = unspecified).</summary>
    public int ColorPrimaries { get; set; }

    /// <summary>The H.273 transfer characteristics code point (13 = sRGB, 16 = PQ, 18 = HLG, 2 = unspecified).</summary>
    public int TransferCharacteristics { get; set; }

    /// <summary>The H.273 matrix coefficients code point (0 = identity, 1 = BT.709, 6 = BT.601, 9 = BT.2020).</summary>
    public int MatrixCoefficients { get; set; }

    /// <summary>True when the file is an image sequence with more than one frame.</summary>
    public bool IsAnimated { get; set; }

    /// <summary>
    /// How many times an image sequence repeats after its first playback: -1 means forever, -2 means the file
    /// does not say. Still images report 0. The encoder writes this value for multi-frame images.
    /// </summary>
    public int RepetitionCount { get; set; }

    /// <summary>The media timescale of an image sequence in ticks per second, or 0 for still images.</summary>
    public ulong Timescale { get; set; }

    /// <summary>
    /// The anti-clockwise rotation from the 'irot' property (0, 90, 180 or 270). It is metadata only: the
    /// decoder returns pixels in coded orientation, exactly as libavif delivers them.
    /// </summary>
    public int RotationDegrees { get; set; }

    /// <summary>
    /// The mirror axis from the 'imir' property: 0 flips top and bottom, 1 flips left and right, or
    /// <see langword="null"/> when absent. Metadata only, like <see cref="RotationDegrees"/>.
    /// </summary>
    public int? MirrorAxis { get; set; }

    /// <summary>Creates a deep copy of this metadata.</summary>
    public AvifMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}

/// <summary>Per-frame AVIF metadata for image sequences.</summary>
public sealed class AvifFrameMetadata : IFormatMetadata
{
    /// <summary>Creates frame metadata with default values.</summary>
    public AvifFrameMetadata()
    {
    }

    private AvifFrameMetadata(AvifFrameMetadata other)
    {
        this.Duration = other.Duration;
        this.DurationInTimescales = other.DurationInTimescales;
    }

    /// <summary>How long the frame is shown. Zero for still images.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// The exact duration in units of <see cref="AvifMetadata.Timescale"/>, or 0 when unknown. When every frame
    /// carries a value and the image has a timescale, the encoder reuses both instead of rounding to milliseconds.
    /// </summary>
    public ulong DurationInTimescales { get; set; }

    /// <summary>Creates a deep copy of this metadata.</summary>
    public AvifFrameMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}
