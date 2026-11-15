using EasyImageSharp.Metadata;

namespace EasyImageSharp.JpegXl;

/// <summary>JPEG XL-specific image metadata read from the codestream's basic info.</summary>
public sealed class JpegXlMetadata : IFormatMetadata
{
    /// <summary>Creates JPEG XL metadata with default values.</summary>
    public JpegXlMetadata()
    {
    }

    private JpegXlMetadata(JpegXlMetadata other)
    {
        this.BitsPerSample = other.BitsPerSample;
        this.ExponentBitsPerSample = other.ExponentBitsPerSample;
        this.IsGray = other.IsGray;
        this.HasAlpha = other.HasAlpha;
        this.AlphaPremultiplied = other.AlphaPremultiplied;
        this.UsesOriginalProfile = other.UsesOriginalProfile;
        this.HasContainer = other.HasContainer;
        this.IsAnimated = other.IsAnimated;
        this.LoopCount = other.LoopCount;
        this.TicksPerSecondNumerator = other.TicksPerSecondNumerator;
        this.TicksPerSecondDenominator = other.TicksPerSecondDenominator;
        this.IntensityTarget = other.IntensityTarget;
    }

    /// <summary>The original bit depth of the colour channels (for example 8, 10, 12, 16 or 32).</summary>
    public int BitsPerSample { get; set; }

    /// <summary>The floating-point exponent bits of the colour channels, or 0 for integer samples.</summary>
    public int ExponentBitsPerSample { get; set; }

    /// <summary>True when the image has a single (grey) colour channel.</summary>
    public bool IsGray { get; set; }

    /// <summary>True when the image has an alpha channel.</summary>
    public bool HasAlpha { get; set; }

    /// <summary>True when the stored colour is premultiplied by alpha. Decoded pixels are always un-premultiplied.</summary>
    public bool AlphaPremultiplied { get; set; }

    /// <summary>
    /// True when the samples are stored in the original colour space (typical of lossless files); false when
    /// they are stored in the perceptual XYB space (typical of lossy files).
    /// </summary>
    public bool UsesOriginalProfile { get; set; }

    /// <summary>True when the file uses the ISO-BMFF container rather than a bare codestream.</summary>
    public bool HasContainer { get; set; }

    /// <summary>True when the codestream declares an animation.</summary>
    public bool IsAnimated { get; set; }

    /// <summary>How many times an animation plays; 0 loops forever. Still images report 0.</summary>
    public int LoopCount { get; set; }

    /// <summary>The numerator of the animation tick rate (ticks per second = numerator / denominator).</summary>
    public uint TicksPerSecondNumerator { get; set; }

    /// <summary>The denominator of the animation tick rate.</summary>
    public uint TicksPerSecondDenominator { get; set; }

    /// <summary>The intended peak luminance of the content in nits (255 for SDR content).</summary>
    public float IntensityTarget { get; set; }

    /// <summary>Creates a deep copy of this metadata.</summary>
    public JpegXlMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}

/// <summary>Per-frame JPEG XL metadata for animations.</summary>
public sealed class JpegXlFrameMetadata : IFormatMetadata
{
    /// <summary>Creates frame metadata with default values.</summary>
    public JpegXlFrameMetadata()
    {
    }

    private JpegXlFrameMetadata(JpegXlFrameMetadata other)
    {
        this.Duration = other.Duration;
        this.DurationTicks = other.DurationTicks;
    }

    /// <summary>How long the frame is shown. Zero for still images.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// The exact duration in animation ticks (see <see cref="JpegXlMetadata.TicksPerSecondNumerator"/>), or 0.
    /// When every frame carries a value and the image has a tick rate, the encoder reuses both.
    /// </summary>
    public uint DurationTicks { get; set; }

    /// <summary>Creates a deep copy of this metadata.</summary>
    public JpegXlFrameMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}
