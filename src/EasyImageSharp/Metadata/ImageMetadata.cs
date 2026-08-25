using System.Diagnostics.CodeAnalysis;
using EasyImageSharp.Formats;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Xmp;

namespace EasyImageSharp.Metadata;

/// <summary>
/// Image-level metadata: physical resolution, EXIF/ICC/XMP profiles, the format the image was decoded from,
/// and format-specific containers (<see cref="GetJpegMetadata"/>, <see cref="GetPngMetadata"/>, ...). Decoders
/// populate it; encoders write what they can represent; <c>Clone</c>/<c>CloneAs</c> deep-copy it.
/// </summary>
public sealed class ImageMetadata : IDeepCloneable<ImageMetadata>
{
    /// <summary>The default horizontal resolution (96 pixels per inch).</summary>
    public const double DefaultHorizontalResolution = 96;

    /// <summary>The default vertical resolution (96 pixels per inch).</summary>
    public const double DefaultVerticalResolution = 96;

    private readonly Dictionary<Type, IFormatMetadata> formatMetadata = new();
    private double horizontalResolution = DefaultHorizontalResolution;
    private double verticalResolution = DefaultVerticalResolution;

    /// <summary>Creates metadata with default values (96 DPI, no profiles).</summary>
    public ImageMetadata()
    {
    }

    private ImageMetadata(ImageMetadata other)
    {
        this.horizontalResolution = other.horizontalResolution;
        this.verticalResolution = other.verticalResolution;
        this.ResolutionUnits = other.ResolutionUnits;
        this.ExifProfile = other.ExifProfile?.DeepClone();
        this.IccProfile = other.IccProfile?.DeepClone();
        this.XmpProfile = other.XmpProfile?.DeepClone();
        this.DecodedImageFormat = other.DecodedImageFormat;
        foreach (KeyValuePair<Type, IFormatMetadata> pair in other.formatMetadata)
        {
            this.formatMetadata[pair.Key] = pair.Value.DeepClone();
        }
    }

    /// <summary>The horizontal resolution in <see cref="ResolutionUnits"/>. Must be positive; defaults to 96.</summary>
    public double HorizontalResolution
    {
        get => this.horizontalResolution;
        set
        {
            if (!(value > 0) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Resolution must be a positive, finite number.");
            }

            this.horizontalResolution = value;
        }
    }

    /// <summary>The vertical resolution in <see cref="ResolutionUnits"/>. Must be positive; defaults to 96.</summary>
    public double VerticalResolution
    {
        get => this.verticalResolution;
        set
        {
            if (!(value > 0) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Resolution must be a positive, finite number.");
            }

            this.verticalResolution = value;
        }
    }

    /// <summary>The unit of the resolution values. Defaults to <see cref="PixelResolutionUnit.PixelsPerInch"/>.</summary>
    public PixelResolutionUnit ResolutionUnits { get; set; } = PixelResolutionUnit.PixelsPerInch;

    /// <summary>The EXIF profile, or <see langword="null"/>.</summary>
    public ExifProfile? ExifProfile { get; set; }

    /// <summary>The embedded ICC colour profile, or <see langword="null"/>.</summary>
    public IccProfile? IccProfile { get; set; }

    /// <summary>The embedded XMP packet, or <see langword="null"/>.</summary>
    public XmpProfile? XmpProfile { get; set; }

    /// <summary>The format the image was decoded from, or <see langword="null"/> for images created in memory.</summary>
    public ImageFormat? DecodedImageFormat { get; internal set; }

    /// <summary>Returns the horizontal resolution converted to <paramref name="unit"/> (aspect-ratio values pass through unchanged).</summary>
    public double GetHorizontalResolution(PixelResolutionUnit unit)
        => ResolutionConverter.Convert(this.horizontalResolution, this.ResolutionUnits, unit);

    /// <summary>Returns the vertical resolution converted to <paramref name="unit"/> (aspect-ratio values pass through unchanged).</summary>
    public double GetVerticalResolution(PixelResolutionUnit unit)
        => ResolutionConverter.Convert(this.verticalResolution, this.ResolutionUnits, unit);

    /// <summary>Sets both resolutions and their unit at once.</summary>
    public void SetResolution(double horizontal, double vertical, PixelResolutionUnit unit)
    {
        this.HorizontalResolution = horizontal;
        this.VerticalResolution = vertical;
        this.ResolutionUnits = unit;
    }

    /// <summary>Gets the format-specific metadata of type <typeparamref name="T"/>, creating a default instance on first access.</summary>
    public T GetFormatMetadata<T>()
        where T : class, IFormatMetadata, new()
    {
        if (this.formatMetadata.TryGetValue(typeof(T), out IFormatMetadata? existing))
        {
            return (T)existing;
        }

        var created = new T();
        this.formatMetadata[typeof(T)] = created;
        return created;
    }

    /// <summary>Gets the format-specific metadata of type <typeparamref name="T"/> if it has been set.</summary>
    public bool TryGetFormatMetadata<T>([NotNullWhen(true)] out T? metadata)
        where T : class, IFormatMetadata
    {
        if (this.formatMetadata.TryGetValue(typeof(T), out IFormatMetadata? existing))
        {
            metadata = (T)existing;
            return true;
        }

        metadata = null;
        return false;
    }

    /// <summary>Stores (replacing) a format-specific metadata container.</summary>
    public void SetFormatMetadata<T>(T metadata)
        where T : class, IFormatMetadata
    {
        ArgumentNullException.ThrowIfNull(metadata);
        this.formatMetadata[typeof(T)] = metadata;
    }

    /// <summary>JPEG-specific metadata (created on first access).</summary>
    public JpegMetadata GetJpegMetadata() => this.GetFormatMetadata<JpegMetadata>();

    /// <summary>PNG-specific metadata (created on first access).</summary>
    public PngMetadata GetPngMetadata() => this.GetFormatMetadata<PngMetadata>();

    /// <summary>TIFF-specific metadata (created on first access).</summary>
    public TiffMetadata GetTiffMetadata() => this.GetFormatMetadata<TiffMetadata>();

    /// <summary>BMP-specific metadata (created on first access).</summary>
    public BmpMetadata GetBmpMetadata() => this.GetFormatMetadata<BmpMetadata>();

    /// <summary>GIF-specific metadata (created on first access).</summary>
    public GifMetadata GetGifMetadata() => this.GetFormatMetadata<GifMetadata>();

    public ImageMetadata DeepClone() => new(this);

    /// <summary>
    /// Copies the resolution from an EXIF profile's XResolution/YResolution/ResolutionUnit tags when they carry
    /// usable values (used by decoders: EXIF overrides container-level density such as JFIF or pHYs).
    /// </summary>
    internal void ApplyExifResolution(ExifProfile profile)
    {
        if (!profile.TryGetValue(ExifTag.XResolution, out IExifValue<Rational>? x)
            || !profile.TryGetValue(ExifTag.YResolution, out IExifValue<Rational>? y))
        {
            return;
        }

        double horizontal = x.Value.ToDouble();
        double vertical = y.Value.ToDouble();
        if (!(horizontal > 0) || !(vertical > 0) || double.IsInfinity(horizontal) || double.IsInfinity(vertical))
        {
            return;
        }

        ushort unit = profile.TryGetValue(ExifTag.ResolutionUnit, out IExifValue<ushort>? u) ? u.Value : (ushort)2;
        this.ResolutionUnits = unit switch
        {
            3 => PixelResolutionUnit.PixelsPerCentimeter,
            1 => PixelResolutionUnit.AspectRatio,
            _ => PixelResolutionUnit.PixelsPerInch,
        };
        this.horizontalResolution = horizontal;
        this.verticalResolution = vertical;
    }

    /// <summary>
    /// Returns a copy of the EXIF profile with XResolution/YResolution/ResolutionUnit synchronised to this
    /// metadata's resolution, ready to be serialized by an encoder; <see langword="null"/> when there is no profile.
    /// </summary>
    internal ExifProfile? PrepareExifForWrite()
    {
        if (this.ExifProfile is null)
        {
            return null;
        }

        ExifProfile copy = this.ExifProfile.DeepClone();
        this.SyncResolutionInto(copy);
        return copy;
    }

    /// <summary>Writes this metadata's resolution into the given profile's TIFF resolution tags.</summary>
    internal void SyncResolutionInto(ExifProfile profile)
    {
        (double h, double v, ushort unit) = this.ResolutionUnits switch
        {
            PixelResolutionUnit.AspectRatio => (this.horizontalResolution, this.verticalResolution, (ushort)1),
            PixelResolutionUnit.PixelsPerCentimeter => (this.horizontalResolution, this.verticalResolution, (ushort)3),
            PixelResolutionUnit.PixelsPerMeter => (
                this.GetHorizontalResolution(PixelResolutionUnit.PixelsPerCentimeter),
                this.GetVerticalResolution(PixelResolutionUnit.PixelsPerCentimeter),
                (ushort)3),
            _ => (this.horizontalResolution, this.verticalResolution, (ushort)2),
        };

        profile.SetValue(ExifTag.XResolution, new Rational(h));
        profile.SetValue(ExifTag.YResolution, new Rational(v));
        profile.SetValue(ExifTag.ResolutionUnit, unit);
    }
}
