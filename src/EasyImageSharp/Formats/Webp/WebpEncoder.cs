using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// Encodes images as WebP (RFC 9649). Single-frame images become a simple 'VP8L' (lossless) or 'VP8 ' (lossy)
/// file, and the extended 'VP8X' container is used only when something needs it: a lossy frame with alpha, an
/// animation, or an ICC / EXIF / XMP profile to carry. Multi-frame images become animations, with each frame
/// shrunk to the rectangle that actually changed.
/// </summary>
/// <remarks>
/// <para>
/// Lossless output is exact for every pixel format the library has. The lossy path needs the VP8 frame encoder
/// to be part of the build; when it is not, <see cref="WebpFileFormat.Lossy"/> raises
/// <see cref="NotSupportedException"/> and <see cref="WebpFileFormat.Auto"/> falls back to lossless.
/// </para>
/// <para>
/// Animation frames are read as fully composited pictures, which is what the decoder produces, and the encoder
/// derives the sub-frame rectangles, blending and disposal itself; a <see cref="WebpFrameMetadata"/> left on a
/// frame contributes its duration and disposal, and its rectangle when that rectangle still covers everything
/// the frame changes.
/// </para>
/// </remarks>
public sealed class WebpEncoder : IImageEncoder
{
    /// <summary>The largest width or height a WebP bitstream can express.</summary>
    public const int MaxDimension = 16383;

    private const int DefaultFrameDelay = 100;

    private readonly int? frameDelay;
    private readonly int? repeatCount;
    private readonly int quality = 75;
    private readonly int method = 4;
    private readonly int nearLosslessQuality = 60;
    private readonly int alphaQuality = 100;

    /// <summary>Which bitstream to write. Defaults to <see cref="WebpFileFormat.Auto"/>.</summary>
    public WebpFileFormat FileFormat { get; init; } = WebpFileFormat.Auto;

    /// <summary>
    /// Encoding quality, 1 to 100. For lossy output it trades detail for size; for lossless output it only
    /// controls how hard the backward-reference search works. Defaults to 75.
    /// </summary>
    public int Quality
    {
        get => this.quality;
        init
        {
            if (value is < 1 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Quality must be between 1 and 100.");
            }

            this.quality = value;
        }
    }

    /// <summary>Effort level, 0 (fastest) to 6 (smallest). Defaults to 4.</summary>
    public int Method
    {
        get => this.method;
        init
        {
            if (value is < 0 or > 6)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Method must be between 0 and 6.");
            }

            this.method = value;
        }
    }

    /// <summary>
    /// Whether to run the near-lossless preprocessing before a lossless encode. It reduces the bit depth of
    /// pixels that sit in flat areas, which compresses much better for a bounded, usually invisible, error.
    /// </summary>
    public bool NearLossless { get; init; }

    /// <summary>
    /// How much detail <see cref="NearLossless"/> keeps, 0 to 100. 100 changes nothing, 0 discards five bits
    /// per channel. Defaults to 60.
    /// </summary>
    public int NearLosslessQuality
    {
        get => this.nearLosslessQuality;
        init
        {
            if (value is < 0 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "NearLosslessQuality must be between 0 and 100.");
            }

            this.nearLosslessQuality = value;
        }
    }

    /// <summary>How the alpha plane of a lossy frame is stored. Defaults to <see cref="WebpAlphaCompression.Lossless"/>.</summary>
    public WebpAlphaCompression AlphaCompression { get; init; } = WebpAlphaCompression.Lossless;

    /// <summary>
    /// The effort spent compressing the alpha plane of a lossy frame, 1 to 100. The plane itself is always
    /// reproduced exactly. Defaults to 100.
    /// </summary>
    public int AlphaQuality
    {
        get => this.alphaQuality;
        init
        {
            if (value is < 1 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "AlphaQuality must be between 1 and 100.");
            }

            this.alphaQuality = value;
        }
    }

    /// <summary>What to do with the colour of fully transparent pixels. Defaults to <see cref="WebpTransparentColorMode.Preserve"/>.</summary>
    public WebpTransparentColorMode TransparentColorMode { get; init; } = WebpTransparentColorMode.Preserve;

    /// <summary>When true, the ICC, EXIF and XMP profiles on the image are not written. Defaults to false.</summary>
    public bool SkipMetadata { get; init; }

    /// <summary>
    /// The duration written for every animation frame, in milliseconds (0 to 16777215). When it is not set
    /// explicitly each frame uses its own <see cref="WebpFrameMetadata.FrameDelay"/>, falling back to 100.
    /// </summary>
    public int FrameDelay
    {
        get => this.frameDelay ?? DefaultFrameDelay;
        init
        {
            if (value is < 0 or > 0xffffff)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "FrameDelay must be between 0 and 16777215 milliseconds.");
            }

            this.frameDelay = value;
        }
    }

    /// <summary>
    /// How many times an animation plays (0 to 65535); 0 loops forever. When it is not set explicitly the
    /// image's <see cref="WebpMetadata.RepeatCount"/> is used, falling back to 0.
    /// </summary>
    public int RepeatCount
    {
        get => this.repeatCount ?? 0;
        init
        {
            if (value is < 0 or > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "RepeatCount must be between 0 and 65535.");
            }

            this.repeatCount = value;
        }
    }

    /// <inheritdoc/>
    public void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        int width = image.Width;
        int height = image.Height;
        if (width > MaxDimension || height > MaxDimension)
        {
            throw new NotSupportedException(
                $"WebP cannot store a {width}x{height} image; both dimensions must be at most {MaxDimension}.");
        }

        Rgba32[][] frames = ReadFrames(image, width, height);
        bool hasAlpha = false;
        foreach (Rgba32[] frame in frames)
        {
            this.PrepareTransparency(frame);
            hasAlpha |= HasTransparency(frame);
        }

        Profiles profiles = this.ReadProfiles(image.Metadata);
        bool lossless = this.ResolveLossless(frames[0]);

        var muxer = new WebpMuxer();
        if (frames.Length > 1)
        {
            this.WriteAnimation(muxer, image, frames, width, height, hasAlpha, lossless, profiles);
        }
        else
        {
            this.WriteStill(muxer, frames[0], width, height, hasAlpha, lossless, profiles);
        }

        muxer.WriteTo(stream);
    }

    // ----- Still images -----

    private void WriteStill(WebpMuxer muxer, Rgba32[] pixels, int width, int height, bool hasAlpha, bool lossless, Profiles profiles)
    {
        byte[]? alphaChunk = null;
        byte[] bitstream;
        if (lossless)
        {
            bitstream = this.EncodeLossless(pixels, width, height, hasAlpha);
        }
        else
        {
            bitstream = this.EncodeLossy(pixels, width, height);
            if (hasAlpha)
            {
                alphaChunk = this.EncodeAlpha(pixels, width, height);
            }
        }

        byte flags = profiles.Flags();
        if (alphaChunk is not null)
        {
            flags |= WebpMuxer.FlagAlpha;
        }

        if (flags == 0)
        {
            muxer.WriteChunk(lossless ? "VP8L"u8 : "VP8 "u8, bitstream);
            return;
        }

        muxer.WriteVp8X(flags, width, height);
        profiles.WriteIcc(muxer);
        if (alphaChunk is not null)
        {
            muxer.WriteChunk("ALPH"u8, alphaChunk);
        }

        muxer.WriteChunk(lossless ? "VP8L"u8 : "VP8 "u8, bitstream);
        profiles.WriteExifAndXmp(muxer);
    }

    // ----- Animations -----

    private void WriteAnimation<TPixel>(
        WebpMuxer muxer, Image<TPixel> image, Rgba32[][] frames, int width, int height, bool hasAlpha, bool lossless, Profiles profiles)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        byte flags = (byte)(WebpMuxer.FlagAnimation | profiles.Flags());
        if (hasAlpha)
        {
            flags |= WebpMuxer.FlagAlpha;
        }

        muxer.WriteVp8X(flags, width, height);
        profiles.WriteIcc(muxer);

        uint background = image.Metadata.TryGetFormatMetadata(out WebpMetadata? webpMetadata) ? webpMetadata.BackgroundColor : 0u;
        int loops = this.repeatCount ?? (webpMetadata?.RepeatCount ?? 0);
        muxer.WriteAnim(background, loops);

        var canvas = new Rgba32[width * height];
        bool previousDisposed = false;
        for (int i = 0; i < frames.Length; i++)
        {
            WebpFrameMetadata? frameMetadata =
                image.Frames[i].Metadata.TryGetFormatMetadata(out WebpFrameMetadata? found) ? found : null;
            int duration = this.frameDelay ?? (frameMetadata is not null && frameMetadata.FrameDelay > 0 ? frameMetadata.FrameDelay : DefaultFrameDelay);
            bool dispose = frameMetadata?.DisposalMethod == WebpDisposalMethod.DisposeToBackground;

            Rgba32[] target = frames[i];
            Rectangle rectangle = i == 0
                ? new Rectangle(0, 0, width, height)
                : ChangedRectangle(canvas, target, width, height, frameMetadata);

            bool allowBlend = i > 0 && !previousDisposed && frameMetadata?.BlendMethod != WebpBlendMethod.DoNotBlend;
            byte[] chunks = this.EncodeFrameVariants(canvas, target, width, rectangle, lossless, allowBlend, out bool blend);
            muxer.WriteAnmf(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, duration, dispose, blend, chunks);

            target.CopyTo(canvas, 0);
            if (dispose)
            {
                for (int y = 0; y < rectangle.Height; y++)
                {
                    canvas.AsSpan(((rectangle.Y + y) * width) + rectangle.X, rectangle.Width).Clear();
                }
            }

            previousDisposed = dispose;
        }

        profiles.WriteExifAndXmp(muxer);
    }

    /// <summary>
    /// Encodes one sub-frame both ways — overwriting the rectangle, and blending it so that unchanged pixels
    /// can be left transparent — and keeps whichever came out smaller. Blending is only offered when every
    /// pixel the frame changes is opaque, because blending can never lower the alpha already on the canvas.
    /// </summary>
    private byte[] EncodeFrameVariants(Rgba32[] canvas, Rgba32[] target, int canvasWidth, Rectangle rectangle, bool lossless, bool allowBlend, out bool blend)
    {
        Rgba32[] direct = Crop(target, canvasWidth, rectangle);
        byte[] best = this.EncodeFrameChunks(direct, rectangle.Width, rectangle.Height, lossless);
        blend = false;

        if (!allowBlend)
        {
            return best;
        }

        var blended = new Rgba32[rectangle.Width * rectangle.Height];
        for (int y = 0; y < rectangle.Height; y++)
        {
            int source = ((rectangle.Y + y) * canvasWidth) + rectangle.X;
            int destination = y * rectangle.Width;
            for (int x = 0; x < rectangle.Width; x++)
            {
                Rgba32 wanted = target[source + x];
                if (wanted.Equals(canvas[source + x]))
                {
                    blended[destination + x] = default;
                }
                else if (wanted.A == 255)
                {
                    blended[destination + x] = wanted;
                }
                else
                {
                    return best;
                }
            }
        }

        byte[] candidate = this.EncodeFrameChunks(blended, rectangle.Width, rectangle.Height, lossless);
        if (candidate.Length < best.Length)
        {
            blend = true;
            return candidate;
        }

        return best;
    }

    private byte[] EncodeFrameChunks(Rgba32[] pixels, int width, int height, bool lossless)
    {
        using var buffer = new MemoryStream();
        bool hasAlpha = HasTransparency(pixels);
        if (lossless)
        {
            WebpMuxer.WriteChunkTo(buffer, "VP8L"u8, this.EncodeLossless(pixels, width, height, hasAlpha));
        }
        else
        {
            if (hasAlpha)
            {
                WebpMuxer.WriteChunkTo(buffer, "ALPH"u8, this.EncodeAlpha(pixels, width, height));
            }

            WebpMuxer.WriteChunkTo(buffer, "VP8 "u8, this.EncodeLossy(pixels, width, height));
        }

        return buffer.ToArray();
    }

    /// <summary>Finds the rectangle outside which the frame is identical to what the canvas already shows.</summary>
    private static Rectangle ChangedRectangle(Rgba32[] canvas, Rgba32[] target, int width, int height, WebpFrameMetadata? metadata)
    {
        int minX = width;
        int minY = height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (!target[row + x].Equals(canvas[row + x]))
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        if (maxX < 0)
        {
            // Nothing moved: the format still needs a rectangle, so send the cheapest one there is.
            return new Rectangle(0, 0, 1, 1);
        }

        // Frame offsets are stored halved, so they have to be even.
        minX &= ~1;
        minY &= ~1;
        var changed = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);

        if (metadata is not null)
        {
            int x0 = Math.Max(0, metadata.X) & ~1;
            int y0 = Math.Max(0, metadata.Y) & ~1;
            int x1 = Math.Min(width, metadata.X + metadata.Width);
            int y1 = Math.Min(height, metadata.Y + metadata.Height);
            if (x1 > x0 && y1 > y0 && x0 <= changed.X && y0 <= changed.Y
                && x1 >= changed.X + changed.Width && y1 >= changed.Y + changed.Height)
            {
                return new Rectangle(x0, y0, x1 - x0, y1 - y0);
            }
        }

        return changed;
    }

    private static Rgba32[] Crop(Rgba32[] source, int sourceWidth, Rectangle rectangle)
    {
        var result = new Rgba32[rectangle.Width * rectangle.Height];
        for (int y = 0; y < rectangle.Height; y++)
        {
            source.AsSpan(((rectangle.Y + y) * sourceWidth) + rectangle.X, rectangle.Width)
                .CopyTo(result.AsSpan(y * rectangle.Width, rectangle.Width));
        }

        return result;
    }

    // ----- Bitstreams -----

    private byte[] EncodeLossless(Rgba32[] pixels, int width, int height, bool hasAlpha)
    {
        var argb = new uint[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 pixel = pixels[i];
            argb[i] = ((uint)pixel.A << 24) | ((uint)pixel.R << 16) | ((uint)pixel.G << 8) | pixel.B;
        }

        if (this.NearLossless)
        {
            argb = WebpNearLossless.Apply(argb, width, height, this.NearLosslessQuality);
        }

        return Vp8LEncoder.Encode(argb, width, height, hasAlpha, this.Quality, this.Method);
    }

    private byte[] EncodeLossy(Rgba32[] pixels, int width, int height)
    {
        IVp8FrameEncoder encoder = Vp8FrameEncoderFactory.Create()
            ?? throw new NotSupportedException("Lossy WebP encoding is not available in this build; use lossless encoding.");
        WebpMuxer.ToYuv420(pixels, width, height, out byte[] y, out byte[] u, out byte[] v);
        return encoder.EncodeKeyFrame(y, u, v, width, height, this.Quality, this.Method);
    }

    private byte[] EncodeAlpha(Rgba32[] pixels, int width, int height)
    {
        var alpha = new byte[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            alpha[i] = pixels[i].A;
        }

        return WebpAlphaEncoder.Encode(alpha, width, height, this.AlphaCompression, this.AlphaQuality, this.Method);
    }

    // ----- Input preparation -----

    private static Rgba32[][] ReadFrames<TPixel>(Image<TPixel> image, int width, int height)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var frames = new Rgba32[image.Frames.Count][];
        for (int i = 0; i < frames.Length; i++)
        {
            ImageFrame<TPixel> frame = image.Frames[i];
            var pixels = new Rgba32[width * height];
            int rows = Math.Min(height, frame.Height);
            int columns = Math.Min(width, frame.Width);
            var row = new Rgba32[frame.Width];
            for (int y = 0; y < rows; y++)
            {
                PixelOps.ToRgba32(frame.GetRowSpan(y), row);
                row.AsSpan(0, columns).CopyTo(pixels.AsSpan(y * width, columns));
            }

            frames[i] = pixels;
        }

        return frames;
    }

    private void PrepareTransparency(Rgba32[] pixels)
    {
        if (this.TransparentColorMode != WebpTransparentColorMode.Clear)
        {
            return;
        }

        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i].A == 0)
            {
                pixels[i] = default;
            }
        }
    }

    private static bool HasTransparency(Rgba32[] pixels)
    {
        foreach (Rgba32 pixel in pixels)
        {
            if (pixel.A != 255)
            {
                return true;
            }
        }

        return false;
    }

    private bool ResolveLossless(Rgba32[] pixels) => this.FileFormat switch
    {
        WebpFileFormat.Lossless => true,
        WebpFileFormat.Lossy => false,
        _ => Vp8FrameEncoderFactory.Create() is null || CountsAsGraphics(pixels),
    };

    /// <summary>True for images with few enough distinct colours that a lossless encode is both smaller and exact.</summary>
    private static bool CountsAsGraphics(Rgba32[] pixels)
    {
        var colors = new HashSet<uint>();
        foreach (Rgba32 pixel in pixels)
        {
            colors.Add(((uint)pixel.A << 24) | ((uint)pixel.R << 16) | ((uint)pixel.G << 8) | pixel.B);
            if (colors.Count > 256)
            {
                return false;
            }
        }

        return true;
    }

    private Profiles ReadProfiles(ImageMetadata metadata)
    {
        if (this.SkipMetadata)
        {
            return default;
        }

        ExifProfile? exif = metadata.PrepareExifForWrite();
        return new Profiles
        {
            Icc = metadata.IccProfile?.ToByteArray(),
            Exif = exif?.ToByteArray(),
            Xmp = metadata.XmpProfile?.ToByteArray(),
        };
    }

    /// <summary>The optional colour and metadata profiles a file may carry, and the VP8X flags they imply.</summary>
    private readonly struct Profiles
    {
        public byte[]? Icc { get; init; }

        public byte[]? Exif { get; init; }

        public byte[]? Xmp { get; init; }

        public byte Flags()
        {
            byte flags = 0;
            if (this.Icc is { Length: > 0 })
            {
                flags |= WebpMuxer.FlagIccProfile;
            }

            if (this.Exif is { Length: > 0 })
            {
                flags |= WebpMuxer.FlagExif;
            }

            if (this.Xmp is { Length: > 0 })
            {
                flags |= WebpMuxer.FlagXmp;
            }

            return flags;
        }

        public void WriteIcc(WebpMuxer muxer)
        {
            if (this.Icc is { Length: > 0 })
            {
                muxer.WriteChunk("ICCP"u8, this.Icc);
            }
        }

        public void WriteExifAndXmp(WebpMuxer muxer)
        {
            if (this.Exif is { Length: > 0 })
            {
                muxer.WriteChunk("EXIF"u8, this.Exif);
            }

            if (this.Xmp is { Length: > 0 })
            {
                muxer.WriteChunk("XMP "u8, this.Xmp);
            }
        }
    }
}
