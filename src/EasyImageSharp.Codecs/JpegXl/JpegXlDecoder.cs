using System.Buffers.Binary;
using System.Runtime.InteropServices;
using EasyImageSharp.Formats;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Xmp;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.JpegXl;

/// <summary>
/// Decodes JPEG XL codestreams and containers through libjxl's event-driven decoder. Images with up to 8 bits per
/// sample decode through 8-bit RGBA, deeper integer images through 16-bit RGBA and floating-point images through
/// 32-bit float RGBA, so wide pixel types keep the precision. Animations are returned coalesced: every frame is
/// the full canvas a viewer would show, with its duration in <see cref="JpegXlFrameMetadata"/>.
/// </summary>
/// <remarks>
/// <para>
/// The basic info is read first and the dimensions are checked against <see cref="DecoderOptions.MaxPixels"/>;
/// each frame is charged to <see cref="DecoderOptions.MaxTotalPixels"/> before its pixel buffer exists, and frames
/// past <see cref="DecoderOptions.MaxFrames"/> are skipped. libjxl applies the codestream orientation, so pixels
/// and dimensions are already upright. Alpha is returned un-premultiplied.
/// </para>
/// <para>
/// The colour profile of the returned pixels is attached as <see cref="ImageMetadata.IccProfile"/> unless it is
/// plain sRGB (grey or colour), which is left implicit like every other format. 'Exif' and 'xml ' boxes
/// (Brotli-compressed 'brob' boxes included) populate the EXIF and XMP profiles; a metadata box over 64 MiB is
/// rejected. Malformed files raise <see cref="InvalidImageContentException"/>, CMYK files (a black extra channel)
/// and a missing native library raise <see cref="NotSupportedException"/>.
/// </para>
/// </remarks>
public sealed class JpegXlDecoder : IImageDecoder
{
    private const string FormatName = "JPEG XL";
    private const long MaxMetadataBoxBytes = 64L * 1024 * 1024;

    /// <inheritdoc/>
    public Image<TPixel> Decode<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(options);
        CancellationToken cancellationToken = Prepare(data, options);
        using var session = new Session(options, cancellationToken);
        session.Run<TPixel>(data, decodePixels: true);
        Image<TPixel> image = session.TakeImage<TPixel>();
        try
        {
            session.ApplyMetadata(image.Metadata, image.Frames.RootFrame.Metadata);

            // ImageInfo.Create records the decoded format on the metadata it is given; that is the public way for
            // a provider to set ImageMetadata.DecodedImageFormat on its result.
            _ = ImageInfo.Create(image.Width, image.Height, session.BitsPerPixel, image.Frames.Count, JpegXlFormat.Instance, image.Metadata);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        CancellationToken cancellationToken = Prepare(data, options);
        using var session = new Session(options, cancellationToken);
        session.Run<Rgba32>(data, decodePixels: false);
        if (session.Width == 0)
        {
            throw new InvalidImageContentException("The JPEG XL file ended before its basic info.");
        }

        var metadata = new ImageMetadata();
        session.ApplyMetadata(metadata, null);
        return ImageInfo.Create(session.Width, session.Height, session.BitsPerPixel, Math.Max(session.FrameCount, 1), JpegXlFormat.Instance, metadata);
    }

    private static CancellationToken Prepare(ReadOnlySpan<byte> data, DecoderOptions options)
    {
        CancellationToken cancellationToken = options.ExecutionOptions?.CancellationToken ?? default;
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length > options.MaxEncodedBytes)
        {
            throw new ImageSizeLimitExceededException("Encoded image exceeds DecoderOptions.MaxEncodedBytes.");
        }

        if (!JpegXlFormat.IsJpegXl(data))
        {
            throw new UnknownImageFormatException("The data does not start with a JPEG XL codestream or container signature.");
        }

        NativeJxl.EnsureAvailable();
        return cancellationToken;
    }

    /// <summary>One run of the libjxl event loop, owning every native resource it creates.</summary>
    private sealed unsafe class Session : IDisposable
    {
        private readonly DecoderOptions options;
        private readonly CancellationToken cancellationToken;
        private readonly IntPtr decoder;
        private readonly IntPtr runner;
        private JxlBasicInfo info;
        private SampleType sampleType;
        private byte* pixels;
        private nuint pixelBytes;
        private byte* box;
        private nuint boxCapacity;
        private int boxKind;
        private long chargedPixels;
        private bool skipCurrentFrame;
        private Image? image;
        private IccProfile? icc;
        private byte[]? exif;
        private byte[]? xmp;
        private TimeSpan pendingDuration;
        private uint pendingTicks;

        internal Session(DecoderOptions options, CancellationToken cancellationToken)
        {
            this.options = options;
            this.cancellationToken = cancellationToken;
            this.decoder = NativeJxl.DecoderCreate(null);
            if (this.decoder == IntPtr.Zero)
            {
                throw new InsufficientMemoryException("libjxl could not allocate a decoder.");
            }

            int threads = Math.Clamp((options.ExecutionOptions?.Configuration ?? Configuration.Default).MaxDegreeOfParallelism, 1, 64);
            if (threads > 1 && NativeJxl.Threads is { } runnerLibrary)
            {
                this.runner = runnerLibrary.Create(threads);
                if (this.runner != IntPtr.Zero
                    && NativeJxl.DecoderSetParallelRunner(this.decoder, runnerLibrary.Run, this.runner) != NativeJxl.DecSuccess)
                {
                    // Single-threaded decoding is always available; a runner that cannot be attached is not an error.
                    runnerLibrary.Destroy(this.runner);
                    this.runner = IntPtr.Zero;
                }
            }
        }

        internal int Width => (int)this.info.XSize;

        internal int Height => (int)this.info.YSize;

        internal int FrameCount { get; private set; }

        internal int BitsPerPixel
        {
            get
            {
                int channels = (int)Math.Max(this.info.NumColorChannels, 1) + (this.info.AlphaBits > 0 ? 1 : 0);
                return (int)Math.Max(this.info.BitsPerSample, 1) * channels;
            }
        }

        internal void Run<TPixel>(ReadOnlySpan<byte> data, bool decodePixels)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            int events = NativeJxl.DecBasicInfo | NativeJxl.DecColorEncoding | NativeJxl.DecFrame | NativeJxl.DecBox
                | (decodePixels ? NativeJxl.DecFullImage : 0);
            this.Check(NativeJxl.DecoderSubscribeEvents(this.decoder, events), "subscribing to events");
            this.Check(NativeJxl.DecoderSetUnpremultiplyAlpha(this.decoder, 1), "configuring alpha");

            // Decompressing 'brob' boxes needs Brotli inside libjxl; without it they are skipped, not fatal.
            _ = NativeJxl.DecoderSetDecompressBoxes(this.decoder, 1);
            fixed (byte* input = data)
            {
                this.Check(NativeJxl.DecoderSetInput(this.decoder, input, (nuint)data.Length), "setting the input");
                NativeJxl.DecoderCloseInput(this.decoder);
                while (true)
                {
                    this.cancellationToken.ThrowIfCancellationRequested();
                    int status = NativeJxl.DecoderProcessInput(this.decoder);
                    switch (status)
                    {
                        case NativeJxl.DecSuccess:
                            this.FinishBox();
                            return;
                        case NativeJxl.DecError:
                            throw new InvalidImageContentException($"{FormatName} data is malformed or uses an unsupported feature (libjxl reported a decoding error).");
                        case NativeJxl.DecNeedMoreInput:
                            throw new InvalidImageContentException($"{FormatName} data is truncated.");
                        case NativeJxl.DecBasicInfo:
                            this.ReadBasicInfo<TPixel>(decodePixels);
                            break;
                        case NativeJxl.DecColorEncoding:
                            this.ReadColorProfile();
                            break;
                        case NativeJxl.DecFrame:
                            this.BeginFrame(decodePixels);
                            break;
                        case NativeJxl.DecNeedImageOutBuffer:
                            if (this.skipCurrentFrame)
                            {
                                // libjxl accepts a skip only once frame processing has begun, which is signalled by
                                // this event; at the frame header it reports "called at the wrong time". Keep running
                                // so metadata boxes stored after the codestream are still read.
                                this.Check(NativeJxl.DecoderSkipCurrentFrame(this.decoder), "skipping a frame");
                            }
                            else
                            {
                                this.SetImageOutBuffer();
                            }

                            break;
                        case NativeJxl.DecFullImage:
                            this.EndFrame<TPixel>();
                            break;
                        case NativeJxl.DecBox:
                            this.BeginBox();
                            break;
                        case NativeJxl.DecBoxNeedMoreOutput:
                            this.GrowBox();
                            break;
                        default:
                            throw new InvalidImageContentException($"{FormatName} decoding stopped with unexpected libjxl status {status}.");
                    }
                }
            }
        }

        internal Image<TPixel> TakeImage<TPixel>()
            where TPixel : unmanaged, IPixel<TPixel>
        {
            if (this.image is not Image<TPixel> result)
            {
                throw new InvalidImageContentException($"The {FormatName} file contains no displayable frame.");
            }

            this.image = null;
            return result;
        }

        internal void ApplyMetadata(ImageMetadata metadata, ImageFrameMetadata? rootFrame)
        {
            metadata.IccProfile = this.icc?.DeepClone();
            metadata.ExifProfile = this.exif is null ? null : new ExifProfile(this.exif);
            metadata.XmpProfile = this.xmp is null ? null : new XmpProfile(this.xmp);
            if (rootFrame is not null)
            {
                rootFrame.IccProfile = metadata.IccProfile?.DeepClone();
                rootFrame.ExifProfile = metadata.ExifProfile?.DeepClone();
                rootFrame.XmpProfile = metadata.XmpProfile?.DeepClone();
            }

            metadata.SetFormatMetadata(new JpegXlMetadata
            {
                BitsPerSample = (int)this.info.BitsPerSample,
                ExponentBitsPerSample = (int)this.info.ExponentBitsPerSample,
                IsGray = this.info.NumColorChannels == 1,
                HasAlpha = this.info.AlphaBits > 0,
                AlphaPremultiplied = this.info.AlphaPremultiplied != 0,
                UsesOriginalProfile = this.info.UsesOriginalProfile != 0,
                HasContainer = this.info.HaveContainer != 0,
                IsAnimated = this.info.HaveAnimation != 0,
                LoopCount = this.info.HaveAnimation != 0 ? (int)Math.Min(this.info.NumLoops, int.MaxValue) : 0,
                TicksPerSecondNumerator = this.info.HaveAnimation != 0 ? this.info.TpsNumerator : 0,
                TicksPerSecondDenominator = this.info.HaveAnimation != 0 ? this.info.TpsDenominator : 0,
                IntensityTarget = this.info.IntensityTarget,
            });
        }

        public void Dispose()
        {
            this.image?.Dispose();
            NativeJxl.DecoderDestroy(this.decoder);
            if (this.runner != IntPtr.Zero)
            {
                NativeJxl.Threads?.Destroy(this.runner);
            }

            NativeMemory.Free(this.pixels);
            NativeMemory.Free(this.box);
            this.pixels = null;
            this.box = null;
        }

        private void ReadBasicInfo<TPixel>(bool decodePixels)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            JxlBasicInfo basic;
            this.Check(NativeJxl.DecoderGetBasicInfo(this.decoder, &basic), "reading the basic info");
            if (basic.XSize == 0 || basic.YSize == 0 || basic.XSize > int.MaxValue || basic.YSize > int.MaxValue)
            {
                throw new InvalidImageContentException($"The {FormatName} file declares invalid dimensions {basic.XSize}x{basic.YSize}.");
            }

            this.info = basic;
            for (uint index = 0; index < basic.NumExtraChannels; index++)
            {
                JxlExtraChannelInfo extra;
                if (NativeJxl.DecoderGetExtraChannelInfo(this.decoder, index, &extra) == NativeJxl.DecSuccess && extra.Type == NativeJxl.ChannelBlack)
                {
                    throw new NotSupportedException($"CMYK {FormatName} images (a black extra channel) are not supported.");
                }
            }

            this.sampleType = basic.ExponentBitsPerSample > 0 ? SampleType.Float32
                : basic.BitsPerSample > 8 || basic.AlphaBits > 8 ? SampleType.UInt16
                : SampleType.UInt8;
            if (decodePixels)
            {
                long pixelCount = (long)basic.XSize * basic.YSize;
                if (pixelCount > this.options.MaxPixels || pixelCount > int.MaxValue)
                {
                    throw new ImageSizeLimitExceededException(
                        $"{FormatName} image is {basic.XSize}x{basic.YSize} ({pixelCount:N0} pixels), which exceeds the configured limit of "
                        + $"{this.options.MaxPixels:N0} pixels.");
                }
            }
        }

        private void ReadColorProfile()
        {
            JxlColorEncoding encoding;
            if (NativeJxl.DecoderGetColorAsEncodedProfile(this.decoder, NativeJxl.ColorProfileTargetData, &encoding) == NativeJxl.DecSuccess
                && IsSrgb(encoding))
            {
                return;
            }

            nuint size;
            if (NativeJxl.DecoderGetIccProfileSize(this.decoder, NativeJxl.ColorProfileTargetData, &size) != NativeJxl.DecSuccess
                || size == 0 || size > (nuint)MaxMetadataBoxBytes)
            {
                return;
            }

            byte[] profile = new byte[(int)size];
            fixed (byte* pointer = profile)
            {
                if (NativeJxl.DecoderGetColorAsIccProfile(this.decoder, NativeJxl.ColorProfileTargetData, pointer, size) == NativeJxl.DecSuccess)
                {
                    this.icc = new IccProfile(profile);
                }
            }
        }

        private static bool IsSrgb(in JxlColorEncoding encoding)
            => encoding.WhitePoint == NativeJxl.WhitePointD65
                && encoding.TransferFunction == NativeJxl.TransferFunctionSrgb
                && (encoding.ColorSpace == NativeJxl.ColorSpaceGray
                    || (encoding.ColorSpace == NativeJxl.ColorSpaceRgb && encoding.Primaries == NativeJxl.PrimariesSrgb));

        private void BeginFrame(bool decodePixels)
        {
            JxlFrameHeader header;
            this.Check(NativeJxl.DecoderGetFrameHeader(this.decoder, &header), "reading a frame header");
            this.FrameCount++;
            this.skipCurrentFrame = decodePixels && this.FrameCount > this.options.MaxFrames;
            if (this.skipCurrentFrame)
            {
                return; // Skipped when libjxl asks for its output buffer; see the event loop.
            }

            if (this.info.HaveAnimation != 0 && this.info.TpsNumerator > 0)
            {
                double seconds = (double)header.Duration * this.info.TpsDenominator / this.info.TpsNumerator;
                this.pendingDuration = double.IsFinite(seconds) && seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
                this.pendingTicks = header.Duration;
            }

            if (decodePixels)
            {
                // Coalesced frames are always the full canvas; charge them before any buffer is allocated.
                this.chargedPixels += (long)this.Width * this.Height;
                if (this.chargedPixels > this.options.MaxTotalPixels)
                {
                    throw new ImageSizeLimitExceededException(
                        $"{FormatName} decode reached {this.chargedPixels:N0} pixels across all frames, which exceeds DecoderOptions.MaxTotalPixels "
                        + $"({this.options.MaxTotalPixels:N0}). Raise it or lower DecoderOptions.MaxFrames.");
                }
            }
        }

        private void SetImageOutBuffer()
        {
            JxlPixelFormat format = this.PixelFormat();
            nuint required;
            this.Check(NativeJxl.DecoderImageOutBufferSize(this.decoder, &format, &required), "sizing the output buffer");
            nuint expected = (nuint)this.Width * (nuint)this.Height * 4 * (nuint)PixelRows.SampleSize(this.sampleType);
            if (required != expected)
            {
                throw new InvalidImageContentException($"libjxl requested an unexpected {required}-byte output buffer for a {this.Width}x{this.Height} frame.");
            }

            if (this.pixels == null)
            {
                this.pixels = (byte*)NativeMemory.Alloc(required);
                this.pixelBytes = required;
            }

            this.Check(NativeJxl.DecoderSetImageOutBuffer(this.decoder, &format, this.pixels, this.pixelBytes), "setting the output buffer");
        }

        private void EndFrame<TPixel>()
            where TPixel : unmanaged, IPixel<TPixel>
        {
            if (this.skipCurrentFrame || this.pixels == null)
            {
                return;
            }

            ImageFrame<TPixel> frame;
            if (this.image is null)
            {
                var created = new Image<TPixel>(this.Width, this.Height);
                this.image = created;
                frame = created.Frames.RootFrame;
            }
            else
            {
                frame = ((Image<TPixel>)this.image).Frames.CreateFrame(this.Width, this.Height);
            }

            int rowBytes = this.Width * 4 * PixelRows.SampleSize(this.sampleType);
            for (int y = 0; y < this.Height; y++)
            {
                this.cancellationToken.ThrowIfCancellationRequested();
                PixelRows.FromRgba(new ReadOnlySpan<byte>(this.pixels + ((nuint)y * (nuint)rowBytes), rowBytes), this.sampleType, frame.GetRowSpan(y));
            }

            if (this.info.HaveAnimation != 0)
            {
                frame.Metadata.SetFormatMetadata(new JpegXlFrameMetadata { Duration = this.pendingDuration, DurationTicks = this.pendingTicks });
            }
        }

        private JxlPixelFormat PixelFormat() => new()
        {
            NumChannels = 4,
            DataType = this.sampleType switch { SampleType.UInt8 => NativeJxl.TypeUInt8, SampleType.UInt16 => NativeJxl.TypeUInt16, _ => NativeJxl.TypeFloat },
            Endianness = 0,
            Align = 0,
        };

        private void BeginBox()
        {
            this.FinishBox();
            byte* type = stackalloc byte[4];
            if (NativeJxl.DecoderGetBoxType(this.decoder, type, 1) != NativeJxl.DecSuccess)
            {
                return;
            }

            var name = new ReadOnlySpan<byte>(type, 4);
            int kind = name.SequenceEqual("Exif"u8) ? 1 : name.SequenceEqual("xml "u8) ? 2 : 0;
            if (kind == 0)
            {
                return;
            }

            ulong raw;
            if (NativeJxl.DecoderGetBoxSizeRaw(this.decoder, &raw) != NativeJxl.DecSuccess)
            {
                raw = 0;
            }

            if (raw > (ulong)MaxMetadataBoxBytes)
            {
                throw new InvalidImageContentException($"A {FormatName} metadata box declares {raw:N0} bytes, more than the {MaxMetadataBoxBytes:N0}-byte limit.");
            }

            this.boxCapacity = (nuint)Math.Max(raw, 256);
            this.box = (byte*)NativeMemory.Alloc(this.boxCapacity);
            this.boxKind = kind;
            this.Check(NativeJxl.DecoderSetBoxBuffer(this.decoder, this.box, this.boxCapacity), "setting a box buffer");
        }

        private void GrowBox()
        {
            if (this.box == null)
            {
                throw new InvalidImageContentException($"libjxl asked for more box output without a box buffer.");
            }

            nuint remaining = NativeJxl.DecoderReleaseBoxBuffer(this.decoder);
            nuint written = this.boxCapacity - remaining;
            nuint capacity = this.boxCapacity * 2;
            if (capacity > (nuint)MaxMetadataBoxBytes)
            {
                throw new InvalidImageContentException($"A {FormatName} metadata box decompresses to more than {MaxMetadataBoxBytes:N0} bytes.");
            }

            byte* grown = (byte*)NativeMemory.Alloc(capacity);
            Buffer.MemoryCopy(this.box, grown, capacity, written);
            NativeMemory.Free(this.box);
            this.box = grown;
            this.boxCapacity = capacity;
            this.Check(NativeJxl.DecoderSetBoxBuffer(this.decoder, this.box + written, capacity - written), "growing a box buffer");
        }

        private void FinishBox()
        {
            if (this.box == null)
            {
                return;
            }

            nuint remaining = NativeJxl.DecoderReleaseBoxBuffer(this.decoder);
            var contents = new ReadOnlySpan<byte>(this.box, (int)(this.boxCapacity - remaining));
            if (this.boxKind == 1 && this.exif is null && contents.Length >= 4)
            {
                // ISO/IEC 18181-2: the Exif box starts with a big-endian offset to the TIFF header.
                uint offset = BinaryPrimitives.ReadUInt32BigEndian(contents);
                if (offset <= (uint)(contents.Length - 4))
                {
                    this.exif = contents[(4 + (int)offset)..].ToArray();
                }
            }
            else if (this.boxKind == 2 && this.xmp is null && contents.Length > 0)
            {
                this.xmp = contents.ToArray();
            }

            NativeMemory.Free(this.box);
            this.box = null;
            this.boxCapacity = 0;
            this.boxKind = 0;
        }

        private void Check(int status, string what)
        {
            if (status != NativeJxl.DecSuccess)
            {
                throw new InvalidImageContentException($"{FormatName} decoding failed while {what}.");
            }
        }
    }
}
