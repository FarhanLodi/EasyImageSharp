using System.Numerics;

namespace EasyImageSharp.Metadata;

/// <summary>How the pixels of a Radiance HDR file are encoded.</summary>
public enum HdrColorFormat
{
    /// <summary><c>32-bit_rle_rgbe</c>: an 8-bit mantissa for red, green and blue sharing one 8-bit exponent.</summary>
    Rgbe = 0,

    /// <summary><c>32-bit_rle_xyze</c>: the same packing applied to CIE XYZ instead of RGB.</summary>
    Xyze = 1,
}

/// <summary>
/// Radiance HDR (RGBE) header facts: the program identifier, the pixel encoding, the cumulative
/// <c>EXPOSURE</c>, <c>COLORCORR</c> and <c>PIXASPECT</c> values, the optional <c>PRIMARIES</c> and
/// <c>SOFTWARE</c> lines and the scanline orientation.
/// </summary>
/// <remarks>
/// Radiance header values are cumulative: every <c>EXPOSURE</c> line multiplies the previous ones, and the
/// original radiance of a pixel is its stored value divided by <see cref="Exposure"/> (and, per channel, by
/// <see cref="ColorCorrection"/>). The decoder reports these facts but leaves the pixel values as stored
/// unless <see cref="Formats.Hdr.HdrDecoder.ApplyExposure"/> is set.
/// </remarks>
public sealed class HdrMetadata : IFormatMetadata
{
    /// <summary>Creates metadata describing a plain RGBE file in the standard orientation.</summary>
    public HdrMetadata()
    {
    }

    private HdrMetadata(HdrMetadata other)
    {
        this.ProgramName = other.ProgramName;
        this.ColorFormat = other.ColorFormat;
        this.Exposure = other.Exposure;
        this.ColorCorrection = other.ColorCorrection;
        this.PixelAspectRatio = other.PixelAspectRatio;
        this.Primaries = other.Primaries is null ? null : (float[])other.Primaries.Clone();
        this.Software = other.Software;
        this.Orientation = other.Orientation;
    }

    /// <summary>The identifier after <c>#?</c> on the first line, typically <c>RADIANCE</c> or <c>RGBE</c>.</summary>
    public string ProgramName { get; set; } = "RADIANCE";

    /// <summary>The pixel encoding declared by the <c>FORMAT</c> line. Defaults to <see cref="HdrColorFormat.Rgbe"/>.</summary>
    public HdrColorFormat ColorFormat { get; set; } = HdrColorFormat.Rgbe;

    /// <summary>The product of every <c>EXPOSURE</c> value in the header, or 1 when there is none.</summary>
    public double Exposure { get; set; } = 1;

    /// <summary>The per-channel product of every <c>COLORCORR</c> line in the header, or (1, 1, 1) when there is none.</summary>
    public Vector3 ColorCorrection { get; set; } = Vector3.One;

    /// <summary>The product of every <c>PIXASPECT</c> value (pixel height over width), or 1 when there is none.</summary>
    public double PixelAspectRatio { get; set; } = 1;

    /// <summary>
    /// The eight <c>PRIMARIES</c> chromaticities (red x, y, green x, y, blue x, y, white x, y), or
    /// <see langword="null"/> when the header does not declare them. Informational only: pixels are not converted.
    /// </summary>
    public float[]? Primaries { get; set; }

    /// <summary>The <c>SOFTWARE</c> line, or <see langword="null"/>.</summary>
    public string? Software { get; set; }

    /// <summary>
    /// The axes of the resolution line without their sizes, for example <c>-Y +X</c> (the standard
    /// top-to-bottom, left-to-right order). Decoded images are always returned upright in that standard order.
    /// </summary>
    public string Orientation { get; set; } = "-Y +X";

    /// <inheritdoc/>
    public HdrMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();
}
