namespace EasyImageSharp.LittleCms;

/// <summary>ICC standard rendering intents.</summary>
public enum IccRenderingIntent
{
    /// <summary>Maps source colors into the destination gamut using profile perceptual tables.</summary>
    Perceptual = 0,
    /// <summary>Preserves in-gamut colors relative to source and destination white.</summary>
    RelativeColorimetric = 1,
    /// <summary>Uses profile saturation tables.</summary>
    Saturation = 2,
    /// <summary>Preserves absolute colorimetry including the source medium white.</summary>
    AbsoluteColorimetric = 3,
}

/// <summary>Supported interleaved floating-point channel models.</summary>
public enum IccColorModel
{
    /// <summary>One gray channel in 0-1.</summary>
    Gray,
    /// <summary>Red, green, blue in 0-1.</summary>
    Rgb,
    /// <summary>Cyan, magenta, yellow, black in percentages 0-100.</summary>
    Cmyk,
    /// <summary>CIELAB: L* in 0-100; a*, b* in their natural signed units.</summary>
    Lab,
    /// <summary>CIEXYZ relative to Y=1.</summary>
    Xyz,
}

/// <summary>Options applied when compiling an ICC transform.</summary>
public sealed class IccTransformOptions
{
    /// <summary>Gets the requested rendering intent. Defaults to relative colorimetric.</summary>
    public IccRenderingIntent Intent { get; init; } = IccRenderingIntent.RelativeColorimetric;
    /// <summary>Gets whether source and destination black points are compensated. Defaults to false.</summary>
    public bool BlackPointCompensation { get; init; }
}
