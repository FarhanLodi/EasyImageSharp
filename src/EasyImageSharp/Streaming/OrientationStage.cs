using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.Streaming;

/// <summary>
/// A pipeline stage that turns the rows it receives upright according to an EXIF orientation, with the same
/// lossless rotations and flips <see cref="IImageProcessingContext.AutoOrient"/> applies.
/// </summary>
/// <remarks>
/// A mirror (orientation 2) only reverses each row and streams. Every other orientation needs the last row
/// before it can emit the first, so the stage collects the frame - which in a transcode is the resized output,
/// not the source - and replays it transformed once complete. That frame is a materialised image like any
/// other and is held to <see cref="Formats.DecoderOptions.MaxPixels"/>.
/// </remarks>
/// <typeparam name="TPixel">The pixel format of the rows.</typeparam>
internal sealed class OrientationStage<TPixel> : IScanlineSink<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    private readonly IScanlineSink<TPixel> next;
    private readonly int orientation;
    private readonly long maxPixels;

    private ImageFrame<TPixel>? frame;
    private TPixel[]? mirrored;
    private ImageMetadata? metadata;
    private int width;
    private int height;
    private int rowsReceived;
    private State state;

    /// <summary>Creates the stage.</summary>
    /// <param name="next">The stage that receives the upright rows.</param>
    /// <param name="orientation">The EXIF orientation, 2 to 8.</param>
    /// <param name="maxPixels">The largest frame the stage may collect.</param>
    public OrientationStage(IScanlineSink<TPixel> next, int orientation, long maxPixels)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (orientation is < 2 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(orientation), orientation, "Only EXIF orientations 2 to 8 change the image.");
        }

        this.next = next;
        this.orientation = orientation;
        this.maxPixels = maxPixels;
    }

    private enum State
    {
        Created,
        Writing,
        Completed,
        Disposed,
    }

    /// <summary>Whether the orientation swaps width and height (EXIF 5 to 8).</summary>
    public static bool SwapsAxes(int orientation) => orientation is >= 5 and <= 8;

    /// <summary>
    /// Reads the EXIF orientation of <paramref name="metadata"/>: 2 to 8 when the stored pixels need turning, 1 for
    /// none (no profile, no tag, the identity, or a value outside the defined range, which AutoOrient also ignores).
    /// </summary>
    public static int ReadOrientation(ImageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return metadata.ExifProfile is { } profile
            && profile.TryGetValue(ExifTag.Orientation, out IExifValue<ushort>? value)
            && value.Value is >= 2 and <= 8
            ? value.Value
            : 1;
    }

    /// <summary>
    /// Records in <paramref name="metadata"/> that its pixels are upright, exactly as AutoOrient does: the
    /// orientation tag becomes 1 and, for the axis-swapping orientations, the EXIF pixel dimensions swap.
    /// </summary>
    public static void MarkUpright(ImageMetadata metadata, int orientation)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ExifProfile? profile = metadata.ExifProfile;
        if (profile is null || !profile.TryGetValue(ExifTag.Orientation, out IExifValue<ushort>? value))
        {
            return;
        }

        value.Value = 1;
        if (SwapsAxes(orientation)
            && profile.TryGetValue(ExifTag.PixelXDimension, out IExifValue<uint>? pixelX)
            && profile.TryGetValue(ExifTag.PixelYDimension, out IExifValue<uint>? pixelY))
        {
            (pixelX.Value, pixelY.Value) = (pixelY.Value, pixelX.Value);
        }
    }

    public void Begin(int width, int height, ImageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        this.EnsureState(State.Created);
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        this.width = width;
        this.height = height;
        this.state = State.Writing;
        if (this.orientation == 2)
        {
            this.mirrored = new TPixel[width];
            this.next.Begin(width, height, metadata);
            return;
        }

        long pixels = (long)width * height;
        if (pixels > this.maxPixels)
        {
            throw new ImageSizeLimitExceededException(
                $"Turning a {width}x{height} output upright holds it in memory, and {pixels:N0} pixels exceeds the configured "
                + $"limit of {this.maxPixels:N0}. Raise DecoderOptions.MaxPixels or turn AutoOrient off.");
        }

        this.metadata = metadata;
        this.frame = FrameFactory.CreateUninitialized<TPixel>(width, height);
    }

    public void WriteRow(ReadOnlySpan<TPixel> row)
    {
        this.EnsureState(State.Writing);
        if (this.rowsReceived >= this.height)
        {
            throw new InvalidOperationException($"All {this.height} rows have already been written.");
        }

        if (row.Length < this.width)
        {
            throw new ArgumentException($"The row holds {row.Length} pixels but the frame is {this.width} wide.", nameof(row));
        }

        row = row[..this.width];
        int y = this.rowsReceived++;
        if (this.mirrored is not null)
        {
            // FrameOps.Flip(Horizontal) reverses every row; nothing else is needed for a mirror.
            row.CopyTo(this.mirrored);
            this.mirrored.AsSpan().Reverse();
            this.next.WriteRow(this.mirrored);
            return;
        }

        row.CopyTo(this.frame!.GetRowSpan(y));
    }

    public void Complete()
    {
        this.EnsureState(State.Writing);
        if (this.rowsReceived != this.height)
        {
            throw new InvalidOperationException($"Only {this.rowsReceived} of {this.height} rows were written.");
        }

        this.state = State.Completed;
        if (this.frame is null)
        {
            this.next.Complete();
            return;
        }

        // The operations AutoOrient runs for each orientation, in its order.
        ImageFrame<TPixel> upright = this.orientation switch
        {
            3 => FrameOps.Rotate180(this.frame),
            4 => FrameOps.Flip(this.frame, FlipMode.Vertical),
            5 => FrameOps.Flip(FrameOps.Rotate90(this.frame), FlipMode.Horizontal),
            6 => FrameOps.Rotate90(this.frame),
            7 => FrameOps.Flip(FrameOps.Rotate270(this.frame), FlipMode.Horizontal),
            _ => FrameOps.Rotate270(this.frame),
        };

        this.frame = null;
        this.next.Begin(upright.Width, upright.Height, this.metadata!);
        for (int y = 0; y < upright.Height; y++)
        {
            OperationExecution.Checkpoint();
            this.next.WriteRow(upright.GetRowSpan(y));
        }

        this.next.Complete();
    }

    public void Dispose()
    {
        if (this.state == State.Disposed)
        {
            return;
        }

        this.state = State.Disposed;
        this.frame = null;
        this.mirrored = null;
        this.next.Dispose();
    }

    private void EnsureState(State expected)
    {
        ObjectDisposedException.ThrowIf(this.state == State.Disposed, this);
        if (this.state != expected)
        {
            throw new InvalidOperationException($"The orientation stage is {this.state}; this call needs it {expected}.");
        }
    }
}
