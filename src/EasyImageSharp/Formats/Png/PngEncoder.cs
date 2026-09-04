using System.Buffers.Binary;
using System.IO.Compression;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing.Quantization;

namespace EasyImageSharp.Formats.Png;

/// <summary>
/// Encodes images as PNG. By default the colour type follows the pixel format (<see cref="L8"/> becomes 8-bit
/// grayscale, opaque RGB formats truecolor, everything else truecolor with alpha) at 8 bits per sample; set
/// <see cref="ColorType"/> and <see cref="BitDepth"/> to write palette images (quantized with
/// <see cref="Quantizer"/>), 1/2/4-bit grayscale, 16-bit samples, a fixed scanline filter or Adam7 interlacing.
/// </summary>
/// <remarks>
/// <para>
/// An image with more than one frame - or one whose <see cref="PngMetadata.IsAnimated"/> is set - is written as
/// an APNG: an acTL chunk, an fcTL for every frame, the first frame's pixels in IDAT and every later frame's in
/// an fdAT. Frames arrive fully composited, which is what the decoder produces, and the encoder derives each
/// frame's sub-rectangle itself by diffing against the canvas the frames before it leave behind. A
/// <see cref="PngFrameMetadata"/> left on a frame contributes its delay and its disposal, and bounds its
/// blending: a frame whose metadata names <see cref="PngBlendMethod.Source"/> is always written with SOURCE,
/// while one naming <see cref="PngBlendMethod.Over"/> - or carrying no PNG metadata at all - is written
/// whichever way comes out smaller, which is pixel for pixel the same picture either way.
/// To write a single still PNG from a multi-frame image, encode <c>image.Frames.ExportFrame(0)</c> instead.
/// </para>
/// <para>
/// Animated output is truecolour or grayscale at 8 or 16 bits: <see cref="PngColorType.Palette"/> and grayscale
/// below 8 bits are refused, because every frame would have to share one palette while the quantizer runs per
/// frame. A single-frame image that is not marked as animated is written exactly as it always was, with no
/// animation chunks at all.
/// </para>
/// </remarks>
public sealed class PngEncoder : IImageEncoder
{
    // Adam7 pass geometry: (x start, y start, x step, y step).
    private static readonly (int X0, int Y0, int Dx, int Dy)[] Adam7Passes =
    {
        (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2),
    };

    private const int DefaultFrameDelay = 100;

    private readonly int? frameDelay;
    private readonly int? repeatCount;

    /// <summary>The deflate effort used for the IDAT stream. Defaults to <see cref="CompressionLevel.Optimal"/>.</summary>
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Optimal;

    /// <summary>The colour type to write; <see langword="null"/> picks one from the pixel format.</summary>
    public PngColorType? ColorType { get; init; }

    /// <summary>
    /// Bits per sample; <see langword="null"/> writes 8 bits (or, for palette images, the smallest depth that
    /// holds the palette). Must be valid for the colour type, see <see cref="PngColorType"/>.
    /// </summary>
    public PngBitDepth? BitDepth { get; init; }

    /// <summary>The scanline filter strategy. Defaults to <see cref="PngFilterMethod.Adaptive"/>.</summary>
    public PngFilterMethod FilterMethod { get; init; } = PngFilterMethod.Adaptive;

    /// <summary>Whether to interlace. Defaults to <see cref="PngInterlaceMethod.None"/>.</summary>
    public PngInterlaceMethod InterlaceMethod { get; init; } = PngInterlaceMethod.None;

    /// <summary>
    /// The quantizer used for <see cref="PngColorType.Palette"/> output and for dithering grayscale images below
    /// 8 bits; <see langword="null"/> uses <see cref="KnownQuantizers.Wu"/> (256 colours, Floyd–Steinberg).
    /// </summary>
    public IQuantizer? Quantizer { get; init; }

    /// <summary>What to do with the colour of fully transparent pixels. Defaults to <see cref="PngTransparentColorMode.Preserve"/>.</summary>
    public PngTransparentColorMode TransparentColorMode { get; init; } = PngTransparentColorMode.Preserve;

    /// <summary>
    /// The delay written for every animation frame, in milliseconds (0 to 65535). When it is not set
    /// explicitly each frame uses its own <see cref="PngFrameMetadata.FrameDelay"/>, falling back to 100.
    /// </summary>
    public int FrameDelay
    {
        get => this.frameDelay ?? DefaultFrameDelay;
        init
        {
            if (value is < 0 or > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "FrameDelay must be between 0 and 65535 milliseconds.");
            }

            this.frameDelay = value;
        }
    }

    /// <summary>
    /// How many times an animation plays; 0 loops forever. When it is not set explicitly the image's
    /// <see cref="PngMetadata.RepeatCount"/> is used, falling back to 0.
    /// </summary>
    public int RepeatCount
    {
        get => this.repeatCount ?? 0;
        init
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "RepeatCount must not be negative.");
            }

            this.repeatCount = value;
        }
    }

    public void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        int width = image.Width;
        int height = image.Height;
        ImageFrame<TPixel> frame = image.Frames.RootFrame;

        // More than one frame - or a single frame the caller marked as animated - becomes an APNG.
        PngMetadata? pngMetadata = image.Metadata.TryGetFormatMetadata(out PngMetadata? found) ? found : null;
        bool animate = image.Frames.Count > 1 || (pngMetadata?.IsAnimated ?? false);

        PngColorType colorType = this.ColorType ?? (animate ? AnimatedColorType<TPixel>() : DefaultColorType<TPixel>());
        int bitDepth = this.BitDepth.HasValue ? (int)this.BitDepth.Value : 8;
        if (this.BitDepth.HasValue)
        {
            ValidateBitDepth(colorType, bitDepth);
        }

        if (animate && (colorType == PngColorType.Palette || (colorType == PngColorType.Grayscale && bitDepth < 8)))
        {
            throw new NotSupportedException(
                "PNG palette and sub-8-bit grayscale output are not supported for animated images.");
        }

        // Palette and sub-byte grayscale go through a quantizer to obtain one index per pixel.
        byte[]? indices = null;
        Rgba32[]? palette = null;
        if (colorType == PngColorType.Palette)
        {
            (indices, palette) = this.QuantizeToPalette(frame, this.BitDepth.HasValue ? 1 << bitDepth : 256);
            if (!this.BitDepth.HasValue)
            {
                bitDepth = palette.Length <= 2 ? 1 : palette.Length <= 4 ? 2 : palette.Length <= 16 ? 4 : 8;
            }
        }
        else if (colorType == PngColorType.Grayscale && bitDepth < 8)
        {
            indices = this.QuantizeToGrayLevels(frame, bitDepth);
        }

        int channels = colorType switch
        {
            PngColorType.Grayscale or PngColorType.Palette => 1,
            PngColorType.GrayscaleWithAlpha => 2,
            PngColorType.Rgb => 3,
            _ => 4,
        };
        int bitsPerPixel = channels * bitDepth;
        var format = new FrameFormat
        {
            ColorType = colorType,
            BitDepth = bitDepth,
            BitsPerPixel = bitsPerPixel,
            FilterBpp = Math.Max(1, bitsPerPixel / 8),
            ClearTransparent = this.TransparentColorMode == PngTransparentColorMode.Clear
                && colorType is PngColorType.RgbWithAlpha or PngColorType.GrayscaleWithAlpha,
        };

        // Signature
        stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // IHDR
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = (byte)colorType;
        ihdr[10] = 0; // Compression method: deflate.
        ihdr[11] = 0; // Filter method: adaptive (per-scanline filter type bytes).
        ihdr[12] = (byte)this.InterlaceMethod;
        WriteChunk(stream, "IHDR"u8, ihdr);
        PngMetadataChunks.Write(stream, image.Metadata);

        // Metadata chunks (pHYs, tEXt, ...) belong here, directly after IHDR and before PLTE.

        if (animate)
        {
            this.WriteAnimation(stream, image, width, height, in format, pngMetadata);
            return;
        }

        if (palette is not null)
        {
            WritePaletteChunks(stream, palette);
        }

        // IDAT: filter each scanline (per pass when interlaced), then deflate everything into one chunk.
        WriteChunk(stream, "IDAT"u8, this.EncodeFrameData(frame, width, height, in format, indices));
        WriteChunk(stream, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Filters and deflates one frame's scanlines - every Adam7 pass in turn when interlaced - and returns the
    /// zlib stream an IDAT or fdAT chunk carries. The dimensions are the whole canvas for a still image and the
    /// frame's own rectangle for an animation frame, which is exactly what a sub-rectangle frame declares.
    /// </summary>
    private byte[] EncodeFrameData<TPixel>(
        ImageFrame<TPixel> frame, int width, int height, in FrameFormat format, byte[]? indices)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var scanlines = new ScanlineSource<TPixel>(frame, format.ColorType, format.BitDepth, indices, format.ClearTransparent);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, this.CompressionLevel, leaveOpen: true))
        {
            if (this.InterlaceMethod == PngInterlaceMethod.Adam7)
            {
                foreach ((int x0, int y0, int dx, int dy) in Adam7Passes)
                {
                    int passWidth = x0 < width ? (width - x0 + dx - 1) / dx : 0;
                    int passHeight = y0 < height ? (height - y0 + dy - 1) / dy : 0;
                    if (passWidth == 0 || passHeight == 0)
                    {
                        continue;
                    }

                    this.WritePass(zlib, scanlines, x0, y0, dx, dy, passWidth, passHeight, format.BitsPerPixel, format.FilterBpp);
                }
            }
            else
            {
                this.WritePass(zlib, scanlines, 0, 0, 1, 1, width, height, format.BitsPerPixel, format.FilterBpp);
            }
        }

        return compressed.ToArray();
    }

    // ----- Setup -----

    private static PngColorType DefaultColorType<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (typeof(TPixel) == typeof(L8))
        {
            return PngColorType.Grayscale;
        }

        if (typeof(TPixel) == typeof(Rgb24) || typeof(TPixel) == typeof(Bgr24))
        {
            return PngColorType.Rgb;
        }

        return PngColorType.RgbWithAlpha;
    }

    /// <summary>
    /// The colour type an animation defaults to: the alpha-bearing sibling of the still default. Sub-rectangle
    /// frames leave the pixels they do not touch fully transparent and blend them back over the canvas, which
    /// needs an alpha channel to express; a grayscale image still stays grayscale.
    /// </summary>
    private static PngColorType AnimatedColorType<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
        => DefaultColorType<TPixel>() switch
        {
            PngColorType.Grayscale => PngColorType.GrayscaleWithAlpha,
            PngColorType.Rgb => PngColorType.RgbWithAlpha,
            PngColorType other => other,
        };

    private static void ValidateBitDepth(PngColorType colorType, int bitDepth)
    {
        bool valid = colorType switch
        {
            PngColorType.Grayscale => bitDepth is 1 or 2 or 4 or 8 or 16,
            PngColorType.Palette => bitDepth is 1 or 2 or 4 or 8,
            _ => bitDepth is 8 or 16,
        };
        if (!valid)
        {
            throw new NotSupportedException($"PNG colour type {colorType} does not allow a bit depth of {bitDepth}.");
        }
    }

    private (byte[] Indices, Rgba32[] Palette) QuantizeToPalette<TPixel>(ImageFrame<TPixel> frame, int maxColors)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        IQuantizer quantizer = this.Quantizer ?? KnownQuantizers.Wu;
        QuantizerOptions options = quantizer.Options;
        if (options.MaxColors > maxColors)
        {
            options = options.WithMaxColors(maxColors);
        }

        IQuantizer<TPixel> worker = quantizer.CreatePixelSpecificQuantizer<TPixel>(options);
        IndexedImageFrame<TPixel> indexed = worker.QuantizeFrame(frame);
        ReadOnlySpan<TPixel> entries = indexed.Palette.Span;
        var palette = new Rgba32[entries.Length];
        for (int i = 0; i < palette.Length; i++)
        {
            palette[i] = entries[i].ToRgba32();
        }

        return (indexed.IndexArray, palette);
    }

    /// <summary>Reduces the frame's luminance to 2^bitDepth evenly spaced levels (dithered as the quantizer options say) and returns the level of every pixel.</summary>
    private byte[] QuantizeToGrayLevels<TPixel>(ImageFrame<TPixel> frame, int bitDepth)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int levels = 1 << bitDepth;
        var grays = new Color[levels];
        for (int i = 0; i < levels; i++)
        {
            byte v = (byte)(i * 255 / (levels - 1));
            grays[i] = new Color(v, v, v);
        }

        // Match on luminance: quantize a grayscale copy so colour pixels pick the level nearest their brightness.
        var luminance = frame.CloneAs<L8>();
        IQuantizer source = this.Quantizer ?? KnownQuantizers.Wu;
        var quantizer = new PaletteQuantizer(grays, source.Options);
        return quantizer.CreatePixelSpecificQuantizer<L8>().QuantizeFrame(luminance).IndexArray;
    }

    private static void WritePaletteChunks(Stream stream, Rgba32[] palette)
    {
        var plte = new byte[palette.Length * 3];
        int lastAlpha = -1;
        for (int i = 0; i < palette.Length; i++)
        {
            plte[i * 3] = palette[i].R;
            plte[(i * 3) + 1] = palette[i].G;
            plte[(i * 3) + 2] = palette[i].B;
            if (palette[i].A != byte.MaxValue)
            {
                lastAlpha = i;
            }
        }

        WriteChunk(stream, "PLTE"u8, plte);
        if (lastAlpha >= 0)
        {
            var trns = new byte[lastAlpha + 1];
            for (int i = 0; i <= lastAlpha; i++)
            {
                trns[i] = palette[i].A;
            }

            WriteChunk(stream, "tRNS"u8, trns);
        }
    }

    // ----- Animations -----

    /// <summary>
    /// Writes the APNG chunks: the animation control, then one frame control per frame with the first frame's
    /// pixels in IDAT and every later frame's in an fdAT. A single counter numbers the fcTL and fdAT chunks
    /// together, as the format requires. Frames after the first are shrunk to the rectangle that actually
    /// changed, so an animation only pays for the pixels that move.
    /// </summary>
    private void WriteAnimation<TPixel>(
        Stream stream, Image<TPixel> image, int width, int height, in FrameFormat format, PngMetadata? metadata)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Rgba32[][] frames = ReadFrames(image, width, height);
        if (format.ClearTransparent)
        {
            // Clear before diffing, so the canvas the encoder tracks is the one a decoder will end up with.
            foreach (Rgba32[] pixels in frames)
            {
                ClearTransparentColor(pixels);
            }
        }

        // An explicit RepeatCount wins; otherwise the play count a decoded animation carried is preserved.
        uint plays = this.repeatCount.HasValue ? (uint)this.repeatCount.Value : metadata?.RepeatCount ?? 0u;
        PngAnimation.WriteAnimationControl(stream, frames.Length, plays);

        // A hidden root frame keeps IDAT outside the animation: it is the still image an APNG-unaware reader
        // shows, and every frame including the first then gets its own fcTL and fdAT.
        bool animateRoot = metadata?.AnimateRootFrame ?? true;
        byte[]? still = null;
        if (!animateRoot)
        {
            still = this.EncodeFrameData(AsFrame(frames[0], width, height), width, height, in format, null);
            WriteChunk(stream, "IDAT"u8, still);
        }

        var canvas = new Rgba32[width * height];
        uint sequence = 0;
        bool previousDisposed = false;
        for (int i = 0; i < frames.Length; i++)
        {
            PngFrameMetadata? frameMetadata =
                image.Frames[i].Metadata.TryGetFormatMetadata(out PngFrameMetadata? found) ? found : null;
            (ushort delayNumerator, ushort delayDenominator) = this.ResolveDelay(frameMetadata);
            PngDisposalMethod disposal = frameMetadata?.DisposalMethod ?? PngDisposalMethod.None;

            // The first frame is always the whole canvas: it is the IDAT image as well, and the format
            // requires that one to be the full size at the origin.
            Rgba32[] target = frames[i];
            Rectangle rectangle = i == 0
                ? new Rectangle(0, 0, width, height)
                : ChangedRectangle(canvas, target, width, height);

            bool blend = false;
            byte[] data;
            if (i == 0 && still is not null)
            {
                data = still;
            }
            else
            {
                bool allowBlend = i > 0 && format.HasAlpha && !previousDisposed
                    && frameMetadata?.BlendMethod != PngBlendMethod.Source;
                data = this.EncodeFrameVariants(canvas, target, width, rectangle, in format, allowBlend, out blend);
            }

            var control = new ApngFrameControl
            {
                Width = rectangle.Width,
                Height = rectangle.Height,
                XOffset = rectangle.X,
                YOffset = rectangle.Y,
            };
            Rgba32[]? saved = disposal == PngDisposalMethod.RestoreToPrevious
                ? PngAnimation.CopyRegion(canvas, width, control)
                : null;

            PngAnimation.WriteFrameControl(
                stream, sequence++, rectangle, delayNumerator, delayDenominator, disposal,
                blend ? PngBlendMethod.Over : PngBlendMethod.Source);
            if (i == 0 && animateRoot)
            {
                WriteChunk(stream, "IDAT"u8, data);
            }
            else
            {
                PngAnimation.WriteFrameData(stream, sequence++, data);
            }

            // Track what a decoder now has on screen: the composited frame, then this frame's disposal.
            target.CopyTo(canvas, 0);
            PngAnimation.ApplyDisposal(canvas, width, disposal, control, saved);
            previousDisposed = disposal != PngDisposalMethod.None;
        }

        WriteChunk(stream, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Encodes one sub-frame both ways - overwriting the rectangle, and blending it so that unchanged pixels
    /// can be left transparent - and keeps whichever came out smaller. Blending is only offered when every
    /// pixel the frame changes is opaque, because source-over can never lower the alpha already on the canvas.
    /// </summary>
    private byte[] EncodeFrameVariants(
        Rgba32[] canvas, Rgba32[] target, int canvasWidth, in Rectangle rectangle, in FrameFormat format,
        bool allowBlend, out bool blend)
    {
        Rgba32[] direct = Crop(target, canvasWidth, rectangle);
        byte[] best = this.EncodeFrameData(
            AsFrame(direct, rectangle.Width, rectangle.Height), rectangle.Width, rectangle.Height, in format, null);
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
                if (!wanted.Equals(canvas[source + x]))
                {
                    if (wanted.A != byte.MaxValue)
                    {
                        return best;
                    }

                    blended[destination + x] = wanted;
                }
                else if (wanted.A == 0 && (wanted.R | wanted.G | wanted.B) != 0)
                {
                    // Source-over onto a fully transparent canvas pixel yields the source verbatim, so a pixel
                    // that is transparent but still carries a colour cannot be left to the blend to reproduce.
                    return best;
                }
            }
        }

        byte[] candidate = this.EncodeFrameData(
            AsFrame(blended, rectangle.Width, rectangle.Height), rectangle.Width, rectangle.Height, in format, null);
        if (candidate.Length < best.Length)
        {
            blend = true;
            return candidate;
        }

        return best;
    }

    /// <summary>
    /// Resolves the two 16-bit halves of a frame's delay. An explicit <see cref="FrameDelay"/> applies to every
    /// frame; otherwise a frame's own fraction is written verbatim whenever it fits, which is what lets a
    /// decoded animation re-encode to exactly the timing it came with.
    /// </summary>
    private (ushort Numerator, ushort Denominator) ResolveDelay(PngFrameMetadata? metadata)
    {
        if (this.frameDelay is null && metadata is not null)
        {
            Rational delay = metadata.FrameDelay;
            if (delay.Numerator > 0 && delay.Numerator <= ushort.MaxValue
                && delay.Denominator > 0 && delay.Denominator <= ushort.MaxValue)
            {
                return ((ushort)delay.Numerator, (ushort)delay.Denominator);
            }

            // A fraction too large for the chunk is approximated in milliseconds, the unit the option uses.
            double seconds = delay.Denominator > 0 ? delay.ToDouble() : 0d;
            if (seconds > 0d)
            {
                return ((ushort)Math.Clamp(Math.Round(seconds * 1000d), 0d, ushort.MaxValue), 1000);
            }
        }

        return ((ushort)(this.frameDelay ?? DefaultFrameDelay), 1000);
    }

    /// <summary>Finds the rectangle outside which the frame is identical to what the canvas already shows.</summary>
    private static Rectangle ChangedRectangle(Rgba32[] canvas, Rgba32[] target, int width, int height)
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

        // Nothing moved: the format still needs a rectangle, so send the cheapest one there is.
        return maxX < 0
            ? new Rectangle(0, 0, 1, 1)
            : new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Reads every frame as a full-canvas RGBA buffer, which is what the sub-rectangle diff needs.</summary>
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

    private static Rgba32[] Crop(Rgba32[] source, int sourceWidth, in Rectangle rectangle)
    {
        var result = new Rgba32[rectangle.Width * rectangle.Height];
        for (int y = 0; y < rectangle.Height; y++)
        {
            source.AsSpan(((rectangle.Y + y) * sourceWidth) + rectangle.X, rectangle.Width)
                .CopyTo(result.AsSpan(y * rectangle.Width, rectangle.Width));
        }

        return result;
    }

    private static void ClearTransparentColor(Rgba32[] pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i].A == 0)
            {
                pixels[i] = default;
            }
        }
    }

    /// <summary>Presents a plain RGBA buffer as a frame, so it can go through the same scanline machinery.</summary>
    private static ImageFrame<Rgba32> AsFrame(Rgba32[] pixels, int width, int height) => new(width, height, pixels);

    // ----- Scanlines and filtering -----

    private void WritePass<TPixel>(
        Stream zlib, ScanlineSource<TPixel> scanlines, int x0, int y0, int dx, int dy, int passWidth, int passHeight,
        int bitsPerPixel, int filterBpp)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int bytesPerRow = ((passWidth * bitsPerPixel) + 7) / 8;
        var current = new byte[bytesPerRow];
        var previous = new byte[bytesPerRow];
        var filtered = new byte[bytesPerRow + 1];
        var candidate = new byte[bytesPerRow];

        for (int row = 0; row < passHeight; row++)
        {
            scanlines.Fill(y0 + (row * dy), x0, dx, passWidth, current);
            filtered[0] = (byte)this.Filter(current, row == 0 ? default : previous, filterBpp, candidate, filtered.AsSpan(1));
            zlib.Write(filtered);
            (previous, current) = (current, previous);
        }
    }

    /// <summary>Filters one scanline into <paramref name="best"/> and returns the filter type byte.</summary>
    private int Filter(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous, int bpp, Span<byte> scratch, Span<byte> best)
    {
        switch (this.FilterMethod)
        {
            case PngFilterMethod.None:
                current.CopyTo(best);
                return 0;
            case PngFilterMethod.Sub:
                PngFilters.Filter(1, current, previous, bpp, best);
                return 1;
            case PngFilterMethod.Up:
                PngFilters.Filter(2, current, previous, bpp, best);
                return 2;
            case PngFilterMethod.Average:
                PngFilters.Filter(3, current, previous, bpp, best);
                return 3;
            case PngFilterMethod.Paeth:
                PngFilters.Filter(4, current, previous, bpp, best);
                return 4;
            default:
                return ChooseFilter(current, previous, bpp, scratch, best);
        }
    }

    /// <summary>
    /// Applies each PNG filter and keeps the one with the smallest absolute sum. Each filter runs as its
    /// own loop (rather than a switch inside one), so the vectorised Sub, Up and Average kernels apply and
    /// the sum is folded sixteen bytes at a time. The original early-exit only skipped work on candidates
    /// that were already losing, so scoring every candidate in full picks the same filter.
    /// </summary>
    private static int ChooseFilter(
        ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous, int bpp, Span<byte> scratch, Span<byte> best)
    {
        int bestFilter = 0;
        long bestSum = long.MaxValue;
        Span<byte> candidate = scratch[..current.Length];

        for (int filter = 0; filter <= 4; filter++)
        {
            PngFilters.Filter(filter, current, previous, bpp, candidate);
            long sum = PngFilters.AbsoluteSum(candidate);
            if (sum < bestSum)
            {
                bestSum = sum;
                bestFilter = filter;
                candidate.CopyTo(best);
            }
        }

        return bestFilter;
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, data.Length);
        stream.Write(lengthBytes);
        stream.Write(type);
        stream.Write(data);

        // Chained Append calls compose correctly: the entry XOR undoes the previous exit XOR.
        uint crc = Crc32.Append(Crc32.Append(0, type), data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    /// <summary>
    /// The output format every frame of one file shares: what IHDR declares, plus the two byte counts the
    /// filter needs. It is resolved once from the encoder options and the pixel type and then passed down,
    /// because an animation's frames all have to agree with the single IHDR at the top of the file.
    /// </summary>
    private readonly struct FrameFormat
    {
        /// <summary>The colour type written to IHDR.</summary>
        public PngColorType ColorType { get; init; }

        /// <summary>Bits per sample, 1 to 16.</summary>
        public int BitDepth { get; init; }

        /// <summary>Bits per pixel: channels times <see cref="BitDepth"/>.</summary>
        public int BitsPerPixel { get; init; }

        /// <summary>The filter's byte offset to the pixel on the left, at least 1.</summary>
        public int FilterBpp { get; init; }

        /// <summary>Whether the colour of fully transparent pixels is discarded before filtering.</summary>
        public bool ClearTransparent { get; init; }

        /// <summary>Whether the colour type carries an alpha channel, which is what a blended frame needs.</summary>
        public bool HasAlpha => this.ColorType is PngColorType.RgbWithAlpha or PngColorType.GrayscaleWithAlpha;
    }

    /// <summary>Produces packed scanline bytes for any colour type, bit depth and column subset (for Adam7).</summary>
    private sealed class ScanlineSource<TPixel>
        where TPixel : unmanaged, IPixel<TPixel>
    {
        private readonly ImageFrame<TPixel> frame;
        private readonly PngColorType colorType;
        private readonly int bitDepth;
        private readonly byte[]? indices;
        private readonly bool clearTransparent;
        private readonly Rgba32[] row;
        private int cachedRow = -1;

        public ScanlineSource(ImageFrame<TPixel> frame, PngColorType colorType, int bitDepth, byte[]? indices, bool clearTransparent)
        {
            this.frame = frame;
            this.colorType = colorType;
            this.bitDepth = bitDepth;
            this.indices = indices;
            this.clearTransparent = clearTransparent;
            this.row = new Rgba32[frame.Width];
        }

        /// <summary>Writes the samples of pixels (x0, x0 + dx, ...) of image row <paramref name="y"/> into <paramref name="dest"/>.</summary>
        public void Fill(int y, int x0, int dx, int count, Span<byte> dest)
        {
            if (this.indices is not null)
            {
                this.FillIndices(y, x0, dx, count, dest);
                return;
            }

            if (this.cachedRow != y)
            {
                PixelOps.ToRgba32<TPixel>(this.frame.GetRowSpan(y), this.row);
                if (this.clearTransparent)
                {
                    for (int i = 0; i < this.row.Length; i++)
                    {
                        if (this.row[i].A == 0)
                        {
                            this.row[i] = default;
                        }
                    }
                }

                this.cachedRow = y;
            }

            bool wide = this.bitDepth == 16;
            int o = 0;
            for (int i = 0; i < count; i++)
            {
                Rgba32 p = this.row[x0 + (i * dx)];
                switch (this.colorType)
                {
                    case PngColorType.Grayscale:
                        o = WriteSample(dest, o, PixelOps.Luminance8(p), wide);
                        break;
                    case PngColorType.GrayscaleWithAlpha:
                        o = WriteSample(dest, o, PixelOps.Luminance8(p), wide);
                        o = WriteSample(dest, o, p.A, wide);
                        break;
                    case PngColorType.Rgb:
                        o = WriteSample(dest, o, p.R, wide);
                        o = WriteSample(dest, o, p.G, wide);
                        o = WriteSample(dest, o, p.B, wide);
                        break;
                    default:
                        o = WriteSample(dest, o, p.R, wide);
                        o = WriteSample(dest, o, p.G, wide);
                        o = WriteSample(dest, o, p.B, wide);
                        o = WriteSample(dest, o, p.A, wide);
                        break;
                }
            }
        }

        private void FillIndices(int y, int x0, int dx, int count, Span<byte> dest)
        {
            ReadOnlySpan<byte> source = this.indices.AsSpan(y * this.frame.Width, this.frame.Width);
            if (this.bitDepth == 8)
            {
                for (int i = 0; i < count; i++)
                {
                    dest[i] = source[x0 + (i * dx)];
                }

                return;
            }

            // Pack MSB-first; the last byte of a row is zero-padded.
            dest.Clear();
            int perByte = 8 / this.bitDepth;
            for (int i = 0; i < count; i++)
            {
                int shift = 8 - this.bitDepth - ((i % perByte) * this.bitDepth);
                dest[i / perByte] |= (byte)(source[x0 + (i * dx)] << shift);
            }
        }

        private static int WriteSample(Span<byte> dest, int offset, byte value, bool wide)
        {
            dest[offset++] = value;
            if (wide)
            {
                dest[offset++] = value; // v * 257 = (v << 8) | v: exact 8-to-16-bit widening.
            }

            return offset;
        }
    }
}
