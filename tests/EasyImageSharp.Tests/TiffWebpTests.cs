using System.Buffers.Binary;
using EasyImageSharp.Formats.Webp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// WebP-compressed TIFF (compression 50001): every strip is a complete WebP file, so each decoded strip must be
/// exactly what the WebP decoder makes of those bytes. libtiff's own lossless file is checked against libtiff in
/// <see cref="Conformance.LibtiffConformanceTests"/>; these cover lossy and lossless strips, RGBA, a short last
/// strip and the layouts WebP cannot carry.
/// </summary>
public class TiffWebpTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Strips_DecodeToExactlyWhatTheWebpDecoderProduces(bool lossless, bool alpha)
    {
        const int Width = 37;
        const int Height = 29;
        const int RowsPerStrip = 8; // Four strips, the last one five rows high.
        using Image<Rgba32> source = StreamingTestSupport.Noise<Rgba32>(Width, Height, seed: 11);
        if (!alpha)
        {
            source.Mutate(ctx => ctx.BackgroundColor(Color.White));
        }

        var strips = new List<byte[]>();
        using var expected = new Image<Rgba32>(Width, Height);
        for (int top = 0; top < Height; top += RowsPerStrip)
        {
            int rows = Math.Min(RowsPerStrip, Height - top);
            using Image<Rgba32> strip = source.Clone(ctx => ctx.Crop(new Rectangle(0, top, Width, rows)));
            var encoded = new MemoryStream();
            strip.SaveAsWebp(encoded, new WebpEncoder { FileFormat = lossless ? WebpFileFormat.Lossless : WebpFileFormat.Lossy, Quality = 80 });
            strips.Add(encoded.ToArray());

            using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded.ToArray());
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    Rgba32 p = decoded[x, y];
                    expected[x, top + y] = alpha ? p : new Rgba32(p.R, p.G, p.B, 255);
                }
            }
        }

        byte[] tiff = BuildWebpTiff(Width, Height, RowsPerStrip, alpha ? 4 : 3, strips, extraSamples: alpha ? 2 : null);
        using Image<Rgba32> actual = Image.Load<Rgba32>(tiff);
        Assert.Equal(StreamingTestSupport.Pixels(expected), StreamingTestSupport.Pixels(actual));
    }

    [Fact]
    public void LayoutsWebpCannotCarry_AreRejectedAsUnsupported()
    {
        using var strip = new Image<Rgba32>(4, 4, Color.Red);
        var encoded = new MemoryStream();
        strip.SaveAsWebp(encoded, new WebpEncoder { FileFormat = WebpFileFormat.Lossless });
        byte[][] strips = [encoded.ToArray()];

        var grey = Assert.Throws<NotSupportedException>(() => Image.Load<Rgba32>(BuildWebpTiff(4, 4, 4, 1, strips, extraSamples: null)));
        Assert.Contains("WebP-compressed TIFF", grey.Message);
        var associated = Assert.Throws<NotSupportedException>(() => Image.Load<Rgba32>(BuildWebpTiff(4, 4, 4, 4, strips, extraSamples: 1)));
        Assert.Contains("associated alpha", associated.Message);
    }

    [Fact]
    public void AStripDeclaringMorePixelsThanItHolds_IsRejected()
    {
        using var big = new Image<Rgba32>(64, 64, Color.Blue);
        var encoded = new MemoryStream();
        big.SaveAsWebp(encoded, new WebpEncoder { FileFormat = WebpFileFormat.Lossless });

        Assert.ThrowsAny<ImageFormatException>(() => Image.Load<Rgba32>(BuildWebpTiff(8, 8, 8, 3, [encoded.ToArray()], extraSamples: null)));
    }

    /// <summary>A little-endian classic TIFF whose strips are the given WebP files.</summary>
    private static byte[] BuildWebpTiff(int width, int height, int rowsPerStrip, int samples, IReadOnlyList<byte[]> strips, int? extraSamples)
    {
        var entries = new List<(ushort Tag, ushort Type, uint Count, uint Value)>();
        var data = new MemoryStream();
        data.Write(new byte[8]); // Header, written last.

        // Strip payloads first, then the out-of-line arrays, then the directory.
        var offsets = new List<uint>();
        foreach (byte[] strip in strips)
        {
            offsets.Add((uint)data.Length);
            data.Write(strip);
            if ((data.Length & 1) != 0)
            {
                data.WriteByte(0);
            }
        }

        uint WriteArray(IEnumerable<uint> values, bool shorts)
        {
            uint at = (uint)data.Length;
            byte[] b = new byte[4];
            foreach (uint v in values)
            {
                if (shorts)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v);
                    data.Write(b, 0, 2);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(b, v);
                    data.Write(b, 0, 4);
                }
            }

            return at;
        }

        uint bitsValue = samples <= 2 ? 8u | (samples == 2 ? 8u << 16 : 0) : WriteArray(Enumerable.Repeat(8u, samples), shorts: true);
        uint offsetsValue = strips.Count == 1 ? offsets[0] : WriteArray(offsets, shorts: false);
        uint countsValue = strips.Count == 1 ? (uint)strips[0].Length : WriteArray(strips.Select(s => (uint)s.Length), shorts: false);

        entries.Add((256, 4, 1, (uint)width));
        entries.Add((257, 4, 1, (uint)height));
        entries.Add((258, 3, (uint)samples, bitsValue));
        entries.Add((259, 3, 1, 50001));
        entries.Add((262, 3, 1, samples >= 3 ? 2u : 1u));
        entries.Add((273, 4, (uint)strips.Count, offsetsValue));
        entries.Add((277, 3, 1, (uint)samples));
        entries.Add((278, 4, 1, (uint)rowsPerStrip));
        entries.Add((279, 4, (uint)strips.Count, countsValue));
        if (extraSamples is int extra)
        {
            entries.Add((338, 3, 1, (uint)extra));
        }

        uint ifd = (uint)data.Length;
        Span<byte> buffer = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)entries.Count);
        data.Write(buffer[..2]);
        foreach ((ushort tag, ushort type, uint count, uint value) in entries)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, tag);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer[2..], type);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], count);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], value);
            data.Write(buffer);
        }

        data.Write(new byte[4]); // No next directory.
        byte[] file = data.ToArray();
        file[0] = (byte)'I';
        file[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), ifd);
        return file;
    }
}
