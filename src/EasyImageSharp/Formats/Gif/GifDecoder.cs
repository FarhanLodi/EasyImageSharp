using System.Buffers.Binary;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Gif;

/// <summary>
/// Decodes GIF87a and GIF89a images: global and local color tables, interlacing, transparency and
/// animation. Every frame of the resulting image is the fully composited logical screen with the
/// disposal method of the preceding frame applied, so <c>image.Frames[i]</c> is exactly what a viewer
/// would display for frame <c>i</c>. Frame delays, disposal methods and transparency are exposed through
/// <see cref="GifFrameMetadata"/> on each frame; the loop count, comments and global colour table size
/// through <see cref="GifMetadata"/> on the image.
/// </summary>
/// <remarks>
/// A truncated or corrupt file yields the frames that were completely decoded before the damage; a file
/// without a single complete frame throws <see cref="InvalidImageContentException"/>. Frames positioned
/// partly or wholly outside the logical screen are clipped.
/// </remarks>
public sealed class GifDecoder : IImageDecoder
{
    private const byte ImageSeparator = 0x2C;
    private const byte ExtensionIntroducer = 0x21;
    private const byte Trailer = 0x3B;
    private const byte GraphicControlLabel = 0xF9;
    private const byte CommentLabel = 0xFE;
    private const byte ApplicationLabel = 0xFF;

    private const int DisposalNone = 0;
    private const int DisposalDoNotDispose = 1;
    private const int DisposalRestoreBackground = 2;
    private const int DisposalRestorePrevious = 3;

    public Image<TPixel> Decode<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            return DecodeCore<TPixel>(data, options);
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap("GIF", ex);
        }
    }

    public ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            return IdentifyCore(data);
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap("GIF", ex);
        }
    }

    private static Image<TPixel> DecodeCore<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var reader = new BlockReader(data);
        LogicalScreen screen = ReadLogicalScreen(ref reader);
        int screenWidth = screen.Width;
        int screenHeight = screen.Height;
        if (screenWidth > 0 && screenHeight > 0)
        {
            options.EnsureFrameWithinLimits(screenWidth, screenHeight, "GIF");
        }

        Rgba32[]? globalTable = screen.GlobalTableSize > 0 ? ReadColorTable(ref reader, screen.GlobalTableSize) : null;

        var frames = new List<ImageFrame<TPixel>>();
        Rgba32[]? canvas = null;
        var metadata = new ImageMetadata { DecodedImageFormat = ImageFormat.Gif };
        GifMetadata gifMetadata = metadata.GetGifMetadata();
        gifMetadata.GlobalColorTableLength = screen.GlobalTableSize;
        gifMetadata.BackgroundColorIndex = screen.BackgroundIndex;

        // Graphic control state that applies to the next image only.
        int disposal = DisposalNone;
        int transparentIndex = -1;
        int frameDelay = 0;

        // Disposal of the most recently drawn frame, applied just before the next one is drawn.
        int pendingDisposal = DisposalNone;
        int pendingX0 = 0, pendingY0 = 0, pendingX1 = 0, pendingY1 = 0;
        Rgba32[]? savedRegion = null;

        try
        {
            bool finished = false;
            while (!finished && frames.Count < options.MaxFrames)
            {
                if (!reader.TryReadByte(out byte blockType))
                {
                    break; // Missing trailer: tolerated.
                }

                switch (blockType)
                {
                    case Trailer:
                        finished = true;
                        break;

                    case ExtensionIntroducer:
                    {
                        byte label = reader.ReadByte();
                        if (label == GraphicControlLabel)
                        {
                            ReadGraphicControl(ref reader, out disposal, out transparentIndex, out frameDelay);
                        }
                        else
                        {
                            ReadOtherExtension(ref reader, label, gifMetadata); // Application, comment and plain-text extensions.
                        }

                        break;
                    }

                    case ImageSeparator:
                    {
                        int left = reader.ReadUInt16();
                        int top = reader.ReadUInt16();
                        int width = reader.ReadUInt16();
                        int height = reader.ReadUInt16();
                        byte flags = reader.ReadByte();
                        bool interlaced = (flags & 0x40) != 0;
                        Rgba32[]? localTable = (flags & 0x80) != 0 ? ReadColorTable(ref reader, 2 << (flags & 0x07)) : null;
                        Rgba32[] palette = localTable ?? globalTable
                            ?? throw new InvalidImageContentException("GIF image has neither a global nor a local color table.");

                        if (canvas is null)
                        {
                            if (screenWidth <= 0 || screenHeight <= 0)
                            {
                                // A zero logical screen is a known encoder bug; adopt the first image's size.
                                if (width == 0 || height == 0)
                                {
                                    throw new InvalidImageContentException("GIF logical screen has zero size.");
                                }

                                screenWidth = width;
                                screenHeight = height;
                                options.EnsureFrameWithinLimits(screenWidth, screenHeight, "GIF");
                            }

                            canvas = new Rgba32[CheckedPixelCount(screenWidth, screenHeight)];
                        }

                        if (width == 0 || height == 0)
                        {
                            // Another known encoder bug: a zero-sized descriptor means "the whole screen".
                            left = 0;
                            top = 0;
                            width = screenWidth;
                            height = screenHeight;
                        }

                        // The pixel limit also bounds the per-frame index buffer.
                        options.EnsureFrameWithinLimits(width, height, "GIF");

                        int minCodeSize = reader.ReadByte();
                        if (minCodeSize > 8)
                        {
                            throw new InvalidImageContentException($"Invalid GIF LZW minimum code size {minCodeSize}.");
                        }

                        if (minCodeSize < 2)
                        {
                            minCodeSize = 2; // Some encoders write 1 for two-color images.
                        }

                        byte[] lzwData = reader.ReadSubBlocks();
                        var indices = new byte[CheckedPixelCount(width, height)];
                        int decodedCount = GifLzwDecoder.Decode(lzwData, minCodeSize, indices);

                        // Clip the frame rectangle to the logical screen.
                        int x0 = Math.Max(0, left);
                        int y0 = Math.Max(0, top);
                        int x1 = Math.Min(screenWidth, left + width);
                        int y1 = Math.Min(screenHeight, top + height);
                        bool visible = x0 < x1 && y0 < y1;

                        // 1. Dispose of the previous frame.
                        ApplyDisposal(canvas, screenWidth, pendingDisposal, pendingX0, pendingY0, pendingX1, pendingY1, savedRegion);
                        savedRegion = null;

                        // 2. Remember what this frame covers if it must be undone afterwards.
                        if (disposal == DisposalRestorePrevious && visible)
                        {
                            savedRegion = CopyRegion(canvas, screenWidth, x0, y0, x1, y1);
                        }

                        // 3. Draw the frame through its palette onto the canvas.
                        if (visible)
                        {
                            DrawFrame(canvas, screenWidth, screenHeight, indices, decodedCount, width, height, left, top,
                                x0, x1, interlaced, palette, transparentIndex);
                        }

                        // 4. Snapshot the composited canvas as this frame.
                        var frame = new ImageFrame<TPixel>(screenWidth, screenHeight);
                        PixelOps.FromRgba32<TPixel>(canvas, frame.PixelSpan);
                        GifFrameMetadata frameMetadata = frame.Metadata.GetGifMetadata();
                        frameMetadata.FrameDelay = frameDelay;
                        frameMetadata.DisposalMethod = (GifDisposalMethod)disposal;
                        frameMetadata.HasTransparency = transparentIndex >= 0;
                        frameMetadata.TransparencyIndex = transparentIndex >= 0 ? (byte)transparentIndex : (byte)0;
                        frameMetadata.LocalColorTableLength = localTable?.Length ?? 0;
                        frames.Add(frame);

                        pendingDisposal = visible ? disposal : DisposalNone;
                        pendingX0 = x0;
                        pendingY0 = y0;
                        pendingX1 = x1;
                        pendingY1 = y1;

                        disposal = DisposalNone;
                        transparentIndex = -1;
                        frameDelay = 0;
                        break;
                    }

                    default:
                        // GIF89a declares extraneous data between blocks corrupt; keep any complete frames.
                        if (frames.Count == 0)
                        {
                            throw new InvalidImageContentException($"Unexpected GIF block type 0x{blockType:X2}.");
                        }

                        finished = true;
                        break;
                }
            }
        }
        catch (InvalidImageContentException) when (frames.Count > 0)
        {
            // Truncated or corrupt data after at least one complete frame: return what was decoded.
        }

        if (frames.Count == 0)
        {
            throw new InvalidImageContentException("GIF image contains no complete frame.");
        }

        return new Image<TPixel>(frames, metadata);
    }

    private static ImageInfo IdentifyCore(ReadOnlySpan<byte> data)
    {
        var reader = new BlockReader(data);
        LogicalScreen screen = ReadLogicalScreen(ref reader);
        int width = screen.Width;
        int height = screen.Height;
        int bitsPerPixel = screen.GlobalTableSize > 0 ? screen.GlobalTableDepth : 8;
        var metadata = new ImageMetadata { DecodedImageFormat = ImageFormat.Gif };
        GifMetadata gifMetadata = metadata.GetGifMetadata();
        gifMetadata.GlobalColorTableLength = screen.GlobalTableSize;
        gifMetadata.BackgroundColorIndex = screen.BackgroundIndex;

        int frameCount = 0;
        try
        {
            reader.Skip(screen.GlobalTableSize * 3);
            bool finished = false;
            while (!finished && reader.TryReadByte(out byte blockType))
            {
                switch (blockType)
                {
                    case Trailer:
                        finished = true;
                        break;

                    case ExtensionIntroducer:
                    {
                        byte label = reader.ReadByte();
                        if (label == GraphicControlLabel)
                        {
                            reader.SkipSubBlocks();
                        }
                        else
                        {
                            ReadOtherExtension(ref reader, label, gifMetadata);
                        }

                        break;
                    }

                    case ImageSeparator:
                    {
                        reader.Skip(4);
                        int imageWidth = reader.ReadUInt16();
                        int imageHeight = reader.ReadUInt16();
                        byte flags = reader.ReadByte();
                        if ((flags & 0x80) != 0)
                        {
                            reader.Skip((2 << (flags & 0x07)) * 3);
                        }

                        reader.ReadByte(); // LZW minimum code size.
                        reader.SkipSubBlocks();
                        frameCount++;

                        if ((width <= 0 || height <= 0) && imageWidth > 0 && imageHeight > 0)
                        {
                            width = imageWidth;
                            height = imageHeight;
                        }

                        break;
                    }

                    default:
                        finished = true;
                        break;
                }
            }
        }
        catch (InvalidImageContentException) when (frameCount > 0)
        {
            // Truncated after at least one complete image block: report the frames that are present.
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidImageContentException("GIF logical screen has zero size.");
        }

        return new ImageInfo(width, height, bitsPerPixel, frameCount, ImageFormat.Gif, metadata);
    }

    // ----- Block parsing -----

    private static LogicalScreen ReadLogicalScreen(ref BlockReader reader)
    {
        ReadOnlySpan<byte> header = reader.ReadBytes(6);
        bool validHeader = header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F'
            && header[3] == (byte)'8' && (header[4] == (byte)'7' || header[4] == (byte)'9') && header[5] == (byte)'a';
        if (!validHeader)
        {
            throw new InvalidImageContentException("Invalid GIF signature.");
        }

        var screen = new LogicalScreen
        {
            Width = reader.ReadUInt16(),
            Height = reader.ReadUInt16(),
        };

        byte flags = reader.ReadByte();
        screen.BackgroundIndex = reader.ReadByte();
        reader.Skip(1); // Pixel aspect ratio.
        if ((flags & 0x80) != 0)
        {
            screen.GlobalTableDepth = (flags & 0x07) + 1;
            screen.GlobalTableSize = 1 << screen.GlobalTableDepth;
        }

        return screen;
    }

    private static Rgba32[] ReadColorTable(ref BlockReader reader, int entries)
    {
        ReadOnlySpan<byte> raw = reader.ReadBytes(entries * 3);
        var table = new Rgba32[entries];
        for (int i = 0; i < entries; i++)
        {
            int o = i * 3;
            table[i] = new Rgba32(raw[o], raw[o + 1], raw[o + 2]);
        }

        return table;
    }

    private static void ReadGraphicControl(ref BlockReader reader, out int disposal, out int transparentIndex, out int frameDelay)
    {
        disposal = DisposalNone;
        transparentIndex = -1;
        frameDelay = 0;

        byte size = reader.ReadByte();
        if (size == 0)
        {
            return; // Empty extension: the size byte was the terminator.
        }

        ReadOnlySpan<byte> payload = reader.ReadBytes(size);
        if (payload.Length >= 4)
        {
            byte packed = payload[0];
            frameDelay = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
            disposal = (packed >> 2) & 0x07;
            if (disposal == 4)
            {
                disposal = DisposalRestorePrevious; // Pre-GIF89a encoders used 4 for "restore previous".
            }
            else if (disposal > DisposalRestorePrevious)
            {
                disposal = DisposalNone;
            }

            if ((packed & 0x01) != 0)
            {
                transparentIndex = payload[3];
            }
        }

        reader.SkipSubBlocks();
    }

    /// <summary>Reads a comment (text) or application (NETSCAPE loop count) extension into the metadata; other extensions are skipped.</summary>
    private static void ReadOtherExtension(ref BlockReader reader, byte label, GifMetadata metadata)
    {
        switch (label)
        {
            case CommentLabel:
                metadata.Comments.Add(ExifReader.DecodeUtf8OrLatin1(reader.ReadSubBlocks()));
                break;
            case ApplicationLabel:
            {
                byte[] payload = reader.ReadSubBlocks();
                // NETSCAPE2.0 / ANIMEXTS1.0: 11-byte identifier, then sub-block [1, loopLo, loopHi].
                if (payload.Length >= 14 && payload[11] == 1
                    && (payload.AsSpan(0, 11).SequenceEqual("NETSCAPE2.0"u8) || payload.AsSpan(0, 11).SequenceEqual("ANIMEXTS1.0"u8)))
                {
                    metadata.RepeatCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(12));
                }

                break;
            }

            default:
                reader.SkipSubBlocks();
                break;
        }
    }

    // ----- Compositing -----

    private static void DrawFrame(
        Rgba32[] canvas, int screenWidth, int screenHeight, byte[] indices, int decodedCount, int width, int height,
        int left, int top, int x0, int x1, bool interlaced, Rgba32[] palette, int transparentIndex)
    {
        int firstColumn = x0 - left;
        int lastColumn = x1 - left;
        int paletteLength = palette.Length;

        for (int streamRow = 0; streamRow < height; streamRow++)
        {
            int rowStart = streamRow * width;
            if (rowStart >= decodedCount)
            {
                break; // The code stream ended early; the remaining rows leave the canvas untouched.
            }

            int imageRow = interlaced ? InterlacedRow(streamRow, height) : streamRow;
            int y = top + imageRow;
            if ((uint)y >= (uint)screenHeight)
            {
                continue;
            }

            int rowEnd = Math.Min(rowStart + lastColumn, decodedCount);
            Span<Rgba32> destination = canvas.AsSpan(y * screenWidth, screenWidth);
            for (int column = firstColumn; rowStart + column < rowEnd; column++)
            {
                int index = indices[rowStart + column];
                if (index == transparentIndex || index >= paletteLength)
                {
                    continue; // Transparent (or out-of-range, treated as transparent like browsers do).
                }

                destination[left + column] = palette[index];
            }
        }
    }

    private static void ApplyDisposal(
        Rgba32[] canvas, int screenWidth, int disposal, int x0, int y0, int x1, int y1, Rgba32[]? savedRegion)
    {
        if (x0 >= x1 || y0 >= y1)
        {
            return;
        }

        int regionWidth = x1 - x0;
        switch (disposal)
        {
            case DisposalRestoreBackground:
                // Browsers ignore the background color index and clear to transparent black.
                for (int y = y0; y < y1; y++)
                {
                    canvas.AsSpan((y * screenWidth) + x0, regionWidth).Clear();
                }

                break;

            case DisposalRestorePrevious when savedRegion is not null:
                for (int y = y0; y < y1; y++)
                {
                    savedRegion.AsSpan((y - y0) * regionWidth, regionWidth).CopyTo(canvas.AsSpan((y * screenWidth) + x0, regionWidth));
                }

                break;

            case DisposalNone:
            case DisposalDoNotDispose:
            default:
                break;
        }
    }

    private static Rgba32[] CopyRegion(Rgba32[] canvas, int screenWidth, int x0, int y0, int x1, int y1)
    {
        int regionWidth = x1 - x0;
        var region = new Rgba32[regionWidth * (y1 - y0)];
        for (int y = y0; y < y1; y++)
        {
            canvas.AsSpan((y * screenWidth) + x0, regionWidth).CopyTo(region.AsSpan((y - y0) * regionWidth, regionWidth));
        }

        return region;
    }

    /// <summary>Maps a row index in interlaced stream order to the image row it belongs to.</summary>
    internal static int InterlacedRow(int streamRow, int height)
    {
        int pass1 = (height + 7) / 8; // Rows 0, 8, 16, ...
        if (streamRow < pass1)
        {
            return streamRow * 8;
        }

        streamRow -= pass1;
        int pass2 = (height + 3) / 8; // Rows 4, 12, 20, ...
        if (streamRow < pass2)
        {
            return 4 + (streamRow * 8);
        }

        streamRow -= pass2;
        int pass3 = (height + 1) / 4; // Rows 2, 6, 10, ...
        if (streamRow < pass3)
        {
            return 2 + (streamRow * 4);
        }

        streamRow -= pass3; // Rows 1, 3, 5, ...
        return 1 + (streamRow * 2);
    }

    private static int CheckedPixelCount(int width, int height)
    {
        long pixels = (long)width * height;
        return pixels <= int.MaxValue
            ? (int)pixels
            : throw new InvalidImageContentException($"GIF frame of {width}x{height} pixels is too large to decode.");
    }

    private struct LogicalScreen
    {
        public int Width;
        public int Height;
        public int GlobalTableDepth;
        public int GlobalTableSize;
        public byte BackgroundIndex;
    }

    /// <summary>Sequential reader over the GIF block structure that reports truncation as malformed content.</summary>
    private ref struct BlockReader
    {
        private readonly ReadOnlySpan<byte> data;
        private int position;

        public BlockReader(ReadOnlySpan<byte> data)
        {
            this.data = data;
            this.position = 0;
        }

        public bool TryReadByte(out byte value)
        {
            if (this.position < this.data.Length)
            {
                value = this.data[this.position++];
                return true;
            }

            value = 0;
            return false;
        }

        public byte ReadByte()
            => this.position < this.data.Length ? this.data[this.position++] : throw Truncated();

        public int ReadUInt16()
        {
            if (this.position + 2 > this.data.Length)
            {
                throw Truncated();
            }

            int value = BinaryPrimitives.ReadUInt16LittleEndian(this.data[this.position..]);
            this.position += 2;
            return value;
        }

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || this.position + count > this.data.Length)
            {
                throw Truncated();
            }

            ReadOnlySpan<byte> slice = this.data.Slice(this.position, count);
            this.position += count;
            return slice;
        }

        public void Skip(int count)
        {
            if (count < 0 || this.position + count > this.data.Length)
            {
                throw Truncated();
            }

            this.position += count;
        }

        /// <summary>Skips a sequence of data sub-blocks up to and including the block terminator.</summary>
        public void SkipSubBlocks()
        {
            while (true)
            {
                byte length = this.ReadByte();
                if (length == 0)
                {
                    return;
                }

                this.Skip(length);
            }
        }

        /// <summary>Reads a sequence of data sub-blocks and returns their concatenated payload.</summary>
        public byte[] ReadSubBlocks()
        {
            int start = this.position;
            int total = 0;
            while (true)
            {
                byte length = this.ReadByte();
                if (length == 0)
                {
                    break;
                }

                this.Skip(length);
                total += length;
            }

            var payload = new byte[total];
            int written = 0;
            int pos = start;
            while (true)
            {
                byte length = this.data[pos++];
                if (length == 0)
                {
                    break;
                }

                this.data.Slice(pos, length).CopyTo(payload.AsSpan(written));
                pos += length;
                written += length;
            }

            return payload;
        }

        private static InvalidImageContentException Truncated() => new("GIF data is truncated.");
    }
}
