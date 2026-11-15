using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// TIFF decoding fixes found by the libtiff conformance suite: associated (premultiplied) alpha is undone, and the
/// pre-6.0 "compat" LZW variant is read. The end-to-end checks against libtiff's own files are in
/// <see cref="Conformance.LibtiffConformanceTests"/>; these pin the arithmetic and the bit packing.
/// </summary>
public class TiffDecoderCorrectnessTests
{
    [Fact]
    public void Unpremultiply8_DividesByAlpha_RoundsToNearest_AndClamps()
    {
        Rgba32[] row =
        {
            new(64, 32, 0, 128),   // 64 * 255 / 128 = 127.5 -> 128; 32 -> 63.75 -> 64.
            new(10, 20, 30, 255),  // Opaque: unchanged.
            new(5, 6, 7, 0),       // Transparent: passed through as stored.
            new(200, 100, 50, 100), // Colour above alpha (invalid premultiplied data): clamped to 255.
        };

        TiffDecoder.Unpremultiply(row);

        Assert.Equal(new Rgba32(128, 64, 0, 128), row[0]);
        Assert.Equal(new Rgba32(10, 20, 30, 255), row[1]);
        Assert.Equal(new Rgba32(5, 6, 7, 0), row[2]);
        Assert.Equal(new Rgba32(255, 255, 128, 100), row[3]);
    }

    [Fact]
    public void Unpremultiply16_DividesByAlpha_AtFullPrecision()
    {
        Rgba64[] row =
        {
            new(16384, 1, 32768, 32768),
            new(1000, 2000, 3000, ushort.MaxValue),
            new(9, 9, 9, 0),
        };

        TiffDecoder.Unpremultiply(row);

        // (16384 * 65535 + 16384) / 32768 = 32768; (65535 + 16384) / 32768 = 2.49 -> 2; 32768 at half alpha -> 65535.
        Assert.Equal(new Rgba64(32768, 2, 65535, 32768), row[0]);
        Assert.Equal(new Rgba64(1000, 2000, 3000, ushort.MaxValue), row[1]);
        Assert.Equal(new Rgba64(9, 9, 9, 0), row[2]);
    }

    [Fact]
    public void CompatLzw_IsDetectedFromItsLsbFirstClearCode_AndDecoded()
    {
        // Clear, the literals 1 2 3, then end-of-information: five 9-bit codes packed LSB-first, as pre-6.0 writers did.
        byte[] compat = PackLsbFirst(9, 256, 1, 2, 3, 257);
        Assert.True(TiffLzw.IsCompat(compat));

        var output = new byte[3];
        TiffLzw.Decode(compat, output);
        Assert.Equal(new byte[] { 1, 2, 3 }, output);

        // The TIFF 6.0 encoder's output starts with the MSB-first clear code and is never mistaken for compat.
        byte[] modern = TiffLzw.Encode(new byte[] { 1, 2, 3, 1, 2, 3, 1, 2, 3 });
        Assert.False(TiffLzw.IsCompat(modern));
        var roundTrip = new byte[9];
        TiffLzw.Decode(modern, roundTrip);
        Assert.Equal(new byte[] { 1, 2, 3, 1, 2, 3, 1, 2, 3 }, roundTrip);
    }

    private static byte[] PackLsbFirst(int width, params int[] codes)
    {
        var bytes = new List<byte>();
        ulong buffer = 0;
        int count = 0;
        foreach (int code in codes)
        {
            buffer |= (ulong)code << count;
            count += width;
            while (count >= 8)
            {
                bytes.Add((byte)buffer);
                buffer >>= 8;
                count -= 8;
            }
        }

        if (count > 0)
        {
            bytes.Add((byte)buffer);
        }

        return bytes.ToArray();
    }
}
