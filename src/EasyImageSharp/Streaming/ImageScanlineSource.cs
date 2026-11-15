using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.Streaming;

/// <summary>
/// The fallback source: rows of an image that has already been decoded in full, for formats and variants no
/// row-streaming decoder handles (interlaced PNG, APNG, GIF, WebP, TIFF, BMP, ...). The rest of the pipeline is
/// the same whichever kind of source feeds it, so only the decode itself loses the bounded-memory property.
/// </summary>
/// <typeparam name="TPixel">The pixel format of the rows.</typeparam>
internal sealed class ImageScanlineSource<TPixel> : IScanlineSource<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    private readonly bool ownsImage;
    private Image<TPixel>? image;
    private int row;

    /// <summary>Creates a source over the root frame of <paramref name="image"/>.</summary>
    /// <param name="image">The decoded image; only its root frame is read.</param>
    /// <param name="ownsImage">True to dispose the image with the source.</param>
    public ImageScanlineSource(Image<TPixel> image, bool ownsImage)
    {
        ArgumentNullException.ThrowIfNull(image);
        this.image = image;
        this.ownsImage = ownsImage;
        this.Width = image.Width;
        this.Height = image.Height;
        this.Metadata = image.Metadata;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public ImageMetadata Metadata { get; }

    public int CurrentRow => this.row;

    public bool ReadRow(Span<TPixel> destination)
    {
        ObjectDisposedException.ThrowIf(this.image is null, this);
        if (this.row >= this.Height)
        {
            return false;
        }

        if (destination.Length < this.Width)
        {
            throw new ArgumentException($"The destination holds {destination.Length} pixels but a row has {this.Width}.", nameof(destination));
        }

        this.image.Frames.RootFrame.GetRowSpan(this.row).CopyTo(destination);
        this.row++;
        return true;
    }

    /// <summary>
    /// Resizes the decoded frame in place with <see cref="FrameOps.Resize"/>, before any row has been read. A
    /// source that is in memory anyway gains nothing from resizing row by row, and the in-memory resize runs in
    /// parallel; the two produce the same pixels.
    /// </summary>
    public void Resize(int width, int height, IResampler sampler, bool premultiplyAlpha, bool compand)
    {
        ObjectDisposedException.ThrowIf(this.image is null, this);
        if (this.row != 0)
        {
            throw new InvalidOperationException("The source can only be resized before its first row is read.");
        }

        ImageFrame<TPixel> frame = this.image.Frames.RootFrame;
        ImageFrame<TPixel> resized = FrameOps.Resize(frame, width, height, sampler, premultiplyAlpha, compand);
        if (!ReferenceEquals(resized, frame))
        {
            frame.ReplaceBuffer(resized.PixelArray, resized.Width, resized.Height);
        }

        this.Width = frame.Width;
        this.Height = frame.Height;
    }

    public void Dispose()
    {
        if (this.ownsImage)
        {
            this.image?.Dispose();
        }

        this.image = null;
    }
}
