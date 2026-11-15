using System.Runtime.InteropServices;
using EasyImageSharp.Formats;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Xmp;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Avif;

/// <summary>
/// Decodes AVIF still images and image sequences through libavif. 8-bit files decode through 8-bit RGBA;
/// 10- and 12-bit files decode through 16-bit RGBA so wide pixel types (<see cref="Rgba64"/>,
/// <see cref="RgbaVector"/>, ...) keep the full precision. Alpha is returned un-premultiplied. The embedded ICC,
/// EXIF and XMP payloads populate <see cref="ImageMetadata"/>; AVIF facts and frame timing go to
/// <see cref="AvifMetadata"/> and <see cref="AvifFrameMetadata"/>.
/// </summary>
/// <remarks>
/// <para>
/// The container is parsed first and the declared dimensions are checked against
/// <see cref="DecoderOptions.MaxPixels"/>, <see cref="DecoderOptions.MaxTotalPixels"/> and
/// <see cref="DecoderOptions.MaxFrames"/> before any AV1 payload is decoded or any pixel buffer is allocated.
/// libavif's own sanity limits (16384 x 16384 pixels, 32768 pixels per side) apply as well, also to
/// <see cref="Identify"/>.
/// </para>
/// <para>
/// Malformed files raise <see cref="InvalidImageContentException"/>, files over a limit raise
/// <see cref="ImageSizeLimitExceededException"/>, and a libavif without an AV1 decoder or a missing native
/// library raise <see cref="NotSupportedException"/>. Transformative properties ('irot', 'imir', 'clap') are not
/// applied to the pixels; rotation and mirroring are reported in <see cref="AvifMetadata"/>.
/// </para>
/// </remarks>
public sealed class AvifDecoder : IImageDecoder
{
    private const string FormatName = "AVIF";

    /// <inheritdoc/>
    public unsafe Image<TPixel> Decode<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(options);
        CancellationToken cancellationToken = Prepare(data, options);
        AvifDecoderNative* decoder = CreateDecoder(options, decode: true);
        try
        {
            fixed (byte* input = data)
            {
                Check(NativeAvif.DecoderSetIoMemory(decoder, input, (nuint)data.Length), decoder);
                Check(NativeAvif.DecoderParse(decoder), decoder);
                AvifImage* header = decoder->Image;
                (int width, int height) = Dimensions(header);
                int frames = Math.Min(Math.Max(decoder->ImageCount, 1), options.MaxFrames);
                EnsureWithinLimits(width, height, frames, options);
                ImageMetadata metadata = ReadMetadata(decoder);

                Image<TPixel>? image = null;
                try
                {
                    for (int index = 0; index < frames; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Check(NativeAvif.DecoderNextImage(decoder), decoder);
                        AvifImage* decoded = decoder->Image;
                        if (decoded->Width != (uint)width || decoded->Height != (uint)height)
                        {
                            throw new InvalidImageContentException(
                                $"AVIF frame {index} is {decoded->Width}x{decoded->Height}, but the container declares {width}x{height}.");
                        }

                        ImageFrame<TPixel> frame;
                        if (image is null)
                        {
                            image = new Image<TPixel>(width, height);
                            frame = image.Frames.RootFrame;
                        }
                        else
                        {
                            frame = image.Frames.CreateFrame(width, height);
                        }

                        ConvertToPixels(decoded, frame, options, cancellationToken);
                        if (decoder->ImageCount > 1)
                        {
                            frame.Metadata.SetFormatMetadata(new AvifFrameMetadata
                            {
                                Duration = ToTimeSpan(decoder->ImageTiming.Duration),
                                DurationInTimescales = decoder->ImageTiming.DurationInTimescales,
                            });
                        }
                    }

                    if (image is null)
                    {
                        throw new InvalidImageContentException("The AVIF file contains no image.");
                    }

                    CopyMetadata(metadata, image.Metadata, image.Frames.RootFrame.Metadata);

                    // ImageInfo.Create records the decoded format on the metadata it is given; that is the public
                    // way for a provider to set ImageMetadata.DecodedImageFormat on its result.
                    _ = ImageInfo.Create(width, height, BitsPerPixel(header, decoder->AlphaPresent != 0), image.Frames.Count,
                        AvifFormat.Instance, image.Metadata);
                    return image;
                }
                catch
                {
                    image?.Dispose();
                    throw;
                }
            }
        }
        finally
        {
            NativeAvif.DecoderDestroy(decoder);
        }
    }

    /// <inheritdoc/>
    public unsafe ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = Prepare(data, options);
        AvifDecoderNative* decoder = CreateDecoder(options, decode: false);
        try
        {
            fixed (byte* input = data)
            {
                Check(NativeAvif.DecoderSetIoMemory(decoder, input, (nuint)data.Length), decoder);
                Check(NativeAvif.DecoderParse(decoder), decoder);
                AvifImage* header = decoder->Image;
                (int width, int height) = Dimensions(header);
                ImageMetadata metadata = ReadMetadata(decoder);
                return ImageInfo.Create(width, height, BitsPerPixel(header, decoder->AlphaPresent != 0),
                    Math.Max(decoder->ImageCount, 1), AvifFormat.Instance, metadata);
            }
        }
        finally
        {
            NativeAvif.DecoderDestroy(decoder);
        }
    }

    /// <summary>Validates the request before any native work; returns the request's cancellation token.</summary>
    private static CancellationToken Prepare(ReadOnlySpan<byte> data, DecoderOptions options)
    {
        CancellationToken cancellationToken = options.ExecutionOptions?.CancellationToken ?? default;
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length > options.MaxEncodedBytes)
        {
            throw new ImageSizeLimitExceededException("Encoded image exceeds DecoderOptions.MaxEncodedBytes.");
        }

        if (!AvifFormat.IsAvif(data))
        {
            throw new UnknownImageFormatException("The data does not start with an AVIF file type box ('ftyp' with an 'avif' or 'avis' brand).");
        }

        NativeAvif.EnsureAvailable();
        return cancellationToken;
    }

    private static unsafe AvifDecoderNative* CreateDecoder(DecoderOptions options, bool decode)
    {
        if (decode && NativeAvif.CodecName(0, NativeAvif.CodecFlagCanDecode) == null)
        {
            throw new NotSupportedException("The loaded libavif was built without an AV1 decoder.");
        }

        AvifDecoderNative* decoder = NativeAvif.DecoderCreate();
        if (decoder == null)
        {
            throw new InsufficientMemoryException("libavif could not allocate a decoder.");
        }

        Configuration configuration = options.ExecutionOptions?.Configuration ?? Configuration.Default;
        decoder->MaxThreads = Math.Clamp(configuration.MaxDegreeOfParallelism, 1, 64);
        decoder->IgnoreExif = 0;
        decoder->IgnoreXmp = 0;
        if (decode)
        {
            // Lowering libavif's own limit also makes the AV1 decoder refuse a payload larger than the request
            // allows, even if it disagrees with the container. libavif rejects values above its default.
            decoder->ImageSizeLimit = (uint)Math.Clamp(options.MaxPixels, 1, NativeAvif.DefaultImageSizeLimit);
        }

        return decoder;
    }

    private static unsafe (int Width, int Height) Dimensions(AvifImage* image)
    {
        if (image == null || image->Width == 0 || image->Height == 0 || image->Width > int.MaxValue || image->Height > int.MaxValue)
        {
            throw new InvalidImageContentException("The AVIF file declares invalid image dimensions.");
        }

        return ((int)image->Width, (int)image->Height);
    }

    private static void EnsureWithinLimits(int width, int height, int frames, DecoderOptions options)
    {
        long pixels = (long)width * height;
        if (pixels > options.MaxPixels || pixels > int.MaxValue)
        {
            throw new ImageSizeLimitExceededException(
                $"AVIF image is {width}x{height} ({pixels:N0} pixels), which exceeds the configured limit of {options.MaxPixels:N0} pixels.");
        }

        if (pixels * frames > options.MaxTotalPixels)
        {
            throw new ImageSizeLimitExceededException(
                $"AVIF sequence needs {pixels * frames:N0} pixels across {frames} frames, which exceeds DecoderOptions.MaxTotalPixels "
                + $"({options.MaxTotalPixels:N0}). Raise it or lower DecoderOptions.MaxFrames.");
        }
    }

    private static unsafe void ConvertToPixels<TPixel>(AvifImage* decoded, ImageFrame<TPixel> frame, DecoderOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        bool wide = decoded->Depth > 8;
        int rowBytes = checked(frame.Width * (wide ? 8 : 4));
        nuint size = checked((nuint)rowBytes * (nuint)frame.Height);
        byte* pixels = (byte*)NativeMemory.Alloc(size);
        try
        {
            AvifRgbImage rgb;
            NativeAvif.RgbImageSetDefaults(&rgb, decoded);
            rgb.Format = NativeAvif.RgbFormatRgba;
            rgb.Depth = wide ? 16u : 8u;
            rgb.AlphaPremultiplied = 0;
            rgb.IsFloat = 0;
            rgb.MaxThreads = Math.Clamp((options.ExecutionOptions?.Configuration ?? Configuration.Default).MaxDegreeOfParallelism, 1, 64);
            rgb.Pixels = pixels;
            rgb.RowBytes = (uint)rowBytes;
            int result = NativeAvif.ImageYuvToRgb(decoded, &rgb);
            if (result != NativeAvif.ResultOk)
            {
                throw Map(result, "YUV to RGB conversion failed", string.Empty);
            }

            for (int y = 0; y < frame.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = new ReadOnlySpan<byte>(pixels + ((nuint)y * (nuint)rowBytes), rowBytes);
                if (wide)
                {
                    PixelRows.FromRgba16(row, frame.GetRowSpan(y));
                }
                else
                {
                    PixelRows.FromRgba8(row, frame.GetRowSpan(y));
                }
            }
        }
        finally
        {
            NativeMemory.Free(pixels);
        }
    }

    private static unsafe ImageMetadata ReadMetadata(AvifDecoderNative* decoder)
    {
        AvifImage* image = decoder->Image;
        var metadata = new ImageMetadata();
        if (image->Icc.Size > 0)
        {
            metadata.IccProfile = new IccProfile(Copy(image->Icc));
        }

        if (image->Exif.Size > 0)
        {
            nuint offset;
            if (NativeAvif.GetExifTiffHeaderOffset(image->Exif.Data, image->Exif.Size, &offset) == NativeAvif.ResultOk && offset < image->Exif.Size)
            {
                byte[] tiff = new ReadOnlySpan<byte>(image->Exif.Data + offset, checked((int)(image->Exif.Size - offset))).ToArray();
                metadata.ExifProfile = new ExifProfile(tiff);
            }
        }

        if (image->Xmp.Size > 0)
        {
            metadata.XmpProfile = new XmpProfile(Copy(image->Xmp));
        }

        int format = image->YuvFormat;
        metadata.SetFormatMetadata(new AvifMetadata
        {
            BitDepth = (int)image->Depth,
            ChromaSubsampling = format is >= NativeAvif.PixelFormatYuv444 and <= NativeAvif.PixelFormatYuv400 ? (AvifChromaSubsampling)format : null,
            FullRange = image->YuvRange == NativeAvif.RangeFull,
            HasAlpha = decoder->AlphaPresent != 0,
            AlphaPremultiplied = image->AlphaPremultiplied != 0,
            ColorPrimaries = image->ColorPrimaries,
            TransferCharacteristics = image->TransferCharacteristics,
            MatrixCoefficients = image->MatrixCoefficients,
            IsAnimated = decoder->ImageCount > 1,
            RepetitionCount = decoder->ImageCount > 1 ? decoder->RepetitionCount : 0,
            Timescale = decoder->ImageCount > 1 ? decoder->Timescale : 0,
            RotationDegrees = (image->TransformFlags & NativeAvif.TransformIrot) != 0 ? (image->IrotAngle & 3) * 90 : 0,
            MirrorAxis = (image->TransformFlags & NativeAvif.TransformImir) != 0 ? image->ImirAxis & 1 : null,
        });
        return metadata;
    }

    private static void CopyMetadata(ImageMetadata source, ImageMetadata image, ImageFrameMetadata rootFrame)
    {
        image.IccProfile = source.IccProfile;
        image.ExifProfile = source.ExifProfile;
        image.XmpProfile = source.XmpProfile;
        rootFrame.IccProfile = source.IccProfile?.DeepClone();
        rootFrame.ExifProfile = source.ExifProfile?.DeepClone();
        rootFrame.XmpProfile = source.XmpProfile?.DeepClone();
        if (source.TryGetFormatMetadata(out AvifMetadata? avif))
        {
            image.SetFormatMetadata(avif);
        }
    }

    private static unsafe byte[] Copy(AvifRWData data) => new ReadOnlySpan<byte>(data.Data, checked((int)data.Size)).ToArray();

    private static unsafe int BitsPerPixel(AvifImage* image, bool alpha)
    {
        int colorChannels = image->YuvFormat == NativeAvif.PixelFormatYuv400 ? 1 : 3;
        return (int)Math.Max(image->Depth, 1) * (colorChannels + (alpha ? 1 : 0));
    }

    private static TimeSpan ToTimeSpan(double seconds)
        => double.IsFinite(seconds) && seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;

    private static unsafe void Check(int result, AvifDecoderNative* decoder)
    {
        if (result != NativeAvif.ResultOk)
        {
            throw Map(result, "decoding failed", NativeAvif.Diagnostic(decoder->Diagnostics));
        }
    }

    /// <summary>Maps an <c>avifResult</c> onto the library's closed decoder exception contract.</summary>
    internal static Exception Map(int result, string context, string diagnostic)
    {
        string message = $"{FormatName} {context}: {NativeAvif.ResultName(result)}" + (diagnostic.Length > 0 ? $" ({diagnostic})" : string.Empty);
        return result switch
        {
            NativeAvif.ResultInvalidFtyp => new UnknownImageFormatException(message),
            NativeAvif.ResultBmffParseFailed when diagnostic.Contains("too large", StringComparison.OrdinalIgnoreCase)
                => new ImageSizeLimitExceededException(message),
            NativeAvif.ResultUnsupportedDepth or NativeAvif.ResultNoCodecAvailable or NativeAvif.ResultNotImplemented
                => new NotSupportedException(message),
            _ => new InvalidImageContentException(message),
        };
    }
}
