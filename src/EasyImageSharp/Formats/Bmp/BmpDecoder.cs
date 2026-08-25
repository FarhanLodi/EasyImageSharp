using System.Buffers.Binary;
using System.Runtime.InteropServices;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Bmp;

/// <summary>
/// Decodes Windows and OS/2 bitmap (BMP) images: 1/4/8-bit palette, 16/24/32-bit RGB, BI_RGB,
/// BI_BITFIELDS (including alpha masks from V3/V4/V5 headers), RLE8/RLE4 run-length compression,
/// top-down and bottom-up row order, and the 12-byte OS/2 BITMAPCOREHEADER.
/// </summary>
public sealed class BmpDecoder : IImageDecoder
{
    private const int FileHeaderSize = 14;
    private const int CoreHeaderSize = 12;
    private const int InfoHeaderSize = 40;

    private const int CompressionRgb = 0;
    private const int CompressionRle8 = 1;
    private const int CompressionRle4 = 2;
    private const int CompressionBitfields = 3;
    private const int CompressionAlphaBitfields = 6;

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
            throw DecoderGuard.Wrap("BMP", ex);
        }
    }

    private static Image<TPixel> DecodeCore<TPixel>(ReadOnlySpan<byte> data, DecoderOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Header header = ParseHeader(data, readPalette: true);
        int width = header.Width;
        int height = header.AbsHeight;
        options.EnsureFrameWithinLimits(width, height, "BMP");

        if (header.Compression is not (CompressionRle8 or CompressionRle4))
        {
            long stride = (((long)header.BitsPerPixel * width) + 31) / 32 * 4;
            if (stride > int.MaxValue || header.DataOffset + (stride * height) > data.Length)
            {
                throw new InvalidImageContentException("BMP pixel data is truncated.");
            }

            header.Stride = (int)stride;
        }

        var image = new Image<TPixel>(
            new List<ImageFrame<TPixel>> { FrameFactory.CreateUninitialized<TPixel>(width, height) }, CreateMetadata(header));
        ImageFrame<TPixel> frame = image.Frames.RootFrame;
        var rgbaRow = new Rgba32[width];

        if (header.Compression is CompressionRle8 or CompressionRle4)
        {
            // Run-length data has no fixed stride; decode into a bottom-up index buffer first.
            // Pixels the stream never writes keep index 0 (palette entry 0), the common convention.
            var indices = new byte[width * height];
            DecodeRle(data[header.DataOffset..], indices, width, height, header.Compression == CompressionRle4);
            Rgba32[] palette = header.Palette!;
            TPixel[] lut = BuildPaletteLut<TPixel>(palette);
            for (int destY = 0; destY < height; destY++)
            {
                ReadOnlySpan<byte> row = indices.AsSpan((height - 1 - destY) * width, width);
                Span<TPixel> destination = frame.GetRowSpan(destY);
                for (int x = 0; x < width; x++)
                {
                    int index = row[x];
                    destination[x] = index < lut.Length
                        ? lut[index]
                        : throw new InvalidImageContentException("BMP palette index out of range.");
                }
            }

            return image;
        }

        int rowStride = header.Stride;

        // When the file's byte layout already is one of the pixel formats, the row becomes a bulk copy or
        // shuffle instead of a per-pixel round trip through Rgba32.
        BmpRowLayout layout = ClassifyRowLayout(header);
        TPixel[]? paletteLut = layout == BmpRowLayout.Palette8 ? BuildPaletteLut<TPixel>(header.Palette!) : null;

        for (int destY = 0; destY < height; destY++)
        {
            int srcRow = header.TopDown ? destY : height - 1 - destY;
            ReadOnlySpan<byte> row = data.Slice(header.DataOffset + (srcRow * rowStride), rowStride);
            Span<TPixel> destination = frame.GetRowSpan(destY);
            switch (layout)
            {
                case BmpRowLayout.Bgr24:
                    PixelOps.Convert<Bgr24, TPixel>(MemoryMarshal.Cast<byte, Bgr24>(row[..(width * 3)]), destination);
                    break;
                case BmpRowLayout.Bgra32:
                    PixelOps.Convert<Bgra32, TPixel>(MemoryMarshal.Cast<byte, Bgra32>(row[..(width * 4)]), destination);
                    break;
                case BmpRowLayout.Palette8:
                    for (int x = 0; x < width; x++)
                    {
                        int index = row[x];
                        destination[x] = index < paletteLut!.Length
                            ? paletteLut[index]
                            : throw new InvalidImageContentException("BMP palette index out of range.");
                    }

                    break;
                default:
                    DecodeRow(row, rgbaRow, header);
                    PixelOps.FromRgba32(rgbaRow, destination);
                    break;
            }
        }

        return image;
    }

    public ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            Header header = ParseHeader(data, readPalette: false);
            return new ImageInfo(header.Width, header.AbsHeight, header.BitsPerPixel, 1, ImageFormat.Bmp, CreateMetadata(header));
        }
        catch (Exception ex) when (DecoderGuard.IsMalformedInputSymptom(ex))
        {
            throw DecoderGuard.Wrap("BMP", ex);
        }
    }

    /// <summary>Builds the image metadata from the header: pixels-per-metre resolution and the bit depth.</summary>
    private static ImageMetadata CreateMetadata(in Header header)
    {
        var metadata = new ImageMetadata { DecodedImageFormat = ImageFormat.Bmp };
        metadata.GetBmpMetadata().BitsPerPixel = (BmpBitsPerPixel)header.BitsPerPixel;
        if (header.PixelsPerMeterX > 0 && header.PixelsPerMeterY > 0)
        {
            metadata.SetResolution(header.PixelsPerMeterX, header.PixelsPerMeterY, PixelResolutionUnit.PixelsPerMeter);
        }

        return metadata;
    }

    /// <summary>Row layouts that map straight onto a pixel format.</summary>
    private enum BmpRowLayout
    {
        /// <summary>No shortcut; the row goes through the general per-pixel decode.</summary>
        General,

        /// <summary>24 bits per pixel, blue first - the layout of <see cref="Bgr24"/>.</summary>
        Bgr24,

        /// <summary>32 bits per pixel with the canonical BGRA masks - the layout of <see cref="Bgra32"/>.</summary>
        Bgra32,

        /// <summary>8-bit palette indices, which become a table lookup per pixel.</summary>
        Palette8,
    }

    private static BmpRowLayout ClassifyRowLayout(in Header header) => header.BitsPerPixel switch
    {
        24 => BmpRowLayout.Bgr24,
        32 when header.RedMask == 0x00FF0000u && header.GreenMask == 0x0000FF00u
            && header.BlueMask == 0x000000FFu && header.AlphaMask == 0xFF000000u => BmpRowLayout.Bgra32,
        8 when header.Palette is not null => BmpRowLayout.Palette8,
        _ => BmpRowLayout.General,
    };

    /// <summary>The palette as the destination pixel format, so a paletted row is one lookup per pixel.</summary>
    private static TPixel[] BuildPaletteLut<TPixel>(Rgba32[] palette)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var lut = new TPixel[palette.Length];
        for (int i = 0; i < palette.Length; i++)
        {
            lut[i] = TPixel.FromRgba32(palette[i]);
        }

        return lut;
    }

    private static void DecodeRow(ReadOnlySpan<byte> row, Span<Rgba32> dest, in Header header)
    {
        switch (header.BitsPerPixel)
        {
            case 1:
            case 4:
            case 8:
                DecodePalettedRow(row, dest, header);
                break;
            case 16:
            case 32:
                DecodeBitfieldsRow(row, dest, header);
                break;
            case 24:
                for (int x = 0; x < dest.Length; x++)
                {
                    int i = x * 3;
                    dest[x] = new Rgba32(row[i + 2], row[i + 1], row[i]);
                }

                break;
            default:
                throw new InvalidImageContentException($"Unsupported BMP bit depth: {header.BitsPerPixel}.");
        }
    }

    private static void DecodePalettedRow(ReadOnlySpan<byte> row, Span<Rgba32> dest, in Header header)
    {
        int bits = header.BitsPerPixel;
        int mask = (1 << bits) - 1;
        Rgba32[] palette = header.Palette!;
        for (int x = 0; x < dest.Length; x++)
        {
            int bitIndex = x * bits;
            int value = (row[bitIndex >> 3] >> (8 - bits - (bitIndex & 7))) & mask;
            dest[x] = value < palette.Length
                ? palette[value]
                : throw new InvalidImageContentException("BMP palette index out of range.");
        }
    }

    private static void DecodeBitfieldsRow(ReadOnlySpan<byte> row, Span<Rgba32> dest, in Header header)
    {
        int bytesPerPixel = header.BitsPerPixel / 8;
        for (int x = 0; x < dest.Length; x++)
        {
            uint value = bytesPerPixel == 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(row[(x * 2)..])
                : BinaryPrimitives.ReadUInt32LittleEndian(row[(x * 4)..]);

            byte r = ExtractChannel(value, header.RedMask);
            byte g = ExtractChannel(value, header.GreenMask);
            byte b = ExtractChannel(value, header.BlueMask);
            byte a = header.AlphaMask != 0 ? ExtractChannel(value, header.AlphaMask) : (byte)255;
            dest[x] = new Rgba32(r, g, b, a);
        }
    }

    private static byte ExtractChannel(uint value, uint mask)
    {
        if (mask == 0)
        {
            return 0;
        }

        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        ulong channel = (value & mask) >> shift;
        ulong max = mask >> shift;

        // Scale to the 0-255 range with rounding.
        return max == 0 ? (byte)0 : (byte)(((channel * 255UL) + (max / 2)) / max);
    }

    /// <summary>
    /// Expands an RLE8/RLE4 stream into <paramref name="indices"/> (row 0 = bottom row, as stored in the file).
    /// Supports encoded runs, absolute mode, end-of-line (00 00), end-of-bitmap (00 01) and delta (00 02 dx dy).
    /// </summary>
    private static void DecodeRle(ReadOnlySpan<byte> src, byte[] indices, int width, int height, bool rle4)
    {
        int x = 0;
        int y = 0;
        int pos = 0;
        while (pos < src.Length)
        {
            if (pos + 2 > src.Length)
            {
                throw new InvalidImageContentException("BMP RLE data is truncated.");
            }

            int count = src[pos];
            int value = src[pos + 1];
            pos += 2;

            if (count > 0)
            {
                // Encoded run: 'count' pixels of 'value' (RLE4: alternating high/low nibbles).
                if (y >= height || x + count > width)
                {
                    throw new InvalidImageContentException("BMP RLE run extends past the end of the row.");
                }

                int rowStart = y * width;
                if (rle4)
                {
                    byte hi = (byte)(value >> 4);
                    byte lo = (byte)(value & 0x0F);
                    for (int i = 0; i < count; i++)
                    {
                        indices[rowStart + x + i] = (i & 1) == 0 ? hi : lo;
                    }
                }
                else
                {
                    indices.AsSpan(rowStart + x, count).Fill((byte)value);
                }

                x += count;
                continue;
            }

            switch (value)
            {
                case 0: // End of line
                    x = 0;
                    y++;
                    break;
                case 1: // End of bitmap
                    return;
                case 2: // Delta
                    if (pos + 2 > src.Length)
                    {
                        throw new InvalidImageContentException("BMP RLE data is truncated.");
                    }

                    x += src[pos];
                    y += src[pos + 1];
                    pos += 2;
                    if (x > width || y > height)
                    {
                        throw new InvalidImageContentException("BMP RLE delta moves past the end of the bitmap.");
                    }

                    break;
                default: // Absolute mode: 'value' literal pixels, padded to a 16-bit boundary
                {
                    int pixels = value;
                    if (y >= height || x + pixels > width)
                    {
                        throw new InvalidImageContentException("BMP RLE absolute run extends past the end of the row.");
                    }

                    int byteCount = rle4 ? (pixels + 1) / 2 : pixels;
                    if (pos + byteCount > src.Length)
                    {
                        throw new InvalidImageContentException("BMP RLE data is truncated.");
                    }

                    int rowStart = (y * width) + x;
                    if (rle4)
                    {
                        for (int i = 0; i < pixels; i++)
                        {
                            byte b = src[pos + (i >> 1)];
                            indices[rowStart + i] = (i & 1) == 0 ? (byte)(b >> 4) : (byte)(b & 0x0F);
                        }
                    }
                    else
                    {
                        src.Slice(pos, pixels).CopyTo(indices.AsSpan(rowStart, pixels));
                    }

                    x += pixels;
                    pos += (byteCount + 1) & ~1;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Parses the file and DIB headers (and, when <paramref name="readPalette"/> is set, the colour table).
    /// Pixel-data bounds are checked by the caller so that <see cref="Identify"/> stays header-only.
    /// </summary>
    private static Header ParseHeader(ReadOnlySpan<byte> data, bool readPalette)
    {
        if (data.Length < FileHeaderSize + CoreHeaderSize || data[0] != 'B' || data[1] != 'M')
        {
            throw new InvalidImageContentException("Invalid BMP file header.");
        }

        int dataOffset = BinaryPrimitives.ReadInt32LittleEndian(data[10..]);
        ReadOnlySpan<byte> dib = data[FileHeaderSize..];
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        var header = new Header { DataOffset = dataOffset };
        int paletteEntrySize;

        if (headerSize == CoreHeaderSize)
        {
            // OS/2 BITMAPCOREHEADER: unsigned 16-bit dimensions, always bottom-up, RGB-triple palette.
            header.Width = BinaryPrimitives.ReadUInt16LittleEndian(dib[4..]);
            header.Height = BinaryPrimitives.ReadUInt16LittleEndian(dib[6..]);
            header.BitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dib[10..]);
            header.Compression = CompressionRgb;
            paletteEntrySize = 3;
            if (header.BitsPerPixel is not (1 or 4 or 8 or 24))
            {
                throw new InvalidImageContentException($"Invalid bit depth {header.BitsPerPixel} for an OS/2 core BMP header.");
            }
        }
        else if (headerSize >= InfoHeaderSize)
        {
            if (headerSize > dib.Length)
            {
                throw new InvalidImageContentException("BMP header is truncated.");
            }

            header.Width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
            header.Height = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
            header.BitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
            header.Compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
            header.PixelsPerMeterX = BinaryPrimitives.ReadInt32LittleEndian(dib[24..]);
            header.PixelsPerMeterY = BinaryPrimitives.ReadInt32LittleEndian(dib[28..]);
            paletteEntrySize = 4;
        }
        else if (headerSize > CoreHeaderSize)
        {
            throw new NotSupportedException($"OS/2 2.x BMP headers ({headerSize} bytes) are not supported.");
        }
        else
        {
            throw new InvalidImageContentException($"Invalid BMP DIB header size: {headerSize}.");
        }

        if (header.Width <= 0 || header.Height == 0 || header.Height == int.MinValue)
        {
            throw new InvalidImageContentException("Invalid BMP dimensions.");
        }

        header.TopDown = header.Height < 0;
        header.AbsHeight = Math.Abs(header.Height);

        switch (header.BitsPerPixel)
        {
            case 1 or 4 or 8 or 16 or 24 or 32:
                break;
            case 2 or 64:
                throw new NotSupportedException($"{header.BitsPerPixel}-bit BMP images are not supported.");
            default:
                throw new InvalidImageContentException($"Invalid BMP bit depth: {header.BitsPerPixel}.");
        }

        int maskBytesAfterHeader = 0;
        switch (header.Compression)
        {
            case CompressionRgb:
                SetDefaultMasks(ref header);
                break;
            case CompressionRle8:
            case CompressionRle4:
                if (header.BitsPerPixel != (header.Compression == CompressionRle8 ? 8 : 4))
                {
                    throw new InvalidImageContentException("BMP RLE compression does not match the bit depth.");
                }

                if (header.TopDown)
                {
                    throw new InvalidImageContentException("Run-length encoded BMP images cannot be top-down.");
                }

                break;
            case CompressionBitfields:
            case CompressionAlphaBitfields:
                if (header.BitsPerPixel is not (16 or 32))
                {
                    throw new InvalidImageContentException("BMP BI_BITFIELDS is only valid for 16- and 32-bit images.");
                }

                if (headerSize >= 52)
                {
                    int need = headerSize >= 56 ? 56 : 52;
                    if (dib.Length < need)
                    {
                        throw new InvalidImageContentException("BMP header is truncated.");
                    }

                    header.RedMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[40..]);
                    header.GreenMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[44..]);
                    header.BlueMask = BinaryPrimitives.ReadUInt32LittleEndian(dib[48..]);
                    header.AlphaMask = headerSize >= 56 ? BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]) : 0;
                }
                else
                {
                    // Masks stored immediately after the 40-byte header (3 or, for BI_ALPHABITFIELDS, 4 of them).
                    maskBytesAfterHeader = header.Compression == CompressionAlphaBitfields ? 16 : 12;
                    if (dib.Length < headerSize + maskBytesAfterHeader)
                    {
                        throw new InvalidImageContentException("BMP bitfield masks are truncated.");
                    }

                    ReadOnlySpan<byte> masks = dib[headerSize..];
                    header.RedMask = BinaryPrimitives.ReadUInt32LittleEndian(masks);
                    header.GreenMask = BinaryPrimitives.ReadUInt32LittleEndian(masks[4..]);
                    header.BlueMask = BinaryPrimitives.ReadUInt32LittleEndian(masks[8..]);
                    header.AlphaMask = maskBytesAfterHeader == 16 ? BinaryPrimitives.ReadUInt32LittleEndian(masks[12..]) : 0;
                }

                break;
            case 4 or 5:
                throw new NotSupportedException("BMP files embedding JPEG or PNG data (BI_JPEG/BI_PNG) are not supported.");
            case 11 or 12 or 13:
                throw new NotSupportedException("CMYK BMP images are not supported.");
            default:
                throw new InvalidImageContentException($"Invalid BMP compression mode: {header.Compression}.");
        }

        long paletteOffset = (long)FileHeaderSize + headerSize + maskBytesAfterHeader;
        long paletteBytes = 0;
        if (header.BitsPerPixel <= 8)
        {
            int maxEntries = 1 << header.BitsPerPixel;
            int colorsUsed = headerSize >= InfoHeaderSize ? BinaryPrimitives.ReadInt32LittleEndian(dib[32..]) : 0;
            if (colorsUsed < 0 || colorsUsed > maxEntries)
            {
                throw new InvalidImageContentException($"BMP declares {colorsUsed} palette entries for a {header.BitsPerPixel}-bit image.");
            }

            int paletteEntries = colorsUsed > 0 ? colorsUsed : maxEntries;
            paletteBytes = paletteEntries * (long)paletteEntrySize;
            if (readPalette)
            {
                if (paletteOffset + paletteBytes > data.Length)
                {
                    throw new InvalidImageContentException("BMP palette is truncated.");
                }

                var palette = new Rgba32[paletteEntries];
                for (int i = 0; i < paletteEntries; i++)
                {
                    int o = (int)paletteOffset + (i * paletteEntrySize);
                    palette[i] = new Rgba32(data[o + 2], data[o + 1], data[o]);
                }

                header.Palette = palette;
            }
        }

        // A zero offset is a known writer bug; the pixel data then follows the palette directly.
        long minimumOffset = paletteOffset + paletteBytes;
        if (header.DataOffset == 0)
        {
            header.DataOffset = (int)Math.Min(minimumOffset, int.MaxValue);
        }

        if (readPalette && (header.DataOffset < minimumOffset || header.DataOffset > data.Length))
        {
            throw new InvalidImageContentException("BMP pixel data offset is out of range.");
        }

        return header;
    }

    private static void SetDefaultMasks(ref Header header)
    {
        switch (header.BitsPerPixel)
        {
            case 16:
                header.RedMask = 0x7C00;
                header.GreenMask = 0x03E0;
                header.BlueMask = 0x001F;
                break;
            case 32:
                header.RedMask = 0x00FF0000;
                header.GreenMask = 0x0000FF00;
                header.BlueMask = 0x000000FF;
                break;
        }
    }

    private struct Header
    {
        public int Width;
        public int Height;
        public int AbsHeight;
        public bool TopDown;
        public int BitsPerPixel;
        public int Compression;
        public int DataOffset;
        public int Stride;
        public uint RedMask;
        public uint GreenMask;
        public uint BlueMask;
        public uint AlphaMask;
        public Rgba32[]? Palette;

        /// <summary>Horizontal resolution in pixels per metre (0 when the header does not carry one).</summary>
        public int PixelsPerMeterX;

        /// <summary>Vertical resolution in pixels per metre (0 when the header does not carry one).</summary>
        public int PixelsPerMeterY;
    }
}
