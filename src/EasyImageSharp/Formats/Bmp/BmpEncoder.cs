using System.Buffers.Binary;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing.Quantization;

namespace EasyImageSharp.Formats.Bmp;

/// <summary>
/// Encodes images as uncompressed Windows bitmaps. <see cref="BitsPerPixel"/> chooses the pixel layout:
/// 1, 4 or 8 bits per pixel through a colour table (built by <see cref="Quantizer"/>), 16 bits as 5-6-5,
/// 24 bits as BGR triples (the default) or 32 bits as BGRA behind a <c>BITMAPV4HEADER</c> so the alpha
/// channel survives. The image's physical resolution is written to the header's pixels-per-metre fields.
/// </summary>
public sealed class BmpEncoder : IImageEncoder
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int V4HeaderSize = 108;

    private const int CompressionRgb = 0;
    private const int CompressionBitfields = 3;

    // 5-6-5 for 16-bit output, 8-8-8-8 for 32-bit output; both are written as BI_BITFIELDS.
    private const uint Mask565Red = 0xF800;
    private const uint Mask565Green = 0x07E0;
    private const uint Mask565Blue = 0x001F;
    private const uint Mask8888Red = 0x00FF0000;
    private const uint Mask8888Green = 0x0000FF00;
    private const uint Mask8888Blue = 0x000000FF;
    private const uint Mask8888Alpha = 0xFF000000;

    /// <summary>
    /// The bit depth to write; <see langword="null"/> writes 24-bit BGR. Depths of 8 and below store one
    /// colour-table index per pixel, 16 stores 5-6-5 and 32 stores BGRA with an alpha channel.
    /// </summary>
    public BmpBitsPerPixel? BitsPerPixel { get; init; }

    /// <summary>
    /// The quantizer that builds the colour table for 1-, 4- and 8-bit output; <see langword="null"/> uses
    /// <see cref="KnownQuantizers.Wu"/>. Ignored for the other bit depths.
    /// </summary>
    public IQuantizer? Quantizer { get; init; }

    public void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        int width = image.Width;
        int height = image.Height;
        int bitsPerPixel = this.BitsPerPixel is { } requested ? (int)requested : 24;
        if (bitsPerPixel is not (1 or 4 or 8 or 16 or 24 or 32))
        {
            throw new NotSupportedException($"BMP output with {bitsPerPixel} bits per pixel is not supported.");
        }

        ImageFrame<TPixel> frame = image.Frames.RootFrame;
        byte[]? indices = null;
        Rgba32[]? palette = null;
        if (bitsPerPixel <= 8)
        {
            (indices, palette) = this.QuantizeToPalette(frame, 1 << bitsPerPixel);
        }

        int headerSize = bitsPerPixel == 32 ? V4HeaderSize : InfoHeaderSize;
        int maskBytes = bitsPerPixel == 16 ? 12 : 0;
        int paletteBytes = palette is null ? 0 : palette.Length * 4;
        long offsetLong = FileHeaderSize + headerSize + maskBytes + paletteBytes;
        long strideLong = ((((long)bitsPerPixel * width) + 31) / 32) * 4;
        long pixelDataSizeLong = strideLong * height;
        if (pixelDataSizeLong + offsetLong > int.MaxValue)
        {
            throw new NotSupportedException(
                $"BMP cannot represent a {width}x{height} image; the {bitsPerPixel}-bit file would exceed the format's 2 GiB size limit.");
        }

        int stride = (int)strideLong;
        int pixelDataSize = (int)pixelDataSizeLong;
        int dataOffset = (int)offsetLong;
        int compression = bitsPerPixel is 16 or 32 ? CompressionBitfields : CompressionRgb;

        Span<byte> header = stackalloc byte[FileHeaderSize + V4HeaderSize];
        header.Clear();
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header[2..], dataOffset + pixelDataSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[10..], dataOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header[14..], headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[18..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[22..], height);
        BinaryPrimitives.WriteInt16LittleEndian(header[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[28..], (short)bitsPerPixel);
        BinaryPrimitives.WriteInt32LittleEndian(header[30..], compression);
        BinaryPrimitives.WriteInt32LittleEndian(header[34..], pixelDataSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[38..], PixelsPerMeter(image.Metadata.GetHorizontalResolution(PixelResolutionUnit.PixelsPerMeter)));
        BinaryPrimitives.WriteInt32LittleEndian(header[42..], PixelsPerMeter(image.Metadata.GetVerticalResolution(PixelResolutionUnit.PixelsPerMeter)));
        BinaryPrimitives.WriteInt32LittleEndian(header[46..], palette?.Length ?? 0);
        if (bitsPerPixel == 32)
        {
            // BITMAPV4HEADER: the channel masks live inside the header, followed by an (unused) colour space.
            BinaryPrimitives.WriteUInt32LittleEndian(header[54..], Mask8888Red);
            BinaryPrimitives.WriteUInt32LittleEndian(header[58..], Mask8888Green);
            BinaryPrimitives.WriteUInt32LittleEndian(header[62..], Mask8888Blue);
            BinaryPrimitives.WriteUInt32LittleEndian(header[66..], Mask8888Alpha);
        }

        stream.Write(header[..(FileHeaderSize + headerSize)]);

        if (bitsPerPixel == 16)
        {
            // A 40-byte header carries its BI_BITFIELDS masks immediately after it.
            Span<byte> masks = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(masks, Mask565Red);
            BinaryPrimitives.WriteUInt32LittleEndian(masks[4..], Mask565Green);
            BinaryPrimitives.WriteUInt32LittleEndian(masks[8..], Mask565Blue);
            stream.Write(masks);
        }

        if (palette is not null)
        {
            var table = new byte[palette.Length * 4];
            for (int i = 0; i < palette.Length; i++)
            {
                table[i * 4] = palette[i].B;
                table[(i * 4) + 1] = palette[i].G;
                table[(i * 4) + 2] = palette[i].R;
                table[(i * 4) + 3] = 0; // Reserved; must be zero.
            }

            stream.Write(table);
        }

        var rgbaRow = new Rgba32[width];
        var rowBytes = new byte[stride];
        for (int y = height - 1; y >= 0; y--)
        {
            Array.Clear(rowBytes);
            if (indices is not null)
            {
                PackIndices(indices.AsSpan(y * width, width), rowBytes, bitsPerPixel);
            }
            else
            {
                PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(y), rgbaRow);
                PackPixels(rgbaRow, rowBytes, bitsPerPixel);
            }

            stream.Write(rowBytes);
        }
    }

    /// <summary>Reduces the frame to at most <paramref name="maxColors"/> colours and returns one index per pixel.</summary>
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

    /// <summary>Packs one row of colour-table indices, most significant bits first.</summary>
    private static void PackIndices(ReadOnlySpan<byte> source, Span<byte> dest, int bitsPerPixel)
    {
        if (bitsPerPixel == 8)
        {
            source.CopyTo(dest);
            return;
        }

        int perByte = 8 / bitsPerPixel;
        for (int x = 0; x < source.Length; x++)
        {
            int shift = 8 - bitsPerPixel - ((x % perByte) * bitsPerPixel);
            dest[x / perByte] |= (byte)(source[x] << shift);
        }
    }

    /// <summary>Packs one row of colours into 16-, 24- or 32-bit little-endian samples.</summary>
    private static void PackPixels(ReadOnlySpan<Rgba32> source, Span<byte> dest, int bitsPerPixel)
    {
        for (int x = 0; x < source.Length; x++)
        {
            Rgba32 p = source[x];
            switch (bitsPerPixel)
            {
                case 16:
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        dest[(x * 2)..], (ushort)(((p.R >> 3) << 11) | ((p.G >> 2) << 5) | (p.B >> 3)));
                    break;
                case 24:
                {
                    int i = x * 3;
                    dest[i] = p.B;
                    dest[i + 1] = p.G;
                    dest[i + 2] = p.R;
                    break;
                }

                default:
                {
                    int i = x * 4;
                    dest[i] = p.B;
                    dest[i + 1] = p.G;
                    dest[i + 2] = p.R;
                    dest[i + 3] = p.A;
                    break;
                }
            }
        }
    }

    /// <summary>Rounds a resolution to the header's signed 32-bit pixels-per-metre field (a non-positive value falls back to 96 DPI).</summary>
    private static int PixelsPerMeter(double value)
        => value > 0 && !double.IsInfinity(value) ? (int)Math.Clamp(Math.Round(value), 1, int.MaxValue) : 3780;
}
