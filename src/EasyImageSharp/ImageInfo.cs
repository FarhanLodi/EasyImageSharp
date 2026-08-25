using EasyImageSharp.Formats;
using EasyImageSharp.Metadata;

namespace EasyImageSharp;

/// <summary>Describes the pixel type of an image without decoding it.</summary>
public sealed class PixelTypeInfo
{
    internal PixelTypeInfo(int bitsPerPixel) => this.BitsPerPixel = bitsPerPixel;

    public int BitsPerPixel { get; }
}

/// <summary>Header-level information about an encoded image, produced by <see cref="Image.Identify(ReadOnlySpan{byte})"/>.</summary>
public sealed class ImageInfo
{
    internal ImageInfo(int width, int height, int bitsPerPixel, int frameCount, ImageFormat format)
        : this(width, height, bitsPerPixel, frameCount, format, null)
    {
    }

    internal ImageInfo(int width, int height, int bitsPerPixel, int frameCount, ImageFormat format, ImageMetadata? metadata)
    {
        this.Width = width;
        this.Height = height;
        this.PixelType = new PixelTypeInfo(bitsPerPixel);
        this.FrameCount = frameCount;
        this.Format = format;
        this.Metadata = metadata ?? new ImageMetadata();
        this.Metadata.DecodedImageFormat ??= format;
    }

    public int Width { get; }

    public int Height { get; }

    public Size Size => new(this.Width, this.Height);

    public PixelTypeInfo PixelType { get; }

    /// <summary>The number of frames (e.g. TIFF pages) in the image.</summary>
    public int FrameCount { get; }

    public ImageFormat Format { get; }

    /// <summary>The metadata readable from the file's headers (resolution, EXIF/ICC/XMP, format facts) without decoding pixels.</summary>
    public ImageMetadata Metadata { get; }

    public override string ToString()
        => $"ImageInfo [ Format={this.Format.Name}, Width={this.Width}, Height={this.Height}, BitsPerPixel={this.PixelType.BitsPerPixel}, Frames={this.FrameCount} ]";
}
