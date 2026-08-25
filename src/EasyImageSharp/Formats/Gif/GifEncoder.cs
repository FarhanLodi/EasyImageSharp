using System.Buffers.Binary;
using System.Text;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing.Quantization;

namespace EasyImageSharp.Formats.Gif;

/// <summary>
/// Encodes images as GIF89a. Every frame is quantized to at most 256 colours (Wu by default), transparency comes
/// from the quantizer's transparent palette entry, and multi-frame images become animations: each frame is
/// written with a Graphic Control Extension and the file carries a NETSCAPE2.0 loop extension. In
/// <see cref="GifColorTableMode.Global"/> mode the single colour table is quantized from all frames together;
/// in <see cref="GifColorTableMode.Local"/> mode every frame gets its own. Frames whose size differs from the
/// root frame are cropped to the logical screen; smaller frames are placed at the top-left.
/// </summary>
public sealed class GifEncoder : IImageEncoder
{
    private const byte ExtensionIntroducer = 0x21;
    private const byte GraphicControlLabel = 0xF9;
    private const byte CommentLabel = 0xFE;
    private const byte ApplicationLabel = 0xFF;
    private const byte ImageSeparator = 0x2C;
    private const byte Trailer = 0x3B;

    private const int DisposalDoNotDispose = 1;
    private const int DisposalRestoreBackground = 2;

    private const int DefaultFrameDelay = 10;

    private readonly int? frameDelay;
    private readonly int? repeatCount;

    /// <summary>The quantizer that builds the colour tables; <see langword="null"/> uses <see cref="KnownQuantizers.Wu"/>.</summary>
    public IQuantizer? Quantizer { get; init; }

    /// <summary>Whether frames share one global colour table or carry their own. Defaults to <see cref="GifColorTableMode.Global"/>.</summary>
    public GifColorTableMode ColorTableMode { get; init; } = GifColorTableMode.Global;

    /// <summary>
    /// The delay written for every frame of an animation, in hundredths of a second (0-65535). When it is not
    /// set explicitly each frame uses its own <see cref="GifFrameMetadata.FrameDelay"/>, falling back to 10.
    /// </summary>
    public int FrameDelay
    {
        get => this.frameDelay ?? DefaultFrameDelay;
        init
        {
            if (value is < 0 or > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "FrameDelay must be between 0 and 65535 hundredths of a second.");
            }

            this.frameDelay = value;
        }
    }

    /// <summary>
    /// How many times an animation repeats after the first play (0-65535); 0 loops forever. Written as a
    /// NETSCAPE2.0 application extension for multi-frame images only. When it is not set explicitly the image's
    /// <see cref="GifMetadata.RepeatCount"/> is used, falling back to 0.
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

    /// <summary>When true, image data is written interlaced (rows in four passes). Defaults to false.</summary>
    public bool Interlaced { get; init; }

    /// <summary>An optional comment stored in a comment extension (Latin-1; other characters become '?').</summary>
    public string? Comment { get; init; }

    public void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        int screenWidth = image.Width;
        int screenHeight = image.Height;
        if (screenWidth > ushort.MaxValue || screenHeight > ushort.MaxValue)
        {
            throw new NotSupportedException($"GIF cannot store a {screenWidth}x{screenHeight} image; both dimensions must be at most 65535.");
        }

        List<EncodedFrame> frames = this.QuantizeFrames(image, screenWidth, screenHeight);
        EncodedFrame root = frames[0];

        // Header and logical screen descriptor; the root frame's palette is always the global colour table.
        stream.Write("GIF89a"u8);
        Span<byte> screen = stackalloc byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(screen, (ushort)screenWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(screen[2..], (ushort)screenHeight);
        screen[4] = (byte)(0x80 | 0x70 | (root.TableBits - 1)); // Global table present, 8-bit colour resolution, table size.
        screen[5] = 0; // Background colour index.
        screen[6] = 0; // Pixel aspect ratio: unspecified.
        stream.Write(screen);
        WriteColorTable(stream, root.Palette, root.TableBits);

        if (frames.Count > 1)
        {
            // An explicit RepeatCount wins; otherwise the loop count a decoded animation carried is preserved.
            int loops = this.repeatCount
                ?? (image.Metadata.TryGetFormatMetadata(out GifMetadata? gifMetadata) ? gifMetadata.RepeatCount : 0);
            WriteNetscapeLoop(stream, loops);
        }

        if (!string.IsNullOrEmpty(this.Comment))
        {
            WriteComment(stream, this.Comment);
        }

        bool restoreBackground = false;
        if (frames.Count > 1)
        {
            foreach (EncodedFrame frame in frames)
            {
                restoreBackground |= frame.TransparentIndex >= 0 || frame.Width < screenWidth || frame.Height < screenHeight;
            }
        }

        for (int i = 0; i < frames.Count; i++)
        {
            EncodedFrame frame = frames[i];
            int disposal = frames.Count == 1 ? 0 : restoreBackground ? DisposalRestoreBackground : DisposalDoNotDispose;
            WriteGraphicControl(stream, disposal, frame.Delay, frame.TransparentIndex);
            this.WriteImage(stream, frame, writeLocalTable: i > 0 && this.ColorTableMode == GifColorTableMode.Local);
        }

        stream.WriteByte(Trailer);
    }

    // ----- Quantization -----

    private List<EncodedFrame> QuantizeFrames<TPixel>(Image<TPixel> image, int screenWidth, int screenHeight)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        IQuantizer quantizer = this.Quantizer ?? KnownQuantizers.Wu;
        var frames = new List<EncodedFrame>(image.Frames.Count);
        IQuantizer<TPixel>? shared = null;
        if (this.ColorTableMode == GifColorTableMode.Global)
        {
            // The global table is built from every frame so animations whose colours change still map well.
            shared = quantizer.CreatePixelSpecificQuantizer<TPixel>();
            foreach (ImageFrame<TPixel> source in image.Frames)
            {
                shared.AddPaletteColors(source, new Rectangle(0, 0, Math.Min(source.Width, screenWidth), Math.Min(source.Height, screenHeight)));
            }
        }

        foreach (ImageFrame<TPixel> source in image.Frames)
        {
            var bounds = new Rectangle(0, 0, Math.Min(source.Width, screenWidth), Math.Min(source.Height, screenHeight));
            IQuantizer<TPixel> worker = shared ?? quantizer.CreatePixelSpecificQuantizer<TPixel>();
            IndexedImageFrame<TPixel> indexed = worker.QuantizeFrame(source, bounds);

            ReadOnlySpan<TPixel> palettePixels = indexed.Palette.Span;
            var palette = new Rgba32[palettePixels.Length];
            int transparentIndex = -1;
            for (int i = 0; i < palette.Length; i++)
            {
                palette[i] = palettePixels[i].ToRgba32();
                if (transparentIndex < 0 && palette[i].A == 0)
                {
                    transparentIndex = i;
                }
            }

            int tableBits = 1;
            while ((1 << tableBits) < palette.Length)
            {
                tableBits++;
            }

            // An explicit FrameDelay applies to every frame; otherwise each frame keeps the delay it carries.
            int delay = this.frameDelay ?? (source.Metadata.TryGetFormatMetadata(out GifFrameMetadata? frameMetadata)
                ? Math.Clamp(frameMetadata.FrameDelay, 0, ushort.MaxValue)
                : DefaultFrameDelay);

            frames.Add(new EncodedFrame(indexed.IndexArray, indexed.Width, indexed.Height, palette, tableBits, transparentIndex, delay));
        }

        return frames;
    }

    // ----- Block writers -----

    private static void WriteColorTable(Stream stream, Rgba32[] palette, int tableBits)
    {
        int entries = 1 << tableBits;
        var table = new byte[entries * 3];
        for (int i = 0; i < palette.Length; i++)
        {
            table[i * 3] = palette[i].R;
            table[(i * 3) + 1] = palette[i].G;
            table[(i * 3) + 2] = palette[i].B;
        }

        stream.Write(table);
    }

    private static void WriteNetscapeLoop(Stream stream, int repeatCount)
    {
        Span<byte> block = stackalloc byte[19];
        block[0] = ExtensionIntroducer;
        block[1] = ApplicationLabel;
        block[2] = 11;
        "NETSCAPE2.0"u8.CopyTo(block[3..]);
        block[14] = 3;
        block[15] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(block[16..], (ushort)repeatCount);
        block[18] = 0;
        stream.Write(block);
    }

    private static void WriteComment(Stream stream, string comment)
    {
        byte[] text = Encoding.Latin1.GetBytes(comment);
        stream.WriteByte(ExtensionIntroducer);
        stream.WriteByte(CommentLabel);
        for (int offset = 0; offset < text.Length; offset += 255)
        {
            int length = Math.Min(255, text.Length - offset);
            stream.WriteByte((byte)length);
            stream.Write(text, offset, length);
        }

        stream.WriteByte(0);
    }

    private static void WriteGraphicControl(Stream stream, int disposal, int delay, int transparentIndex)
    {
        Span<byte> block = stackalloc byte[8];
        block[0] = ExtensionIntroducer;
        block[1] = GraphicControlLabel;
        block[2] = 4;
        block[3] = (byte)((disposal << 2) | (transparentIndex >= 0 ? 1 : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(block[4..], (ushort)delay);
        block[6] = (byte)Math.Max(0, transparentIndex);
        block[7] = 0;
        stream.Write(block);
    }

    private void WriteImage(Stream stream, EncodedFrame frame, bool writeLocalTable)
    {
        Span<byte> descriptor = stackalloc byte[10];
        descriptor[0] = ImageSeparator;
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[1..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[3..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[5..], (ushort)frame.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[7..], (ushort)frame.Height);
        byte flags = 0;
        if (writeLocalTable)
        {
            flags |= (byte)(0x80 | (frame.TableBits - 1));
        }

        if (this.Interlaced)
        {
            flags |= 0x40;
        }

        descriptor[9] = flags;
        stream.Write(descriptor);
        if (writeLocalTable)
        {
            WriteColorTable(stream, frame.Palette, frame.TableBits);
        }

        int minCodeSize = Math.Max(2, frame.TableBits);
        stream.WriteByte((byte)minCodeSize);

        byte[] indices = frame.Indices;
        if (this.Interlaced)
        {
            indices = Interlace(indices, frame.Width, frame.Height);
        }

        GifLzwEncoder.Encode(indices, minCodeSize, stream);
    }

    /// <summary>Reorders rows into the four GIF interlace passes (every 8th from 0, every 8th from 4, every 4th from 2, every 2nd from 1).</summary>
    private static byte[] Interlace(byte[] indices, int width, int height)
    {
        var result = new byte[indices.Length];
        int streamRow = 0;
        ReadOnlySpan<(int Start, int Step)> passes = stackalloc (int, int)[] { (0, 8), (4, 8), (2, 4), (1, 2) };
        foreach ((int start, int step) in passes)
        {
            for (int y = start; y < height; y += step)
            {
                indices.AsSpan(y * width, width).CopyTo(result.AsSpan(streamRow * width, width));
                streamRow++;
            }
        }

        return result;
    }

    private sealed class EncodedFrame
    {
        public EncodedFrame(byte[] indices, int width, int height, Rgba32[] palette, int tableBits, int transparentIndex, int delay)
        {
            this.Indices = indices;
            this.Width = width;
            this.Height = height;
            this.Palette = palette;
            this.TableBits = tableBits;
            this.TransparentIndex = transparentIndex;
            this.Delay = delay;
        }

        public byte[] Indices { get; }

        public int Width { get; }

        public int Height { get; }

        public Rgba32[] Palette { get; }

        public int TableBits { get; }

        public int TransparentIndex { get; }

        /// <summary>The delay written to this frame's Graphic Control Extension, in hundredths of a second.</summary>
        public int Delay { get; }
    }
}
