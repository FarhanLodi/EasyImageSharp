using System.Buffers.Binary;
using System.Runtime.InteropServices;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Webp;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.JpegXl;

/// <summary>
/// Encodes images as JPEG XL through libjxl: lossy (VarDCT, controlled by the Butteraugli <see cref="Distance"/>)
/// or mathematically <see cref="Lossless"/>, at a chosen <see cref="Effort"/>. Several frames become an animation
/// whose durations come from <see cref="FrameDuration"/> or the frames' own timing metadata.
/// </summary>
/// <remarks>
/// <para>
/// The stored sample format follows the pixel type unless <see cref="BitsPerSample"/> is set: 8-bit integer for
/// 8-bit formats, 16-bit integer for <see cref="Rgba64"/>, <see cref="Rgb48"/>, <see cref="L16"/> and
/// <see cref="La32"/>, and 32-bit float for <see cref="RgbaVector"/>. Grey formats are stored with one colour
/// channel. An alpha channel is written only when some pixel of some frame is not fully opaque.
/// </para>
/// <para>
/// The image's ICC profile is embedded when its colour space matches the stored channels ('RGB ' or 'GRAY');
/// otherwise the pixels are tagged as sRGB. EXIF and XMP go into 'Exif' and 'xml ' container boxes. Encoding uses
/// <see cref="Configuration.Default"/>'s degree of parallelism (through libjxl_threads, when present) unless
/// <see cref="MaxThreads"/> is set.
/// </para>
/// </remarks>
public sealed class JpegXlEncoder : IImageEncoder
{
    private const int DefaultFrameDurationMilliseconds = 100;
    private const int OutputChunkBytes = 1 << 20;

    private readonly float distance = 1f;
    private readonly int effort = 7;
    private readonly int? bitsPerSample;
    private readonly int? maxThreads;
    private readonly TimeSpan? frameDuration;
    private readonly int? loopCount;

    /// <summary>
    /// The Butteraugli distance of lossy output, from 0.1 to 25: 1 is visually lossless (the default), larger values
    /// are smaller and lossier. Ignored when <see cref="Lossless"/> is set.
    /// </summary>
    public float Distance
    {
        get => this.distance;
        init => this.distance = value is >= 0.1f and <= 25f ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Distance must be between 0.1 and 25.");
    }

    /// <summary>Encoder effort from 1 (fastest) to 10 (slowest, smallest). Defaults to 7, the libjxl default.</summary>
    public int Effort
    {
        get => this.effort;
        init => this.effort = value is >= 1 and <= 10 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Effort must be between 1 and 10.");
    }

    /// <summary>Stores the samples exactly (modular mode in the original colour space). Defaults to false.</summary>
    public bool Lossless { get; init; }

    /// <summary>
    /// The integer bit depth to store, 1 to 16. When <see langword="null"/> (the default) it follows the pixel type:
    /// 8 for 8-bit formats, 16 for 16-bit formats and 32-bit float for <see cref="RgbaVector"/>.
    /// </summary>
    public int? BitsPerSample
    {
        get => this.bitsPerSample;
        init => this.bitsPerSample = value is null or (>= 1 and <= 16) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "BitsPerSample must be between 1 and 16.");
    }

    /// <summary>When true, the ICC, EXIF and XMP profiles on the image are not written. Defaults to false.</summary>
    public bool SkipMetadata { get; init; }

    /// <summary>When true, EXIF and XMP boxes are Brotli-compressed ('brob'). Defaults to false for wider reader support.</summary>
    public bool CompressMetadataBoxes { get; init; }

    /// <summary>The number of libjxl worker threads (1 to 64). Defaults to <see cref="Configuration.Default"/>'s degree of parallelism.</summary>
    public int? MaxThreads
    {
        get => this.maxThreads;
        init => this.maxThreads = value is null or (>= 1 and <= 64) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "MaxThreads must be between 1 and 64.");
    }

    /// <summary>
    /// The duration of every animation frame. When <see langword="null"/> each frame uses its own
    /// <see cref="JpegXlFrameMetadata"/>, WebP, GIF or APNG frame delay, falling back to 100 ms.
    /// </summary>
    public TimeSpan? FrameDuration
    {
        get => this.frameDuration;
        init => this.frameDuration = value is null || (value > TimeSpan.Zero && value <= TimeSpan.FromDays(1)) ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "FrameDuration must be positive and at most one day.");
    }

    /// <summary>
    /// How many times an animation plays; 0 loops forever. When <see langword="null"/> the image's
    /// <see cref="JpegXlMetadata.LoopCount"/> is used, falling back to 0.
    /// </summary>
    public int? LoopCount
    {
        get => this.loopCount;
        init => this.loopCount = value is null or >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "LoopCount must be non-negative.");
    }

    /// <inheritdoc/>
    public unsafe void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);
        int width = image.Width, height = image.Height;
        foreach (ImageFrame<TPixel> candidate in image.Frames)
        {
            if (candidate.Width != width || candidate.Height != height)
            {
                throw new NotSupportedException("Every frame of a JPEG XL animation must have the root frame's size.");
            }
        }

        NativeJxl.EnsureAvailable();
        (SampleType sampleType, uint bits, uint exponentBits) = this.ResolveSampleFormat<TPixel>();
        IccProfile? icc = this.SkipMetadata ? null : image.Metadata.IccProfile;
        int colorChannels = PixelRows.IsGray<TPixel>() && (icc is null || icc.Header.ColorSpace == "GRAY") ? 1 : 3;
        if (icc is not null && icc.Header.ColorSpace != (colorChannels == 1 ? "GRAY" : "RGB "))
        {
            icc = null;
        }

        bool alpha = !PixelRows.IsAlwaysOpaque<TPixel>() && HasTranslucentPixel(image, sampleType);
        int frameCount = image.Frames.Count;
        (uint ticksNumerator, uint ticksDenominator, uint[] durations) = this.ResolveTiming(image);

        IntPtr encoder = NativeJxl.EncoderCreate(null);
        if (encoder == IntPtr.Zero)
        {
            throw new InsufficientMemoryException("libjxl could not allocate an encoder.");
        }

        IntPtr runner = IntPtr.Zero;
        byte* chunk = null;
        try
        {
            int threads = this.maxThreads ?? Math.Clamp(Configuration.Default.MaxDegreeOfParallelism, 1, 64);
            if (threads > 1 && NativeJxl.Threads is { } runnerLibrary)
            {
                runner = runnerLibrary.Create(threads);
                if (runner != IntPtr.Zero)
                {
                    Check(NativeJxl.EncoderSetParallelRunner(encoder, runnerLibrary.Run, runner), encoder, "configuring threads");
                }
            }

            JxlBasicInfo info;
            NativeJxl.EncoderInitBasicInfo(&info);
            info.XSize = (uint)width;
            info.YSize = (uint)height;
            info.BitsPerSample = bits;
            info.ExponentBitsPerSample = exponentBits;
            info.NumColorChannels = (uint)colorChannels;
            info.NumExtraChannels = alpha ? 1u : 0u;
            info.AlphaBits = alpha ? bits : 0;
            info.AlphaExponentBits = alpha ? exponentBits : 0;
            info.UsesOriginalProfile = this.Lossless ? 1 : 0;
            if (frameCount > 1)
            {
                info.HaveAnimation = 1;
                info.TpsNumerator = ticksNumerator;
                info.TpsDenominator = ticksDenominator;
                info.NumLoops = (uint)(this.loopCount
                    ?? (image.Metadata.TryGetFormatMetadata(out JpegXlMetadata? jxl) && jxl.LoopCount >= 0 ? jxl.LoopCount : 0));
            }

            Check(NativeJxl.EncoderSetBasicInfo(encoder, &info), encoder, "setting the basic info");
            this.WriteColor(encoder, icc, colorChannels == 1);

            IntPtr settings = NativeJxl.EncoderFrameSettingsCreate(encoder, IntPtr.Zero);
            if (settings == IntPtr.Zero)
            {
                throw new InsufficientMemoryException("libjxl could not allocate frame settings.");
            }

            Check(NativeJxl.EncoderFrameSettingsSetOption(settings, NativeJxl.FrameSettingEffort, this.effort), encoder, "setting the effort");
            Check(this.Lossless ? NativeJxl.EncoderSetFrameLossless(settings, 1) : NativeJxl.EncoderSetFrameDistance(settings, this.distance),
                encoder, "setting the quality");
            if (!this.SkipMetadata)
            {
                this.WriteBoxes(encoder, image.Metadata);
            }

            int channels = colorChannels + (alpha ? 1 : 0);
            var format = new JxlPixelFormat
            {
                NumChannels = (uint)channels,
                DataType = sampleType switch { SampleType.UInt8 => NativeJxl.TypeUInt8, SampleType.UInt16 => NativeJxl.TypeUInt16, _ => NativeJxl.TypeFloat },
                Endianness = 0,
                Align = 0,
            };
            for (int index = 0; index < frameCount; index++)
            {
                if (frameCount > 1)
                {
                    JxlFrameHeader header;
                    NativeJxl.EncoderInitFrameHeader(&header);
                    header.Duration = durations[index];
                    Check(NativeJxl.EncoderSetFrameHeader(settings, &header), encoder, "setting a frame header");
                }

                AddFrame(encoder, settings, image.Frames[index], sampleType, colorChannels, alpha, &format);
            }

            NativeJxl.EncoderCloseInput(encoder);
            chunk = (byte*)NativeMemory.Alloc(OutputChunkBytes);
            while (true)
            {
                byte* next = chunk;
                nuint available = OutputChunkBytes;
                int status = NativeJxl.EncoderProcessOutput(encoder, &next, &available);
                stream.Write(new ReadOnlySpan<byte>(chunk, (int)(OutputChunkBytes - available)));
                if (status == NativeJxl.EncSuccess)
                {
                    break;
                }

                if (status != NativeJxl.EncNeedMoreOutput)
                {
                    throw Failure(encoder, "writing the codestream");
                }
            }
        }
        finally
        {
            NativeMemory.Free(chunk);
            NativeJxl.EncoderDestroy(encoder);
            if (runner != IntPtr.Zero)
            {
                NativeJxl.Threads?.Destroy(runner);
            }
        }
    }

    private (SampleType Type, uint Bits, uint ExponentBits) ResolveSampleFormat<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (this.bitsPerSample is int bits)
        {
            return (bits <= 8 ? SampleType.UInt8 : SampleType.UInt16, (uint)bits, 0);
        }

        if (typeof(TPixel) == typeof(RgbaVector))
        {
            return (SampleType.Float32, 32, 8);
        }

        return PixelRows.IsHighPrecision<TPixel>() ? (SampleType.UInt16, 16u, 0u) : (SampleType.UInt8, 8u, 0u);
    }

    private unsafe void WriteColor(IntPtr encoder, IccProfile? icc, bool gray)
    {
        if (icc is not null)
        {
            byte[] bytes = icc.ToByteArray();
            fixed (byte* pointer = bytes)
            {
                Check(NativeJxl.EncoderSetIccProfile(encoder, pointer, (nuint)bytes.Length), encoder, "embedding the ICC profile");
            }

            return;
        }

        JxlColorEncoding encoding;
        NativeJxl.ColorEncodingSetToSrgb(&encoding, gray ? 1 : 0);
        Check(NativeJxl.EncoderSetColorEncoding(encoder, &encoding), encoder, "setting the colour encoding");
    }

    private unsafe void WriteBoxes(IntPtr encoder, ImageMetadata metadata)
    {
        byte[]? exif = metadata.ExifProfile is { } profile && profile.Values.Count + (profile.Thumbnail is null ? 0 : 1) > 0 ? profile.ToByteArray() : null;
        byte[]? xmp = metadata.XmpProfile is { Length: > 0 } packet ? packet.ToByteArray() : null;
        if (exif is null && xmp is null)
        {
            return;
        }

        Check(NativeJxl.EncoderUseBoxes(encoder), encoder, "enabling metadata boxes");
        int compress = this.CompressMetadataBoxes ? 1 : 0;
        if (exif is not null)
        {
            // ISO/IEC 18181-2: a big-endian offset to the TIFF header precedes the EXIF payload.
            byte[] contents = new byte[exif.Length + 4];
            BinaryPrimitives.WriteUInt32BigEndian(contents, 0);
            exif.CopyTo(contents, 4);
            fixed (byte* type = "Exif"u8)
            fixed (byte* pointer = contents)
            {
                Check(NativeJxl.EncoderAddBox(encoder, type, pointer, (nuint)contents.Length, compress), encoder, "adding the EXIF box");
            }
        }

        if (xmp is not null)
        {
            fixed (byte* type = "xml "u8)
            fixed (byte* pointer = xmp)
            {
                Check(NativeJxl.EncoderAddBox(encoder, type, pointer, (nuint)xmp.Length, compress), encoder, "adding the XMP box");
            }
        }

        NativeJxl.EncoderCloseBoxes(encoder);
    }

    private static unsafe void AddFrame<TPixel>(IntPtr encoder, IntPtr settings, ImageFrame<TPixel> frame, SampleType type, int colorChannels, bool alpha, JxlPixelFormat* format)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int rowBytes = checked(frame.Width * (colorChannels + (alpha ? 1 : 0)) * PixelRows.SampleSize(type));
        nuint size = checked((nuint)rowBytes * (nuint)frame.Height);
        byte* buffer = (byte*)NativeMemory.Alloc(size);
        try
        {
            for (int y = 0; y < frame.Height; y++)
            {
                _ = PixelRows.ToInterleaved(frame.GetRowSpan(y), type, colorChannels, alpha, new Span<byte>(buffer + ((nuint)y * (nuint)rowBytes), rowBytes));
            }

            Check(NativeJxl.EncoderAddImageFrame(settings, format, buffer, size), encoder, "adding a frame");
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    /// <summary>Chooses the tick rate and per-frame durations, reusing exact JPEG XL ticks when every frame carries them.</summary>
    private (uint Numerator, uint Denominator, uint[] Durations) ResolveTiming<TPixel>(Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int count = image.Frames.Count;
        var durations = new uint[count];
        if (this.frameDuration is null && count > 1
            && image.Metadata.TryGetFormatMetadata(out JpegXlMetadata? jxl) && jxl.TicksPerSecondNumerator > 0 && jxl.TicksPerSecondDenominator > 0)
        {
            bool exact = true;
            for (int i = 0; i < count && exact; i++)
            {
                exact = image.Frames[i].Metadata.TryGetFormatMetadata(out JpegXlFrameMetadata? frame) && frame.DurationTicks > 0;
                durations[i] = exact ? frame!.DurationTicks : 0;
            }

            if (exact)
            {
                return (jxl.TicksPerSecondNumerator, jxl.TicksPerSecondDenominator, durations);
            }
        }

        for (int i = 0; i < count; i++)
        {
            double milliseconds = this.frameDuration?.TotalMilliseconds ?? FrameDurationMilliseconds(image.Frames[i].Metadata);
            durations[i] = (uint)Math.Clamp(Math.Round(milliseconds), 1, uint.MaxValue);
        }

        return (1000, 1, durations);
    }

    private static double FrameDurationMilliseconds(ImageFrameMetadata metadata)
    {
        if (metadata.TryGetFormatMetadata(out JpegXlFrameMetadata? jxl) && jxl.Duration > TimeSpan.Zero)
        {
            return jxl.Duration.TotalMilliseconds;
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

    private static bool HasTranslucentPixel<TPixel>(Image<TPixel> image, SampleType type)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        foreach (ImageFrame<TPixel> frame in image.Frames)
        {
            for (int y = 0; y < frame.Height; y++)
            {
                foreach (TPixel pixel in frame.GetRowSpan(y))
                {
                    bool translucent = type switch
                    {
                        SampleType.UInt8 => pixel.ToRgba32().A != byte.MaxValue,
                        SampleType.UInt16 => Rgba64.FromScaledVector4(pixel.ToScaledVector4()).A != ushort.MaxValue,
                        _ => pixel.ToScaledVector4().W < 1f,
                    };
                    if (translucent)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static void Check(int status, IntPtr encoder, string what)
    {
        if (status != NativeJxl.EncSuccess)
        {
            throw Failure(encoder, what);
        }
    }

    private static Exception Failure(IntPtr encoder, string what)
    {
        int error = NativeJxl.EncoderGetError(encoder);
        string message = $"JPEG XL encoding failed while {what} (libjxl error 0x{error:X}).";
        return error switch
        {
            NativeJxl.EncErrorNotSupported => new NotSupportedException(message),
            NativeJxl.EncErrorOutOfMemory => new InsufficientMemoryException(message),
            NativeJxl.EncErrorApiUsage => new ArgumentException(message),
            _ => new InvalidOperationException(message),
        };
    }
}
