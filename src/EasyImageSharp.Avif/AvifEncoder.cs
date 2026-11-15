using System.Runtime.InteropServices;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Webp;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Avif;

/// <summary>
/// Encodes images as AVIF through libavif and its AV1 encoder (libaom, rav1e or SVT-AV1, whichever the loaded
/// libavif provides). A single frame becomes a still image; several frames become an image sequence ('avis')
/// whose frame durations come from <see cref="FrameDuration"/> or the frames' own timing metadata.
/// </summary>
/// <remarks>
/// <para>
/// The coded bit depth follows the pixel type unless <see cref="BitDepth"/> is set: 8 bits for 8-bit formats,
/// 10 bits for the wide formats (<see cref="Rgba64"/>, <see cref="Rgb48"/>, <see cref="L16"/>, <see cref="La32"/>,
/// <see cref="RgbaVector"/>), which are handed to libavif as 16-bit samples. Grey formats default to 4:0:0, all
/// others to 4:4:4. An alpha plane is written only when some pixel of some frame is not fully opaque.
/// </para>
/// <para>
/// <see cref="Lossless"/> stores 8-bit RGB exactly (identity matrix, full range, 4:4:4, quality 100). Lossless
/// output of wide pixel types is exact only at the precision of the chosen bit depth. Encoding uses
/// <see cref="Configuration.Default"/>'s degree of parallelism unless <see cref="MaxThreads"/> is set; AV1
/// encoders may produce different (equally valid) bytes for different thread counts.
/// </para>
/// </remarks>
public sealed class AvifEncoder : IImageEncoder
{
    /// <summary>The largest frame dimension an AV1 sequence header can describe.</summary>
    private const int MaxDimension = 65536;
    private const int DefaultFrameDurationMilliseconds = 100;

    private readonly int quality = 60;
    private readonly int alphaQuality = 100;
    private readonly int speed = 6;
    private readonly int? bitDepth;
    private readonly int? maxThreads;
    private readonly TimeSpan? frameDuration;
    private readonly int? repetitionCount;

    /// <summary>Colour quality from 0 (smallest) to 100 (lossless quantizer). Defaults to 60, the libavif command-line default.</summary>
    public int Quality
    {
        get => this.quality;
        init => this.quality = value is >= 0 and <= 100 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Quality must be between 0 and 100.");
    }

    /// <summary>Alpha quality from 0 to 100. Defaults to 100 (lossless alpha).</summary>
    public int AlphaQuality
    {
        get => this.alphaQuality;
        init => this.alphaQuality = value is >= 0 and <= 100 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "AlphaQuality must be between 0 and 100.");
    }

    /// <summary>Encoder speed from 0 (slowest, smallest) to 10 (fastest). Defaults to 6.</summary>
    public int Speed
    {
        get => this.speed;
        init => this.speed = value is >= 0 and <= 10 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Speed must be between 0 and 10.");
    }

    /// <summary>
    /// The chroma layout. When <see langword="null"/> (the default) grey pixel types use 4:0:0 and every other type 4:4:4.
    /// </summary>
    public AvifChromaSubsampling? ChromaSubsampling { get; init; }

    /// <summary>
    /// The coded bit depth: 8, 10 or 12. When <see langword="null"/> (the default) it follows the pixel type: 8 for
    /// 8-bit formats and 10 for wider ones.
    /// </summary>
    public int? BitDepth
    {
        get => this.bitDepth;
        init => this.bitDepth = value is null or 8 or 10 or 12 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "BitDepth must be 8, 10 or 12.");
    }

    /// <summary>
    /// Stores RGB exactly: quality 100 for colour and alpha, the identity matrix and full range. Requires 4:4:4
    /// (the default) or an explicit 4:0:0 for grey content.
    /// </summary>
    public bool Lossless { get; init; }

    /// <summary>When true, the ICC, EXIF and XMP profiles on the image are not written. Defaults to false.</summary>
    public bool SkipMetadata { get; init; }

    /// <summary>The number of AV1 encoder threads (1 to 64). Defaults to <see cref="Configuration.Default"/>'s degree of parallelism.</summary>
    public int? MaxThreads
    {
        get => this.maxThreads;
        init => this.maxThreads = value is null or (>= 1 and <= 64) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "MaxThreads must be between 1 and 64.");
    }

    /// <summary>
    /// The duration of every frame of an image sequence. When <see langword="null"/> each frame uses its own
    /// <see cref="AvifFrameMetadata"/>, WebP, GIF or APNG frame delay, falling back to 100 ms.
    /// </summary>
    public TimeSpan? FrameDuration
    {
        get => this.frameDuration;
        init => this.frameDuration = value is null || (value > TimeSpan.Zero && value <= TimeSpan.FromDays(1)) ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "FrameDuration must be positive and at most one day.");
    }

    /// <summary>
    /// How many times an image sequence repeats after its first playback; -1 repeats forever. When
    /// <see langword="null"/> the image's <see cref="AvifMetadata.RepetitionCount"/> is used, falling back to -1.
    /// </summary>
    public int? RepetitionCount
    {
        get => this.repetitionCount;
        init => this.repetitionCount = value is null or >= -1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "RepetitionCount must be -1 (forever) or non-negative.");
    }

    /// <inheritdoc/>
    public unsafe void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);
        int width = image.Width, height = image.Height;
        if (width > MaxDimension || height > MaxDimension)
        {
            throw new NotSupportedException($"AVIF cannot store a {width}x{height} frame; both dimensions must be at most {MaxDimension}.");
        }

        foreach (ImageFrame<TPixel> candidate in image.Frames)
        {
            if (candidate.Width != width || candidate.Height != height)
            {
                throw new NotSupportedException("Every frame of an AVIF image sequence must have the root frame's size.");
            }
        }

        NativeAvif.EnsureAvailable();
        if (NativeAvif.CodecName(0, NativeAvif.CodecFlagCanEncode) == null)
        {
            throw new NotSupportedException("The loaded libavif was built without an AV1 encoder.");
        }

        AvifChromaSubsampling chroma = this.ResolveChroma<TPixel>();
        int depth = this.bitDepth ?? (PixelRows.IsHighPrecision<TPixel>() ? 10 : 8);
        bool wideInput = PixelRows.IsHighPrecision<TPixel>();
        bool hasAlpha = !PixelRows.IsAlwaysOpaque<TPixel>() && HasTranslucentPixel(image, wideInput);
        int frameCount = image.Frames.Count;
        (ulong timescale, ulong[] durations) = this.ResolveTiming(image);

        AvifEncoderNative* encoder = NativeAvif.EncoderCreate();
        if (encoder == null)
        {
            throw new InsufficientMemoryException("libavif could not allocate an encoder.");
        }

        AvifRWData output = default;
        try
        {
            encoder->MaxThreads = this.maxThreads ?? Math.Clamp(Configuration.Default.MaxDegreeOfParallelism, 1, 64);
            encoder->Speed = this.speed;
            encoder->Quality = this.Lossless ? 100 : this.quality;
            encoder->QualityAlpha = this.Lossless ? 100 : this.alphaQuality;
            encoder->Timescale = timescale;
            encoder->RepetitionCount = this.repetitionCount
                ?? (image.Metadata.TryGetFormatMetadata(out AvifMetadata? avif) && avif.RepetitionCount >= -1 ? avif.RepetitionCount : NativeAvif.RepetitionCountInfinite);

            for (int index = 0; index < frameCount; index++)
            {
                AvifImage* frame = this.CreateFrame(image, index, chroma, depth, wideInput, hasAlpha);
                try
                {
                    uint flags = frameCount == 1 ? NativeAvif.AddImageFlagSingle : 0;
                    Check(NativeAvif.EncoderAddImage(encoder, frame, durations[index], flags), encoder, "adding a frame");
                }
                finally
                {
                    NativeAvif.ImageDestroy(frame);
                }
            }

            Check(NativeAvif.EncoderFinish(encoder, &output), encoder, "finishing the file");
            stream.Write(new ReadOnlySpan<byte>(output.Data, checked((int)output.Size)));
        }
        finally
        {
            NativeAvif.RWDataFree(&output);
            NativeAvif.EncoderDestroy(encoder);
        }
    }

    private AvifChromaSubsampling ResolveChroma<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
    {
        AvifChromaSubsampling chroma = this.ChromaSubsampling ?? (PixelRows.IsGray<TPixel>() ? AvifChromaSubsampling.Yuv400 : AvifChromaSubsampling.Yuv444);
        if (!Enum.IsDefined(chroma))
        {
            throw new ArgumentOutOfRangeException(nameof(this.ChromaSubsampling), chroma, "Unknown chroma subsampling.");
        }

        if (this.Lossless && chroma is not (AvifChromaSubsampling.Yuv444 or AvifChromaSubsampling.Yuv400))
        {
            throw new NotSupportedException("Lossless AVIF requires 4:4:4 chroma (or 4:0:0 for grey content).");
        }

        return chroma;
    }

    private unsafe AvifImage* CreateFrame<TPixel>(Image<TPixel> image, int index, AvifChromaSubsampling chroma, int depth, bool wideInput, bool hasAlpha)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ImageFrame<TPixel> source = image.Frames[index];
        AvifImage* frame = NativeAvif.ImageCreate((uint)source.Width, (uint)source.Height, (uint)depth, (int)chroma);
        if (frame == null)
        {
            throw new InsufficientMemoryException("libavif could not allocate a frame.");
        }

        try
        {
            frame->YuvRange = NativeAvif.RangeFull;
            frame->ColorPrimaries = 1;
            frame->TransferCharacteristics = 13;
            frame->MatrixCoefficients = (ushort)(this.Lossless && chroma == AvifChromaSubsampling.Yuv444 ? 0 : 6);
            if (index == 0 && !this.SkipMetadata)
            {
                this.WriteMetadata(frame, image.Metadata);
            }

            int rowBytes = checked(source.Width * (wideInput ? 8 : 4));
            nuint size = checked((nuint)rowBytes * (nuint)source.Height);
            byte* pixels = (byte*)NativeMemory.Alloc(size);
            try
            {
                for (int y = 0; y < source.Height; y++)
                {
                    var row = new Span<byte>(pixels + ((nuint)y * (nuint)rowBytes), rowBytes);
                    _ = wideInput ? PixelRows.ToRgba16(source.GetRowSpan(y), row) : PixelRows.ToRgba8(source.GetRowSpan(y), row);
                }

                AvifRgbImage rgb;
                NativeAvif.RgbImageSetDefaults(&rgb, frame);
                rgb.Format = NativeAvif.RgbFormatRgba;
                rgb.Depth = wideInput ? 16u : 8u;
                rgb.IgnoreAlpha = hasAlpha ? 0 : 1;
                rgb.AlphaPremultiplied = 0;
                rgb.IsFloat = 0;
                rgb.Pixels = pixels;
                rgb.RowBytes = (uint)rowBytes;
                int result = NativeAvif.ImageRgbToYuv(frame, &rgb);
                if (result != NativeAvif.ResultOk)
                {
                    throw MapEncode(result, "converting RGB to YUV", string.Empty);
                }
            }
            finally
            {
                NativeMemory.Free(pixels);
            }

            return frame;
        }
        catch
        {
            NativeAvif.ImageDestroy(frame);
            throw;
        }
    }

    private unsafe void WriteMetadata(AvifImage* frame, ImageMetadata metadata)
    {
        if (metadata.IccProfile is { } icc)
        {
            byte[] bytes = icc.ToByteArray();
            fixed (byte* pointer = bytes)
            {
                CheckMetadata(NativeAvif.ImageSetProfileIcc(frame, pointer, (nuint)bytes.Length), "ICC profile");
            }

            // An ICC profile takes precedence over CICP; leave the code points unspecified as libavif's own tools do.
            frame->ColorPrimaries = 2;
            frame->TransferCharacteristics = 2;
        }

        if (metadata.ExifProfile is { } exif && exif.Values.Count + (exif.Thumbnail is null ? 0 : 1) > 0)
        {
            byte[] bytes = exif.ToByteArray();
            fixed (byte* pointer = bytes)
            {
                CheckMetadata(NativeAvif.ImageSetMetadataExif(frame, pointer, (nuint)bytes.Length), "EXIF profile");
            }
        }

        if (metadata.XmpProfile is { } xmp && xmp.Length > 0)
        {
            byte[] bytes = xmp.ToByteArray();
            fixed (byte* pointer = bytes)
            {
                CheckMetadata(NativeAvif.ImageSetMetadataXmp(frame, pointer, (nuint)bytes.Length), "XMP packet");
            }
        }
    }

    /// <summary>Chooses the timescale and per-frame durations, reusing exact AVIF timing when every frame carries it.</summary>
    private (ulong Timescale, ulong[] Durations) ResolveTiming<TPixel>(Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int count = image.Frames.Count;
        var durations = new ulong[count];
        if (this.frameDuration is null && count > 1
            && image.Metadata.TryGetFormatMetadata(out AvifMetadata? avif) && avif.Timescale > 0)
        {
            bool exact = true;
            for (int i = 0; i < count && exact; i++)
            {
                exact = image.Frames[i].Metadata.TryGetFormatMetadata(out AvifFrameMetadata? frame) && frame.DurationInTimescales > 0;
                durations[i] = exact ? frame!.DurationInTimescales : 0;
            }

            if (exact)
            {
                return (avif.Timescale, durations);
            }
        }

        for (int i = 0; i < count; i++)
        {
            durations[i] = (ulong)Math.Max(1, Math.Round(this.frameDuration?.TotalMilliseconds ?? FrameDurationMilliseconds(image.Frames[i].Metadata)));
        }

        return (1000, durations);
    }

    private static double FrameDurationMilliseconds(ImageFrameMetadata metadata)
    {
        if (metadata.TryGetFormatMetadata(out AvifFrameMetadata? avif) && avif.Duration > TimeSpan.Zero)
        {
            return avif.Duration.TotalMilliseconds;
        }

        if (metadata.TryGetFormatMetadata(out WebpFrameMetadata? webp) && webp.FrameDelay > 0)
        {
            return webp.FrameDelay;
        }

        if (metadata.TryGetFormatMetadata(out GifFrameMetadata? gif) && gif.FrameDelay > 0)
        {
            return gif.FrameDelay * 10.0;
        }

        if (metadata.TryGetFormatMetadata(out PngFrameMetadata? png) && png.FrameDelay.Numerator > 0)
        {
            double denominator = png.FrameDelay.Denominator == 0 ? 100 : png.FrameDelay.Denominator;
            return png.FrameDelay.Numerator * 1000.0 / denominator;
        }

        return DefaultFrameDurationMilliseconds;
    }

    private static bool HasTranslucentPixel<TPixel>(Image<TPixel> image, bool wide)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        foreach (ImageFrame<TPixel> frame in image.Frames)
        {
            for (int y = 0; y < frame.Height; y++)
            {
                foreach (TPixel pixel in frame.GetRowSpan(y))
                {
                    if (wide ? Rgba64.FromScaledVector4(pixel.ToScaledVector4()).A != ushort.MaxValue : pixel.ToRgba32().A != byte.MaxValue)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static void CheckMetadata(int result, string what)
    {
        if (result != NativeAvif.ResultOk)
        {
            throw MapEncode(result, $"attaching the {what}", string.Empty);
        }
    }

    private static unsafe void Check(int result, AvifEncoderNative* encoder, string context)
    {
        if (result != NativeAvif.ResultOk)
        {
            throw MapEncode(result, context, NativeAvif.Diagnostic(encoder->Diagnostics));
        }
    }

    private static Exception MapEncode(int result, string context, string diagnostic)
    {
        string message = $"AVIF encoding failed while {context}: {NativeAvif.ResultName(result)}" + (diagnostic.Length > 0 ? $" ({diagnostic})" : string.Empty);
        return result switch
        {
            NativeAvif.ResultUnsupportedDepth or NativeAvif.ResultNoCodecAvailable or NativeAvif.ResultNotImplemented => new NotSupportedException(message),
            NativeAvif.ResultInvalidExifPayload or NativeAvif.ResultInvalidArgument => new ArgumentException(message),
            NativeAvif.ResultOutOfMemory => new InsufficientMemoryException(message),
            _ => new InvalidOperationException(message),
        };
    }
}
