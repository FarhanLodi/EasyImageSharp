using EasyImageSharp.Formats;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Streaming;

/// <summary>
/// The fallback sink: collects the rows into an image and runs an ordinary <see cref="IImageEncoder"/> on it
/// once the last row arrives, for encoders and options that cannot write row by row (WebP, GIF, BMP, TIFF,
/// palette or interlaced PNG, ...). The output is identical to saving the collected image with that encoder,
/// because that is exactly what happens.
/// </summary>
/// <typeparam name="TPixel">The pixel format of the rows.</typeparam>
internal sealed class EncoderScanlineSink<TPixel> : IScanlineSink<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    private readonly Stream output;
    private readonly IImageEncoder encoder;
    private readonly long maxPixels;

    private Image<TPixel>? image;
    private int rowsReceived;
    private bool completed;
    private bool disposed;

    /// <summary>Creates the sink.</summary>
    /// <param name="output">The stream the encoder writes to; never disposed by the sink.</param>
    /// <param name="encoder">The encoder to run on the collected image.</param>
    /// <param name="maxPixels">The largest image the sink may collect.</param>
    public EncoderScanlineSink(Stream output, IImageEncoder encoder, long maxPixels)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(encoder);
        this.output = output;
        this.encoder = encoder;
        this.maxPixels = maxPixels;
    }

    public void Begin(int width, int height, ImageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (this.image is not null || this.completed)
        {
            throw new InvalidOperationException("The sink has already been started.");
        }

        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        long pixels = (long)width * height;
        if (pixels > this.maxPixels)
        {
            throw new ImageSizeLimitExceededException(
                $"{this.encoder.GetType().Name} encodes a whole image, so the {width}x{height} output is held in memory, and "
                + $"{pixels:N0} pixels exceeds the configured limit of {this.maxPixels:N0}. Raise DecoderOptions.MaxPixels "
                + "or choose an encoder that streams.");
        }

        // Every row is written before the encoder reads any, so the buffer does not need clearing.
        ImageFrame<TPixel> frame = FrameFactory.CreateUninitialized<TPixel>(width, height);
        this.image = new Image<TPixel>(new List<ImageFrame<TPixel>> { frame }, metadata);
    }

    public void WriteRow(ReadOnlySpan<TPixel> row)
    {
        Image<TPixel> target = this.Current();
        if (this.rowsReceived >= target.Height)
        {
            throw new InvalidOperationException($"All {target.Height} rows have already been written.");
        }

        if (row.Length < target.Width)
        {
            throw new ArgumentException($"The row holds {row.Length} pixels but the image is {target.Width} wide.", nameof(row));
        }

        row[..target.Width].CopyTo(target.Frames.RootFrame.GetRowSpan(this.rowsReceived++));
    }

    public void Complete()
    {
        Image<TPixel> target = this.Current();
        if (this.rowsReceived != target.Height)
        {
            throw new InvalidOperationException($"Only {this.rowsReceived} of {target.Height} rows were written.");
        }

        try
        {
            this.encoder.Encode(target, this.output);
        }
        finally
        {
            target.Dispose();
            this.image = null;
            this.completed = true;
        }
    }

    public void Dispose()
    {
        this.disposed = true;
        this.image?.Dispose();
        this.image = null;
    }

    private Image<TPixel> Current()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        return this.image ?? throw new InvalidOperationException(
            this.completed ? "The sink has already completed." : "Begin must be called before rows are written.");
    }
}
