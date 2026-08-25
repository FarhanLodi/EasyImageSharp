using System.Buffers.Binary;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Tga;

/// <summary>The pixel depths <see cref="TgaEncoder"/> can write.</summary>
public enum TgaBitsPerPixel
{
    /// <summary>8-bit grayscale (image type 3).</summary>
    Pixel8 = 8,

    /// <summary>16-bit truecolor, 5 bits per channel (image type 2, opaque).</summary>
    Pixel16 = 16,

    /// <summary>24-bit BGR truecolor (image type 2).</summary>
    Pixel24 = 24,

    /// <summary>32-bit BGRA truecolor with 8 alpha bits (image type 2).</summary>
    Pixel32 = 32,
}

/// <summary>The compression schemes <see cref="TgaEncoder"/> can apply.</summary>
public enum TgaCompression
{
    /// <summary>Uncompressed pixel data (image types 2/3).</summary>
    None,

    /// <summary>Run-length encoded packets that never span rows (image types 10/11).</summary>
    RunLength,
}

/// <summary>
/// Encodes images as Truevision TGA with a bottom-left origin and the TGA 2.0 footer. Unless
/// <see cref="BitsPerPixel"/> is set, <see cref="L8"/> images are written as 8-bit grayscale, opaque RGB
/// formats as 24-bit and alpha formats as 32-bit truecolor.
/// </summary>
public sealed class TgaEncoder : IImageEncoder
{
    /// <summary>The pixel depth to write, or <see langword="null"/> to choose from the image's pixel format.</summary>
    public TgaBitsPerPixel? BitsPerPixel { get; init; }

    /// <summary>Whether to run-length encode the pixel data. Defaults to <see cref="TgaCompression.RunLength"/>.</summary>
    public TgaCompression Compression { get; init; } = TgaCompression.RunLength;

    public void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        int width = image.Width;
        int height = image.Height;
        if (width > ushort.MaxValue || height > ushort.MaxValue)
        {
            throw new NotSupportedException($"TGA cannot represent a {width}x{height} image; dimensions are limited to 65535.");
        }

        int bits = (int)(this.BitsPerPixel ?? DefaultDepth<TPixel>());
        if (bits is not (8 or 16 or 24 or 32))
        {
            throw new ArgumentOutOfRangeException(nameof(this.BitsPerPixel), bits, "BitsPerPixel must be 8, 16, 24 or 32.");
        }

        bool rle = this.Compression == TgaCompression.RunLength;
        int imageType = (bits == 8 ? TgaHeader.TypeGrayscale : TgaHeader.TypeTrueColor) | (rle ? TgaHeader.RleFlag : 0);
        int descriptor = bits == 32 ? 8 : 0; // Attribute bits; origin bits 0 = bottom-left.

        Span<byte> header = stackalloc byte[TgaHeader.Size];
        header.Clear();
        header[2] = (byte)imageType;
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)height);
        header[16] = (byte)bits;
        header[17] = (byte)descriptor;
        stream.Write(header);

        int bytesPerPixel = bits / 8;
        ImageFrame<TPixel> frame = image.Frames.RootFrame;
        var rgbaRow = new Rgba32[width];
        var rowBytes = new byte[width * bytesPerPixel];
        var packed = rle ? new byte[width * (bytesPerPixel + 1)] : null; // Worst case: one raw packet per pixel.

        for (int y = height - 1; y >= 0; y--)
        {
            PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(y), rgbaRow);
            PackRow(rgbaRow, rowBytes, bits);
            if (packed is null)
            {
                stream.Write(rowBytes);
            }
            else
            {
                int length = RunLengthEncodeRow(rowBytes, bytesPerPixel, packed);
                stream.Write(packed, 0, length);
            }
        }

        Span<byte> footer = stackalloc byte[TgaHeader.FooterSize];
        footer.Clear(); // No extension or developer area.
        TgaHeader.FooterSignature.CopyTo(footer[8..]);
        stream.Write(footer);
    }

    private static TgaBitsPerPixel DefaultDepth<TPixel>()
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (typeof(TPixel) == typeof(L8))
        {
            return TgaBitsPerPixel.Pixel8;
        }

        if (typeof(TPixel) == typeof(Rgb24) || typeof(TPixel) == typeof(Bgr24))
        {
            return TgaBitsPerPixel.Pixel24;
        }

        return TgaBitsPerPixel.Pixel32;
    }

    private static void PackRow(ReadOnlySpan<Rgba32> row, Span<byte> dest, int bits)
    {
        switch (bits)
        {
            case 8:
                for (int x = 0; x < row.Length; x++)
                {
                    dest[x] = PixelOps.Luminance8(row[x]);
                }

                break;
            case 16:
                for (int x = 0; x < row.Length; x++)
                {
                    Rgba32 p = row[x];
                    ushort v = (ushort)(((p.R >> 3) << 10) | ((p.G >> 3) << 5) | (p.B >> 3));
                    BinaryPrimitives.WriteUInt16LittleEndian(dest[(x * 2)..], v);
                }

                break;
            case 24:
                for (int x = 0; x < row.Length; x++)
                {
                    Rgba32 p = row[x];
                    int o = x * 3;
                    dest[o] = p.B;
                    dest[o + 1] = p.G;
                    dest[o + 2] = p.R;
                }

                break;
            default:
                for (int x = 0; x < row.Length; x++)
                {
                    Rgba32 p = row[x];
                    int o = x * 4;
                    dest[o] = p.B;
                    dest[o + 1] = p.G;
                    dest[o + 2] = p.R;
                    dest[o + 3] = p.A;
                }

                break;
        }
    }

    /// <summary>
    /// Packs one row into run-length packets: runs of two or more identical pixels become run packets, everything
    /// else raw packets, each holding at most 128 pixels. Returns the number of bytes written to <paramref name="dest"/>.
    /// </summary>
    private static int RunLengthEncodeRow(ReadOnlySpan<byte> row, int bytesPerPixel, Span<byte> dest)
    {
        int width = row.Length / bytesPerPixel;
        int pos = 0;
        int x = 0;
        while (x < width)
        {
            int run = 1;
            while (x + run < width && run < 128 && SamePixel(row, x, x + run, bytesPerPixel))
            {
                run++;
            }

            if (run >= 2)
            {
                dest[pos++] = (byte)(0x80 | (run - 1));
                row.Slice(x * bytesPerPixel, bytesPerPixel).CopyTo(dest[pos..]);
                pos += bytesPerPixel;
                x += run;
                continue;
            }

            int start = x++;
            while (x < width && x - start < 128 && !(x + 1 < width && SamePixel(row, x, x + 1, bytesPerPixel)))
            {
                x++;
            }

            int count = x - start;
            dest[pos++] = (byte)(count - 1);
            row.Slice(start * bytesPerPixel, count * bytesPerPixel).CopyTo(dest[pos..]);
            pos += count * bytesPerPixel;
        }

        return pos;
    }

    private static bool SamePixel(ReadOnlySpan<byte> row, int a, int b, int bytesPerPixel)
        => row.Slice(a * bytesPerPixel, bytesPerPixel).SequenceEqual(row.Slice(b * bytesPerPixel, bytesPerPixel));
}
