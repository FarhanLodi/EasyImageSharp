using System.Buffers.Binary;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// Decodes WebP images (RFC 9649): the simple lossy ('VP8 ') and lossless ('VP8L') containers, the extended
/// 'VP8X' container with its alpha ('ALPH') and animation ('ANIM'/'ANMF') chunks. Animations are returned
/// fully composited, so <c>image.Frames[i]</c> is exactly what a viewer would display for frame <c>i</c>;
/// the loop count and background colour are exposed through <see cref="WebpMetadata"/>, and per-frame
/// duration, offsets, blending and disposal through <see cref="WebpFrameMetadata"/>.
/// </summary>
/// <remarks>
/// <para>
/// Colour, EXIF and XMP chunks are skipped rather than parsed: they never make a file fail to decode.
/// Frames marked "dispose to background" clear their rectangle to <em>transparent black</em> rather than to
/// the colour advertised in the ANIM chunk; this is what web browsers and the reference decoder do, and the
/// advertised colour remains available as <see cref="WebpMetadata.BackgroundColor"/>.
/// </para>
/// <para>
/// Only VP8 key frames are supported, which is all a still WebP image or an animation frame may contain; an
/// inter frame raises <see cref="NotSupportedException"/>. Truncated or inconsistent files raise
/// <see cref="InvalidImageContentException"/>.
/// </para>
/// </remarks>
public sealed class WebpDecoder : IImageDecoder
{
    /// <inheritdoc/>
    public Image<TPixel> Decode<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(options);
        byte[] buffer = data.ToArray();
        try
        {
            return DecodeCore<TPixel>(buffer, options);
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap("WebP", ex);
        }
    }

    /// <inheritdoc/>
    public ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        byte[] buffer = data.ToArray();
        try
        {
            WebpFile file = Parse(buffer);
            var metadata = new ImageMetadata { DecodedImageFormat = ImageFormat.Webp };
            metadata.SetFormatMetadata(CreateMetadata(file));
            return new ImageInfo(file.Width, file.Height, file.HasAlpha ? 32 : 24, file.Frames.Count, ImageFormat.Webp, metadata);
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap("WebP", ex);
        }
    }

    private static WebpMetadata CreateMetadata(WebpFile file) => new()
    {
        IsLossless = file.Lossless,
        HasAlpha = file.HasAlpha,
        IsAnimated = file.IsAnimated,
        RepeatCount = file.LoopCount,
        BackgroundColor = file.Background,
    };

    private static int ReadLe24(byte[] data, int offset) => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);

    private static Image<TPixel> DecodeCore<TPixel>(byte[] data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        WebpFile file = Parse(data);
        var metadata = new ImageMetadata { DecodedImageFormat = ImageFormat.Webp };
        metadata.SetFormatMetadata(CreateMetadata(file));

        if (!file.IsAnimated)
        {
            WebpFrame only = file.Frames[0];
            Rgba32[] pixels = DecodeFrame(data, only, options, out int width, out int height);
            var single = new List<ImageFrame<TPixel>> { CreateFrame<TPixel>(pixels, width, height) };
            AttachFrameMetadata(single[0], only, width, height);
            return new Image<TPixel>(single, metadata);
        }

        options.EnsureFrameWithinLimits(file.Width, file.Height, "WebP");
        var canvas = new Rgba32[file.Width * file.Height];
        var frames = new List<ImageFrame<TPixel>>();
        int frameCount = Math.Min(file.Frames.Count, options.MaxFrames);
        bool previousDisposed = false;
        for (int i = 0; i < frameCount; i++)
        {
            WebpFrame frame = file.Frames[i];
            Rgba32[] pixels = DecodeFrame(data, frame, options, out int width, out int height);
            if (width != frame.Width || height != frame.Height)
            {
                throw new InvalidImageContentException(
                    $"WebP frame {i} decodes to {width}x{height} but its ANMF header declares {frame.Width}x{frame.Height}.");
            }

            // Blending only has an effect over pixels the previous frame left behind: the first frame and
            // any frame following a dispose-to-background land on a cleared rectangle, and the reference
            // decoder writes them through unchanged rather than blending them against transparent black
            // (which would discard the colour of fully transparent pixels).
            bool blend = frame.Blend && i > 0 && !previousDisposed;
            Compose(canvas, file.Width, file.Height, pixels, frame, blend);
            ImageFrame<TPixel> output = CreateFrame<TPixel>(canvas, file.Width, file.Height);
            AttachFrameMetadata(output, frame, frame.Width, frame.Height);
            frames.Add(output);

            previousDisposed = frame.DisposeToBackground;
            if (previousDisposed)
            {
                ClearRectangle(canvas, file.Width, frame);
            }
        }

        if (frames.Count == 0)
        {
            throw new InvalidImageContentException("WebP animation contains no frames.");
        }

        return new Image<TPixel>(frames, metadata);
    }

    private static void AttachFrameMetadata<TPixel>(ImageFrame<TPixel> frame, WebpFrame source, int width, int height)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        WebpFrameMetadata meta = frame.Metadata.GetFormatMetadata<WebpFrameMetadata>();
        meta.FrameDelay = source.Duration;
        meta.X = source.X;
        meta.Y = source.Y;
        meta.Width = width;
        meta.Height = height;
        meta.BlendMethod = source.Blend ? WebpBlendMethod.AlphaBlend : WebpBlendMethod.DoNotBlend;
        meta.DisposalMethod = source.DisposeToBackground ? WebpDisposalMethod.DisposeToBackground : WebpDisposalMethod.DoNotDispose;
    }

    private static ImageFrame<TPixel> CreateFrame<TPixel>(Rgba32[] pixels, int width, int height)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var frame = new ImageFrame<TPixel>(width, height);
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                PixelOps.FromRgba32(pixels.AsSpan(y * width, width), frame.GetRowSpan(y));
            }
        });

        return frame;
    }

    /// <summary>Draws one decoded sub-frame onto the canvas, alpha-blending it when <paramref name="blend"/> is set.</summary>
    private static void Compose(Rgba32[] canvas, int canvasWidth, int canvasHeight, Rgba32[] pixels, WebpFrame frame, bool blend)
    {
        int x0 = frame.X;
        int y0 = frame.Y;
        if (x0 < 0 || y0 < 0 || x0 + frame.Width > canvasWidth || y0 + frame.Height > canvasHeight)
        {
            throw new InvalidImageContentException("WebP animation frame does not fit inside the canvas.");
        }

        for (int y = 0; y < frame.Height; y++)
        {
            int src = y * frame.Width;
            int dst = ((y0 + y) * canvasWidth) + x0;
            if (!blend)
            {
                pixels.AsSpan(src, frame.Width).CopyTo(canvas.AsSpan(dst, frame.Width));
                continue;
            }

            for (int x = 0; x < frame.Width; x++)
            {
                canvas[dst + x] = Blend(pixels[src + x], canvas[dst + x]);
            }
        }
    }

    /// <summary>
    /// Composites a straight-alpha source pixel over a straight-alpha destination pixel, dividing by the
    /// resulting alpha through the same 24-bit reciprocal the reference decoder uses so the rounding matches.
    /// </summary>
    private static Rgba32 Blend(Rgba32 source, Rgba32 destination)
    {
        if (source.A == 255)
        {
            return source;
        }

        if (source.A == 0)
        {
            return destination;
        }

        int carried = destination.A * (255 - source.A) / 255;
        int alpha = source.A + carried;
        if (alpha == 0)
        {
            return default;
        }

        uint scale = (1u << 24) / (uint)alpha;
        return new Rgba32(
            BlendChannel(source.R, source.A, destination.R, carried, scale),
            BlendChannel(source.G, source.A, destination.G, carried, scale),
            BlendChannel(source.B, source.A, destination.B, carried, scale),
            (byte)alpha);
    }

    private static byte BlendChannel(byte source, int sourceAlpha, byte destination, int carriedAlpha, uint scale)
    {
        ulong unscaled = (ulong)((source * sourceAlpha) + (destination * carriedAlpha));
        return (byte)((unscaled * scale) >> 24);
    }

    /// <summary>Clears the frame's rectangle to transparent black, the disposal colour browsers use.</summary>
    private static void ClearRectangle(Rgba32[] canvas, int canvasWidth, WebpFrame frame)
    {
        for (int y = 0; y < frame.Height; y++)
        {
            canvas.AsSpan((((frame.Y + y) * canvasWidth) + frame.X), frame.Width).Clear();
        }
    }

    private static Rgba32[] DecodeFrame(byte[] data, WebpFrame frame, DecoderOptions options, out int width, out int height)
    {
        if (frame.Lossless)
        {
            uint[] argb = Vp8LDecoder.Decode(data, frame.BitstreamStart, frame.BitstreamLength, options, out width, out height);
            var pixels = new Rgba32[argb.Length];
            for (int i = 0; i < argb.Length; i++)
            {
                uint value = argb[i];
                pixels[i] = new Rgba32((byte)(value >> 16), (byte)(value >> 8), (byte)value, (byte)(value >> 24));
            }

            return pixels;
        }

        Vp8Planes planes = Vp8Decoder.Decode(data, frame.BitstreamStart, frame.BitstreamLength, options);
        width = planes.Width;
        height = planes.Height;
        var rgba = new Rgba32[width * height];
        WebpYuv.ToRgba(planes, rgba);
        if (frame.AlphaLength > 0)
        {
            byte[] alpha = WebpAlpha.Decode(data, frame.AlphaStart, frame.AlphaLength, width, height);
            WebpYuv.ApplyAlpha(rgba, alpha, width * height);
        }

        return rgba;
    }

    // ----- RIFF container -----

    private static WebpFile Parse(byte[] data)
    {
        if (data.Length < 12 || !data.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !data.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            throw new InvalidImageContentException("Not a RIFF/WEBP file.");
        }

        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        int end = data.Length;
        if (riffSize >= 4 && riffSize <= int.MaxValue - 8 && riffSize + 8 < (uint)end)
        {
            end = (int)riffSize + 8;
        }

        var file = new WebpFile();
        WebpFrame? still = null;
        int alphaStart = 0;
        int alphaLength = 0;
        int pos = 12;
        while (pos + 8 <= end)
        {
            ReadOnlySpan<byte> id = data.AsSpan(pos, 4);
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4, 4));
            if (rawSize > (uint)(end - pos - 8))
            {
                throw new InvalidImageContentException("WebP chunk extends past the end of the file.");
            }

            int size = (int)rawSize;
            int payload = pos + 8;

            if (id.SequenceEqual("VP8X"u8))
            {
                if (size < 10)
                {
                    throw new InvalidImageContentException("WebP VP8X chunk is too short.");
                }

                byte flags = data[payload];
                file.HasAlpha = (flags & 0x10) != 0;
                file.IsAnimated = (flags & 0x02) != 0;
                file.Width = 1 + ReadLe24(data, payload + 4);
                file.Height = 1 + ReadLe24(data, payload + 7);
            }
            else if (id.SequenceEqual("ANIM"u8))
            {
                if (size < 6)
                {
                    throw new InvalidImageContentException("WebP ANIM chunk is too short.");
                }

                file.Background = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(payload, 4));
                file.LoopCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(payload + 4, 2));
            }
            else if (id.SequenceEqual("ANMF"u8))
            {
                if (size < 16)
                {
                    throw new InvalidImageContentException("WebP ANMF chunk is too short.");
                }

                byte flags = data[payload + 15];
                var frame = new WebpFrame
                {
                    X = 2 * ReadLe24(data, payload),
                    Y = 2 * ReadLe24(data, payload + 3),
                    Width = 1 + ReadLe24(data, payload + 6),
                    Height = 1 + ReadLe24(data, payload + 9),
                    Duration = ReadLe24(data, payload + 12),
                    Blend = (flags & 0x02) == 0,
                    DisposeToBackground = (flags & 0x01) != 0,
                };
                ParseFrameChunks(data, payload + 16, payload + size, frame);
                file.Frames.Add(frame);
            }
            else if (id.SequenceEqual("ALPH"u8))
            {
                alphaStart = payload;
                alphaLength = size;
            }
            else if (id.SequenceEqual("VP8 "u8) || id.SequenceEqual("VP8L"u8))
            {
                still ??= new WebpFrame
                {
                    BitstreamStart = payload,
                    BitstreamLength = size,
                    Lossless = id[3] == (byte)'L',
                    AlphaStart = alphaStart,
                    AlphaLength = alphaLength,
                };
            }

            // ICCP, EXIF, XMP and any unknown chunk are skipped: they never make a file fail to decode.
            pos = payload + size + (size & 1);
        }

        if (file.IsAnimated)
        {
            if (file.Frames.Count == 0)
            {
                throw new InvalidImageContentException("WebP animation has no ANMF frames.");
            }

            if (file.Width <= 0 || file.Height <= 0)
            {
                throw new InvalidImageContentException("WebP animation declares an empty canvas.");
            }

            return file;
        }

        if (still is null)
        {
            throw new InvalidImageContentException("WebP file contains no image data.");
        }

        // Still images: the authoritative size is the one in the bitstream header, not the VP8X canvas.
        file.Frames.Clear();
        file.Frames.Add(still);
        ReadStillHeader(data, still, file);
        if (!still.Lossless)
        {
            file.HasAlpha = still.AlphaLength > 0;
        }

        still.Width = file.Width;
        still.Height = file.Height;
        return file;
    }

    private static void ReadStillHeader(byte[] data, WebpFrame still, WebpFile file)
    {
        ReadOnlySpan<byte> bitstream = data.AsSpan(still.BitstreamStart, still.BitstreamLength);
        if (still.Lossless)
        {
            if (!Vp8LDecoder.TryReadHeader(bitstream, out int w, out int h, out bool hasAlpha))
            {
                throw new InvalidImageContentException("WebP VP8L chunk has an invalid header.");
            }

            file.Width = w;
            file.Height = h;
            file.Lossless = true;
            file.HasAlpha = hasAlpha;
        }
        else
        {
            if (bitstream.Length >= 1 && (bitstream[0] & 1) != 0)
            {
                throw new NotSupportedException(
                    "WebP: VP8 inter frames are not supported; a still WebP image must be a key frame.");
            }

            if (!Vp8Decoder.TryReadHeader(bitstream, out int w, out int h))
            {
                throw new InvalidImageContentException("WebP VP8 chunk has no readable key frame header.");
            }

            file.Width = w;
            file.Height = h;
            file.Lossless = false;
        }
    }

    private static void ParseFrameChunks(byte[] data, int pos, int end, WebpFrame frame)
    {
        while (pos + 8 <= end)
        {
            ReadOnlySpan<byte> id = data.AsSpan(pos, 4);
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4, 4));
            if (rawSize > (uint)(end - pos - 8))
            {
                throw new InvalidImageContentException("WebP frame chunk extends past the end of the frame.");
            }

            int size = (int)rawSize;
            int payload = pos + 8;
            if (id.SequenceEqual("ALPH"u8))
            {
                frame.AlphaStart = payload;
                frame.AlphaLength = size;
            }
            else if (id.SequenceEqual("VP8 "u8) || id.SequenceEqual("VP8L"u8))
            {
                if (frame.BitstreamLength == 0)
                {
                    frame.BitstreamStart = payload;
                    frame.BitstreamLength = size;
                    frame.Lossless = id[3] == (byte)'L';
                }
            }

            pos = payload + size + (size & 1);
        }

        if (frame.BitstreamLength == 0)
        {
            throw new InvalidImageContentException("WebP ANMF frame carries no image data.");
        }
    }

    /// <summary>One image: a still, or one frame of an animation with its placement and compositing rules.</summary>
    private sealed class WebpFrame
    {
        public int X { get; init; }

        public int Y { get; init; }

        public int Width { get; set; }

        public int Height { get; set; }

        public int Duration { get; init; }

        public bool DisposeToBackground { get; init; }

        public bool Blend { get; init; }

        public int BitstreamStart { get; set; }

        public int BitstreamLength { get; set; }

        public bool Lossless { get; set; }

        public int AlphaStart { get; set; }

        public int AlphaLength { get; set; }
    }

    /// <summary>Everything the container tells us before any pixel is decoded.</summary>
    private sealed class WebpFile
    {
        public int Width { get; set; }

        public int Height { get; set; }

        public bool HasAlpha { get; set; }

        public bool IsAnimated { get; set; }

        public bool Lossless { get; set; }

        public ushort LoopCount { get; set; } = 1;

        public uint Background { get; set; }

        public List<WebpFrame> Frames { get; } = new();
    }
}
