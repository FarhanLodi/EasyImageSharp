using System.Buffers.Binary;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Ico;

/// <summary>
/// Decodes Windows icon (ICO) and cursor (CUR) files. Every directory entry becomes a frame, in file order;
/// entries are either PNG streams or BMP DIBs (BITMAPINFOHEADER without file header, 1/4/8/16/24/32-bit,
/// BI_RGB) whose height covers both the XOR colour bitmap and the 1-bit AND transparency mask. The size of the
/// embedded image wins over the directory entry (which stores 0 for 256 pixels).
/// </summary>
/// <remarks>
/// Transparency: for DIBs below 32 bits the AND mask decides (bit set = transparent). A 32-bit DIB uses its
/// alpha channel unless every alpha byte is zero, in which case the icon is treated as opaque with the AND mask
/// applied (the pre-Vista convention). Cursor hotspots are read but not surfaced by this version. DIBs with
/// bitfield, RLE or JPEG/PNG compression codes are reported as unsupported.
/// </remarks>
public sealed class IcoDecoder : IImageDecoder
{
    internal const int DirectoryHeaderSize = 6;
    internal const int DirectoryEntrySize = 16;
    internal const int MaxEntries = 64;

    private const int TypeIcon = 1;
    private const int TypeCursor = 2;
    private const int InfoHeaderSize = 40;

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
            throw DecoderGuard.Wrap("ICO", ex);
        }
    }

    public ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            Entry[] entries = ReadDirectory(data);
            ReadOnlySpan<byte> first = EntryData(data, entries[0]);
            int width, height, bitsPerPixel;
            if (IsPng(first))
            {
                ImageInfo png = new PngDecoder().Identify(first, options);
                width = png.Width;
                height = png.Height;
                bitsPerPixel = png.PixelType.BitsPerPixel;
            }
            else
            {
                DibHeader dib = ParseDibHeader(first, entries[0]);
                width = dib.Width;
                height = dib.Height;
                bitsPerPixel = dib.BitsPerPixel;
            }

            return new ImageInfo(width, height, bitsPerPixel, entries.Length, ImageFormat.Ico);
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap("ICO", ex);
        }
    }

    /// <summary>True when the data starts with the ICONDIR of an icon or cursor and a plausible first entry.</summary>
    internal static bool Matches(ReadOnlySpan<byte> data)
    {
        if (data.Length < DirectoryHeaderSize + DirectoryEntrySize)
        {
            return false;
        }

        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int type = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (reserved != 0 || type is not (TypeIcon or TypeCursor) || count is < 1 or > MaxEntries)
        {
            return false;
        }

        ReadOnlySpan<byte> entry = data[DirectoryHeaderSize..];
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
        return size >= 8 && offset >= DirectoryHeaderSize + ((uint)count * DirectoryEntrySize);
    }

    private static Image<TPixel> DecodeCore<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Entry[] entries = ReadDirectory(data);
        var frames = new List<ImageFrame<TPixel>>(entries.Length);
        DecoderOptions.FrameBudget budget = options.CreateBudget();
        foreach (Entry entry in entries)
        {
            if (frames.Count >= options.MaxFrames)
            {
                break;
            }

            ReadOnlySpan<byte> bytes = EntryData(data, entry);
            if (IsPng(bytes))
            {
                // A PNG-compressed entry is bounded per frame by the PNG decoder's own MaxPixels check, so the
                // cumulative budget is charged once its real size is known - one frame late at the very most.
                ImageFrame<TPixel> decoded = new PngDecoder().Decode<TPixel>(bytes, options).Frames.RootFrame;
                budget.Add(decoded.Width, decoded.Height, "ICO");
                frames.Add(decoded);
            }
            else
            {
                frames.Add(DecodeDib<TPixel>(bytes, entry, ref budget));
            }
        }

        return new Image<TPixel>(frames);
    }

    private static bool IsPng(ReadOnlySpan<byte> data) => ImageFormat.Png.Matches(data);

    private static Entry[] ReadDirectory(ReadOnlySpan<byte> data)
    {
        if (data.Length < DirectoryHeaderSize)
        {
            throw new InvalidImageContentException("ICO directory header is truncated.");
        }

        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int type = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (reserved != 0 || type is not (TypeIcon or TypeCursor))
        {
            throw new InvalidImageContentException("Invalid ICO/CUR directory header.");
        }

        if (count == 0)
        {
            throw new InvalidImageContentException("ICO/CUR file declares no images.");
        }

        if (count > MaxEntries)
        {
            throw new InvalidImageContentException($"ICO/CUR file declares {count} images; at most {MaxEntries} are supported.");
        }

        long directoryEnd = DirectoryHeaderSize + ((long)count * DirectoryEntrySize);
        if (directoryEnd > data.Length)
        {
            throw new InvalidImageContentException("ICO directory is truncated.");
        }

        var entries = new Entry[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = data.Slice(DirectoryHeaderSize + (i * DirectoryEntrySize), DirectoryEntrySize);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            if (size == 0 || offset < directoryEnd || offset >= (uint)data.Length)
            {
                throw new InvalidImageContentException($"ICO directory entry {i} points outside the file.");
            }

            entries[i] = new Entry(
                e[0] == 0 ? 256 : e[0],
                e[1] == 0 ? 256 : e[1],
                type == TypeCursor,
                BinaryPrimitives.ReadUInt16LittleEndian(e[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(e[6..]),
                (int)offset,
                (int)Math.Min(size, (uint)data.Length - offset));
        }

        return entries;
    }

    private static ReadOnlySpan<byte> EntryData(ReadOnlySpan<byte> data, in Entry entry) => data.Slice(entry.Offset, entry.Size);

    private static DibHeader ParseDibHeader(ReadOnlySpan<byte> dib, in Entry entry)
    {
        if (dib.Length < 4)
        {
            throw new InvalidImageContentException("ICO bitmap entry is truncated.");
        }

        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize == 12)
        {
            throw new NotSupportedException("ICO entries with an OS/2 BITMAPCOREHEADER are not supported.");
        }

        if (headerSize < InfoHeaderSize || headerSize > dib.Length)
        {
            throw new InvalidImageContentException($"Invalid ICO bitmap header size {headerSize}.");
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        int storedHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        int bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        int compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
        int colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(dib[32..]);

        if (width <= 0 || storedHeight <= 0)
        {
            throw new InvalidImageContentException("Invalid ICO bitmap dimensions.");
        }

        if (compression != 0)
        {
            throw new NotSupportedException($"ICO bitmap entries with compression {compression} are not supported (only BI_RGB).");
        }

        if (bitsPerPixel is not (1 or 4 or 8 or 16 or 24 or 32))
        {
            throw new InvalidImageContentException($"Invalid ICO bitmap bit depth {bitsPerPixel}.");
        }

        // The stored height normally covers the XOR bitmap and the AND mask; a few writers omit the mask.
        int height;
        bool hasMask;
        if (storedHeight % 2 == 0 && (storedHeight / 2 == entry.Height || storedHeight != entry.Height))
        {
            height = storedHeight / 2;
            hasMask = true;
        }
        else
        {
            height = storedHeight;
            hasMask = false;
        }

        int paletteEntries = 0;
        if (bitsPerPixel <= 8)
        {
            int max = 1 << bitsPerPixel;
            if (colorsUsed < 0 || colorsUsed > max)
            {
                throw new InvalidImageContentException($"ICO bitmap declares {colorsUsed} palette entries for {bitsPerPixel} bits per pixel.");
            }

            paletteEntries = colorsUsed > 0 ? colorsUsed : max;
        }

        return new DibHeader(headerSize, width, height, bitsPerPixel, hasMask, paletteEntries);
    }

    private static ImageFrame<TPixel> DecodeDib<TPixel>(
        scoped ReadOnlySpan<byte> dib, in Entry entry, ref DecoderOptions.FrameBudget budget)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        DibHeader header = ParseDibHeader(dib, entry);
        int width = header.Width;
        int height = header.Height;
        budget.Add(width, height, "ICO");

        long pos = header.HeaderSize;
        Rgba32[]? palette = null;
        if (header.PaletteEntries > 0)
        {
            long paletteBytes = header.PaletteEntries * 4L;
            if (pos + paletteBytes > dib.Length)
            {
                throw new InvalidImageContentException("ICO bitmap palette is truncated.");
            }

            palette = new Rgba32[header.PaletteEntries];
            for (int i = 0; i < palette.Length; i++)
            {
                int o = (int)pos + (i * 4);
                palette[i] = new Rgba32(dib[o + 2], dib[o + 1], dib[o]);
            }

            pos += paletteBytes;
        }

        long xorStride = (((long)width * header.BitsPerPixel) + 31) / 32 * 4;
        long maskStride = ((long)width + 31) / 32 * 4;
        if (pos + (xorStride * height) > dib.Length)
        {
            throw new InvalidImageContentException("ICO bitmap pixel data is truncated.");
        }

        long maskOffset = pos + (xorStride * height);
        bool maskPresent = header.HasMask && maskOffset + (maskStride * height) <= dib.Length;

        var frame = new ImageFrame<TPixel>(width, height);

        // The budget check above is what guarantees width * height fits an int; do not remove it.
        var pixels = new Rgba32[width * height];
        bool anyAlpha = false;
        for (int y = 0; y < height; y++)
        {
            // Rows are stored bottom-up.
            ReadOnlySpan<byte> row = dib.Slice((int)(pos + ((height - 1 - y) * xorStride)), (int)xorStride);
            Span<Rgba32> dest = pixels.AsSpan(y * width, width);
            switch (header.BitsPerPixel)
            {
                case 1 or 4 or 8:
                {
                    int bits = header.BitsPerPixel;
                    int mask = (1 << bits) - 1;
                    for (int x = 0; x < width; x++)
                    {
                        int bitIndex = x * bits;
                        int value = (row[bitIndex >> 3] >> (8 - bits - (bitIndex & 7))) & mask;
                        dest[x] = value < palette!.Length
                            ? palette[value]
                            : throw new InvalidImageContentException("ICO palette index out of range.");
                    }

                    break;
                }

                case 16:
                    for (int x = 0; x < width; x++)
                    {
                        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(row[(x * 2)..]);
                        dest[x] = new Rgba32(Widen5((v >> 10) & 0x1F), Widen5((v >> 5) & 0x1F), Widen5(v & 0x1F));
                    }

                    break;
                case 24:
                    for (int x = 0; x < width; x++)
                    {
                        int o = x * 3;
                        dest[x] = new Rgba32(row[o + 2], row[o + 1], row[o]);
                    }

                    break;
                default:
                    for (int x = 0; x < width; x++)
                    {
                        int o = x * 4;
                        byte a = row[o + 3];
                        anyAlpha |= a != 0;
                        dest[x] = new Rgba32(row[o + 2], row[o + 1], row[o], a);
                    }

                    break;
            }
        }

        bool applyMask = maskPresent && (header.BitsPerPixel < 32 || !anyAlpha);
        if (header.BitsPerPixel == 32 && !anyAlpha)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i].A = 255;
            }
        }

        if (applyMask)
        {
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> row = dib.Slice((int)(maskOffset + ((height - 1 - y) * maskStride)), (int)maskStride);
                Span<Rgba32> dest = pixels.AsSpan(y * width, width);
                for (int x = 0; x < width; x++)
                {
                    if ((row[x >> 3] & (0x80 >> (x & 7))) != 0)
                    {
                        dest[x].A = 0;
                    }
                }
            }
        }

        for (int y = 0; y < height; y++)
        {
            PixelOps.FromRgba32(pixels.AsSpan(y * width, width), frame.GetRowSpan(y));
        }

        return frame;
    }

    private static byte Widen5(int v) => (byte)(((v * 255) + 15) / 31);

    /// <summary>One ICONDIRENTRY. For cursors <see cref="Planes"/> and <see cref="BitCount"/> hold the hotspot.</summary>
    private readonly record struct Entry(int Width, int Height, bool IsCursor, int Planes, int BitCount, int Offset, int Size);

    private readonly record struct DibHeader(int HeaderSize, int Width, int Height, int BitsPerPixel, bool HasMask, int PaletteEntries);
}
