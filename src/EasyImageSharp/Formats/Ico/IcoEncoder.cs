using System.Buffers.Binary;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Formats.Ico;

/// <summary>How <see cref="IcoEncoder"/> stores each frame inside the icon.</summary>
public enum IcoEntryFormat
{
    /// <summary>32-bit BMP DIB for frames up to 48 pixels in both dimensions, PNG for larger ones.</summary>
    Auto,

    /// <summary>Always a 32-bit BGRA BMP DIB with an AND mask.</summary>
    Bmp,

    /// <summary>Always a PNG stream (Windows Vista and later).</summary>
    Png,
}

/// <summary>
/// Encodes every frame of an image as one entry of a Windows icon (ICO) or, with <see cref="EncodeAsCursor"/>,
/// cursor (CUR) file. Frames may be at most 256 × 256 pixels (stored as 0 in the directory) and there may be at
/// most 64 of them. BMP entries are 32-bit BGRA with an AND mask that marks fully transparent pixels.
/// </summary>
public sealed class IcoEncoder : IImageEncoder
{
    private const int PngThreshold = 48;

    /// <summary>The container used for each entry. Defaults to <see cref="IcoEntryFormat.Auto"/>.</summary>
    public IcoEntryFormat EntryFormat { get; init; } = IcoEntryFormat.Auto;

    /// <summary>Write a CUR file (type 2) whose directory entries carry hotspots instead of an ICO file (type 1).</summary>
    public bool EncodeAsCursor { get; init; }

    /// <summary>
    /// Per-frame cursor hotspots used when <see cref="EncodeAsCursor"/> is set; frames without an entry use (0, 0).
    /// Ignored for icons.
    /// </summary>
    public IReadOnlyList<Point>? Hotspots { get; init; }

    public void Encode<TPixel>(Image<TPixel> image, Stream stream)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        int count = image.Frames.Count;
        if (count > IcoDecoder.MaxEntries)
        {
            throw new NotSupportedException($"An ICO/CUR file may hold at most {IcoDecoder.MaxEntries} images; the image has {count} frames.");
        }

        var blobs = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            ImageFrame<TPixel> frame = image.Frames[i];
            if (frame.Width > 256 || frame.Height > 256)
            {
                throw new NotSupportedException($"ICO/CUR entries may be at most 256x256 pixels; frame {i} is {frame.Width}x{frame.Height}.");
            }

            bool png = this.EntryFormat switch
            {
                IcoEntryFormat.Png => true,
                IcoEntryFormat.Bmp => false,
                _ => frame.Width > PngThreshold || frame.Height > PngThreshold,
            };
            blobs[i] = png ? EncodePng(image, i) : EncodeDib(frame);
        }

        int directorySize = IcoDecoder.DirectoryHeaderSize + (count * IcoDecoder.DirectoryEntrySize);
        var directory = new byte[directorySize];
        BinaryPrimitives.WriteUInt16LittleEndian(directory.AsSpan(2), (ushort)(this.EncodeAsCursor ? 2 : 1));
        BinaryPrimitives.WriteUInt16LittleEndian(directory.AsSpan(4), (ushort)count);

        long offset = directorySize;
        for (int i = 0; i < count; i++)
        {
            ImageFrame<TPixel> frame = image.Frames[i];
            Span<byte> e = directory.AsSpan(IcoDecoder.DirectoryHeaderSize + (i * IcoDecoder.DirectoryEntrySize), IcoDecoder.DirectoryEntrySize);
            e[0] = (byte)(frame.Width == 256 ? 0 : frame.Width);
            e[1] = (byte)(frame.Height == 256 ? 0 : frame.Height);
            e[2] = 0; // Colour count: not a palette image.
            e[3] = 0;
            if (this.EncodeAsCursor)
            {
                Point hotspot = this.Hotspots is { } spots && i < spots.Count ? spots[i] : default;
                BinaryPrimitives.WriteUInt16LittleEndian(e[4..], (ushort)Math.Clamp(hotspot.X, 0, ushort.MaxValue));
                BinaryPrimitives.WriteUInt16LittleEndian(e[6..], (ushort)Math.Clamp(hotspot.Y, 0, ushort.MaxValue));
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(e[4..], 1);  // Colour planes.
                BinaryPrimitives.WriteUInt16LittleEndian(e[6..], 32); // Bits per pixel.
            }

            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)blobs[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)offset);
            offset += blobs[i].Length;
        }

        stream.Write(directory);
        foreach (byte[] blob in blobs)
        {
            stream.Write(blob);
        }
    }

    private static byte[] EncodePng<TPixel>(Image<TPixel> image, int frameIndex)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using Image<TPixel> single = image.Frames.CloneFrame(frameIndex);
        using var ms = new MemoryStream();
        new PngEncoder().Encode(single, ms);
        return ms.ToArray();
    }

    /// <summary>Writes a 32-bit BGRA BITMAPINFOHEADER DIB (height doubled) followed by the AND mask, both bottom-up.</summary>
    private static byte[] EncodeDib<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        int xorStride = width * 4;
        int maskStride = ((width + 31) / 32) * 4;
        int imageSize = (xorStride + maskStride) * height;
        var dib = new byte[40 + imageSize];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), height * 2);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14), 32);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20), imageSize);

        var rgbaRow = new Rgba32[width];
        int xorOffset = 40;
        int maskOffset = 40 + (xorStride * height);
        for (int y = 0; y < height; y++)
        {
            PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(y), rgbaRow);
            int storedRow = height - 1 - y;
            Span<byte> xor = dib.AsSpan(xorOffset + (storedRow * xorStride), xorStride);
            Span<byte> mask = dib.AsSpan(maskOffset + (storedRow * maskStride), maskStride);
            for (int x = 0; x < width; x++)
            {
                Rgba32 p = rgbaRow[x];
                int o = x * 4;
                xor[o] = p.B;
                xor[o + 1] = p.G;
                xor[o + 2] = p.R;
                xor[o + 3] = p.A;
                if (p.A == 0)
                {
                    mask[x >> 3] |= (byte)(0x80 >> (x & 7));
                }
            }
        }

        return dib;
    }
}
