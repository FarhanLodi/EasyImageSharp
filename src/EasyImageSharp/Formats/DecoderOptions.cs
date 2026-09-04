namespace EasyImageSharp.Formats;

/// <summary>
/// Settings and resource limits applied while decoding. Pass an instance to any <c>Image.Load</c> or
/// <c>Image.Identify</c> overload; when omitted, <see cref="Default"/> is used.
/// </summary>
/// <remarks>
/// <para>
/// Decoders enforce <see cref="MaxPixels"/> immediately after parsing the image header and before any
/// pixel memory is allocated, so a hostile file that claims enormous dimensions is rejected cheaply with an
/// <see cref="ImageSizeLimitExceededException"/>. Header inspection via <c>Identify</c> is never limited,
/// so callers can always inspect the declared size first.
/// </para>
/// <para>
/// <see cref="MaxPixels"/> bounds one frame. Multi-frame formats — animated GIF, animated WebP, APNG,
/// multi-page TIFF and concatenated Netpbm — additionally accumulate every frame they decode against
/// <see cref="MaxTotalPixels"/>, so a tiny file that declares hundreds of large frames cannot force
/// unbounded allocation even when <see cref="MaxFrames"/> is left at its unlimited default.
/// </para>
/// </remarks>
public sealed class DecoderOptions
{
    /// <summary>The default limit for <see cref="MaxPixels"/>: 256 megapixels per frame (1 GiB of RGBA pixels).</summary>
    public const long DefaultMaxPixels = 256L * 1024 * 1024;

    /// <summary>
    /// The default limit for <see cref="MaxTotalPixels"/>: 1 073 741 824 pixels summed over every decoded
    /// frame (4 GiB of RGBA pixels, four times the single-frame default).
    /// </summary>
    public const long DefaultMaxTotalPixels = 1L << 30;

    /// <summary>The largest pixel count a single .NET array can hold, which is what a frame buffer is.</summary>
    private const long MaxAddressablePixels = int.MaxValue;

    private long maxPixels = DefaultMaxPixels;
    private long maxTotalPixels = DefaultMaxTotalPixels;
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
    /// The maximum number of pixels a single decode may allocate across <em>all</em> of its frames.
    /// <see cref="MaxPixels"/> bounds one frame; this bounds their sum, so a small file that declares many
    /// large frames cannot exhaust memory. Defaults to <see cref="DefaultMaxTotalPixels"/>, which is four
    /// full-size frames at the default <see cref="MaxPixels"/>; lower it when decoding untrusted
    /// multi-frame uploads.
    /// </summary>
    public long MaxTotalPixels
    {
        get => this.maxTotalPixels;
        init
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "MaxTotalPixels must be positive.");
            }

            this.maxTotalPixels = value;
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
    /// <remarks>
    /// Also guarantees that <c>width * height</c> fits an <see cref="int"/>, which several decoders rely on
    /// when they size a pixel or index buffer directly from the header. The default <see cref="MaxPixels"/>
    /// implies that already, but callers may raise it past <see cref="int.MaxValue"/>, and a crafted header
    /// would then overflow the multiplication instead of being rejected.
    /// </remarks>
    internal void EnsureFrameWithinLimits(int width, int height, string formatName)
    {
        long pixels = (long)width * height;
        if (pixels > this.maxPixels)
        {
            throw new ImageSizeLimitExceededException(
                $"{formatName} image is {width}x{height} ({pixels:N0} pixels), which exceeds the configured limit of "
                + $"{this.maxPixels:N0} pixels. Raise DecoderOptions.MaxPixels to decode larger images.");
        }

        if (pixels > MaxAddressablePixels)
        {
            throw new ImageSizeLimitExceededException(
                $"{formatName} image is {width}x{height} ({pixels:N0} pixels), which exceeds the largest buffer this "
                + $"library can address ({MaxAddressablePixels:N0} pixels).");
        }
    }

    /// <summary>Starts a cumulative frame budget for one multi-frame decode.</summary>
    internal FrameBudget CreateBudget() => new(this);

    /// <summary>
    /// Accumulates the frames a single decode commits to, enforcing <see cref="MaxPixels"/> per frame and
    /// <see cref="MaxTotalPixels"/> across the decode as a whole. Stack-only by design: a budget belongs to
    /// one decode call, is never shared between threads and never outlives the loop that created it.
    /// </summary>
    internal ref struct FrameBudget
    {
        private readonly DecoderOptions owner;
        private long total;

        internal FrameBudget(DecoderOptions owner)
        {
            this.owner = owner;
            this.total = 0;
        }

        /// <summary>
        /// Checks one frame against <see cref="MaxPixels"/> and charges it to the cumulative budget, throwing
        /// <see cref="ImageSizeLimitExceededException"/> when the running total passes <see cref="MaxTotalPixels"/>.
        /// Call it before the frame is allocated, never after.
        /// </summary>
        internal void Add(int width, int height, string formatName)
        {
            this.owner.EnsureFrameWithinLimits(width, height, formatName);
            this.total += (long)width * height;
            if (this.total > this.owner.maxTotalPixels)
            {
                throw new ImageSizeLimitExceededException(
                    $"{formatName} decode reached {this.total:N0} pixels across all frames, which exceeds the configured "
                    + $"limit of {this.owner.maxTotalPixels:N0} pixels. Raise DecoderOptions.MaxTotalPixels to decode "
                    + "larger animations or multi-page documents, or lower DecoderOptions.MaxFrames.");
            }
        }
    }
}
