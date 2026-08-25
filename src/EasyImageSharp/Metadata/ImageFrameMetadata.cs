using System.Diagnostics.CodeAnalysis;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Xmp;

namespace EasyImageSharp.Metadata;

/// <summary>
/// Metadata attached to a single frame: optional per-frame EXIF/ICC/XMP profiles (TIFF pages carry their own)
/// and format-specific containers such as <see cref="GifFrameMetadata"/> or <see cref="TiffFrameMetadata"/>.
/// </summary>
public sealed class ImageFrameMetadata : IDeepCloneable<ImageFrameMetadata>
{
    private readonly Dictionary<Type, IFormatMetadata> formatMetadata = new();

    /// <summary>Creates empty frame metadata.</summary>
    public ImageFrameMetadata()
    {
    }

    private ImageFrameMetadata(ImageFrameMetadata other)
    {
        this.ExifProfile = other.ExifProfile?.DeepClone();
        this.IccProfile = other.IccProfile?.DeepClone();
        this.XmpProfile = other.XmpProfile?.DeepClone();
        foreach (KeyValuePair<Type, IFormatMetadata> pair in other.formatMetadata)
        {
            this.formatMetadata[pair.Key] = pair.Value.DeepClone();
        }
    }

    /// <summary>The frame's own EXIF profile (TIFF pages), or <see langword="null"/>. For the first frame this mirrors <see cref="ImageMetadata.ExifProfile"/> at decode time.</summary>
    public ExifProfile? ExifProfile { get; set; }

    /// <summary>The frame's own ICC profile (TIFF pages), or <see langword="null"/>.</summary>
    public IccProfile? IccProfile { get; set; }

    /// <summary>The frame's own XMP packet (TIFF pages), or <see langword="null"/>.</summary>
    public XmpProfile? XmpProfile { get; set; }

    /// <summary>Gets the format-specific frame metadata of type <typeparamref name="T"/>, creating a default instance on first access.</summary>
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

    /// <summary>Gets the format-specific frame metadata of type <typeparamref name="T"/> if it has been set.</summary>
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

    /// <summary>Stores (replacing) a format-specific frame metadata container.</summary>
    public void SetFormatMetadata<T>(T metadata)
        where T : class, IFormatMetadata
    {
        ArgumentNullException.ThrowIfNull(metadata);
        this.formatMetadata[typeof(T)] = metadata;
    }

    /// <summary>GIF frame metadata (created on first access).</summary>
    public GifFrameMetadata GetGifMetadata() => this.GetFormatMetadata<GifFrameMetadata>();

    /// <summary>TIFF page metadata (created on first access).</summary>
    public TiffFrameMetadata GetTiffMetadata() => this.GetFormatMetadata<TiffFrameMetadata>();

    /// <summary>PNG frame metadata (created on first access).</summary>
    public PngFrameMetadata GetPngMetadata() => this.GetFormatMetadata<PngFrameMetadata>();

    public ImageFrameMetadata DeepClone() => new(this);
}
