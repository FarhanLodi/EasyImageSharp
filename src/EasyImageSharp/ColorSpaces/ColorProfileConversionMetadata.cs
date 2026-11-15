using EasyImageSharp.Metadata;

namespace EasyImageSharp.ColorSpaces;

/// <summary>
/// Records what a conversion to sRGB did with an image's embedded ICC profile. It is attached to
/// <see cref="ImageMetadata"/> (a summary over every frame) and to each <see cref="ImageFrameMetadata"/>
/// whenever <see cref="ColorProfileHandling.ConvertToSrgb"/> is requested at load time or the
/// <c>ConvertToSrgb</c> processing operation runs, and it is absent otherwise. Read it with
/// <c>image.Metadata.TryGetFormatMetadata&lt;ColorProfileConversionMetadata&gt;(out var conversion)</c>.
/// </summary>
/// <remarks>
/// The image-level summary is the least successful frame outcome: <see cref="ColorProfileConversionStatus.Invalid"/>
/// or <see cref="ColorProfileConversionStatus.Unsupported"/> when any frame was left unconverted, otherwise
/// <see cref="ColorProfileConversionStatus.Converted"/> when any frame was converted, otherwise
/// <see cref="ColorProfileConversionStatus.AlreadySrgb"/> or <see cref="ColorProfileConversionStatus.NoProfile"/>.
/// The container is deep-copied by <c>Clone</c> and <c>CloneAs</c> like every other metadata container; encoders
/// ignore it.
/// </remarks>
public sealed class ColorProfileConversionMetadata : IFormatMetadata
{
    /// <summary>Creates an empty record with <see cref="ColorProfileConversionStatus.NoProfile"/>.</summary>
    public ColorProfileConversionMetadata()
    {
    }

    private ColorProfileConversionMetadata(ColorProfileConversionMetadata other)
    {
        this.Status = other.Status;
        this.Detail = other.Detail;
        this.SourceProfileDescription = other.SourceProfileDescription;
    }

    /// <summary>What happened to the pixels and the profile.</summary>
    public ColorProfileConversionStatus Status { get; set; }

    /// <summary>
    /// Why a profile was not converted (for <see cref="ColorProfileConversionStatus.Unsupported"/> and
    /// <see cref="ColorProfileConversionStatus.Invalid"/>), or <see langword="null"/>. Diagnostic text only; do not
    /// parse it.
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>The description tag of the profile that was converted or rejected, or <see langword="null"/>.</summary>
    public string? SourceProfileDescription { get; set; }

    /// <summary>Creates an independent copy of this record.</summary>
    public ColorProfileConversionMetadata DeepClone() => new(this);

    IFormatMetadata IDeepCloneable<IFormatMetadata>.DeepClone() => this.DeepClone();

    /// <inheritdoc/>
    public override string ToString()
    {
        // Spelled out rather than Enum.ToString, which formats through enum metadata a trimmed app may not keep.
        string status = this.Status switch
        {
            ColorProfileConversionStatus.NoProfile => nameof(ColorProfileConversionStatus.NoProfile),
            ColorProfileConversionStatus.Converted => nameof(ColorProfileConversionStatus.Converted),
            ColorProfileConversionStatus.AlreadySrgb => nameof(ColorProfileConversionStatus.AlreadySrgb),
            ColorProfileConversionStatus.Unsupported => nameof(ColorProfileConversionStatus.Unsupported),
            ColorProfileConversionStatus.Invalid => nameof(ColorProfileConversionStatus.Invalid),
            _ => ((int)this.Status).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return this.Detail is null ? status : $"{status}: {this.Detail}";
    }
}
