namespace EasyImageSharp.Formats;

/// <summary>
/// Settings and resource limits applied while decoding. Pass an instance to any <c>Image.Load</c> or
/// <c>Image.Identify</c> overload; when omitted, <see cref="Default"/> is used.
/// </summary>
/// <remarks>
/// Decoders enforce <see cref="MaxPixels"/> immediately after parsing the image header and before any
/// pixel memory is allocated, so a hostile file that claims enormous dimensions is rejected cheaply with an
/// <see cref="ImageSizeLimitExceededException"/>. Header inspection via <c>Identify</c> is never limited,
/// so callers can always inspect the declared size first.
/// </remarks>
public sealed class DecoderOptions
{
    /// <summary>The default limit for <see cref="MaxPixels"/>: 256 megapixels per frame (1 GiB of RGBA pixels).</summary>
    public const long DefaultMaxPixels = 256L * 1024 * 1024;

    private long maxPixels = DefaultMaxPixels;
    private int maxFrames = int.MaxValue;

    /// <summary>The options used when none are supplied.</summary>
    public static DecoderOptions Default { get; } = new();

    /// <summary>
    /// The maximum number of pixels (width × height) a single frame may contain. Frames declaring more pixels
    /// throw <see cref="ImageSizeLimitExceededException"/> before allocation. Defaults to <see cref="DefaultMaxPixels"/>.
    /// </summary>
    public long MaxPixels
    {
        get => this.maxPixels;
        init
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "MaxPixels must be positive.");
            }

            this.maxPixels = value;
        }
    }

    /// <summary>
    /// The maximum number of frames (for example TIFF pages) to decode. Frames beyond this count are not
    /// decoded; the header-reported count from <c>Identify</c> is unaffected. Defaults to unlimited.
    /// </summary>
    public int MaxFrames
    {
        get => this.maxFrames;
        init
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "MaxFrames must be positive.");
            }

            this.maxFrames = value;
        }
    }

    /// <summary>Throws <see cref="ImageSizeLimitExceededException"/> when a frame of the given size exceeds <see cref="MaxPixels"/>.</summary>
    internal void EnsureFrameWithinLimits(int width, int height, string formatName)
    {
        long pixels = (long)width * height;
        if (pixels > this.maxPixels)
        {
            throw new ImageSizeLimitExceededException(
                $"{formatName} image is {width}x{height} ({pixels:N0} pixels), which exceeds the configured limit of "
                + $"{this.maxPixels:N0} pixels. Raise DecoderOptions.MaxPixels to decode larger images.");
        }
    }
}
