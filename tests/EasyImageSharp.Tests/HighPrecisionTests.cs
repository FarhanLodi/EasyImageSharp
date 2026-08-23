using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Precision guarantees of the wide pixel formats: exact 8/16-bit widening and narrowing, lossless
/// conversion between formats that both carry more than 8 bits, and the unclamped float format.
/// </summary>
public class HighPrecisionTests
{
    [Fact]
    public void SixteenToEightBit_RoundsAtTheDocumentedPoints()
    {
        // A plain >> 8 would give 0, 1 and 255; rounding gives 0, 1 and 255 as well for these,
        // but the half steps in between are what separates the two.
        Assert.Equal(0, new Rgb48(0x0000, 0x0000, 0x0000).ToRgba32().R);
        Assert.Equal(1, new Rgb48(0x0101, 0x0101, 0x0101).ToRgba32().R);
        Assert.Equal(255, new Rgb48(0xFFFF, 0xFFFF, 0xFFFF).ToRgba32().R);

        // 0x0080 is just below the half step between 0 and 1, 0x0081 just above it.
        Assert.Equal(0, new Rgb48(0x0080, 0, 0).ToRgba32().R);
        Assert.Equal(1, new Rgb48(0x0081, 0, 0).ToRgba32().R);

        // A truncating shift would report 0 here.
        Assert.Equal(1, new Rgb48(0x00FF, 0, 0).ToRgba32().R);
    }

    [Fact]
    public void SixteenToEightBit_MatchesRoundedRescalingForEveryValue()
    {
        for (int v = 0; v <= ushort.MaxValue; v++)
        {
            byte expected = (byte)Math.Round(v * 255.0 / 65535.0, MidpointRounding.AwayFromZero);
            Assert.Equal(expected, new Rgb48((ushort)v, 0, 0).ToRgba32().R);
            Assert.Equal(expected, new Rgba64((ushort)v, 0, 0, 0).ToRgba32().R);
            Assert.Equal(expected, new L16((ushort)v).ToRgba32().R);
            Assert.Equal(expected, new La32(0, (ushort)v).ToRgba32().A);
        }
    }

    [Fact]
    public void EightToSixteenBit_ReplicatesBitsAndRoundTrips()
    {
        for (int v = 0; v <= byte.MaxValue; v++)
        {
            var source = new Rgba32((byte)v, (byte)v, (byte)v, (byte)v);

            Rgb48 wide = Rgb48.FromRgba32(source);
            Assert.Equal(v * 257, wide.R);
            Assert.Equal(v, wide.ToRgba32().R);

            Rgba64 wideAlpha = Rgba64.FromRgba32(source);
            Assert.Equal(v * 257, wideAlpha.A);
            Assert.Equal(source, wideAlpha.ToRgba32());
        }
    }

    [Fact]
    public void HighPrecisionFormats_AreRecognisedAsSuch()
    {
        Assert.True(PixelOps.IsHighPrecision<Rgb48>());
        Assert.True(PixelOps.IsHighPrecision<Rgba64>());
        Assert.True(PixelOps.IsHighPrecision<L16>());
        Assert.True(PixelOps.IsHighPrecision<La32>());
        Assert.True(PixelOps.IsHighPrecision<RgbaVector>());

        Assert.False(PixelOps.IsHighPrecision<Rgba32>());
        Assert.False(PixelOps.IsHighPrecision<Rgb24>());
        Assert.False(PixelOps.IsHighPrecision<Bgra32>());
        Assert.False(PixelOps.IsHighPrecision<Bgr24>());
        Assert.False(PixelOps.IsHighPrecision<L8>());
        Assert.False(PixelOps.IsHighPrecision<La16>());
        Assert.False(PixelOps.IsHighPrecision<A8>());
        Assert.False(PixelOps.IsHighPrecision<Argb32>());
        Assert.False(PixelOps.IsHighPrecision<Abgr32>());
    }

    [Fact]
    public void Rgb48ToRgba64_KeepsValuesThatEightBitsCannotHold()
    {
        // Routed through Rgba32 these three components would all collapse to zero.
        var source = new Rgb48[] { new(1, 2, 3) };
        var destination = new Rgba64[1];

        PixelOps.Convert<Rgb48, Rgba64>(source, destination);

        Assert.Equal(new Rgba64(1, 2, 3, ushort.MaxValue), destination[0]);
    }

    [Fact]
    public void Rgb48ToRgba64ToRgb48_IsLosslessForEveryValue()
    {
        var source = new Rgb48[ushort.MaxValue + 1];
        for (int v = 0; v <= ushort.MaxValue; v++)
        {
            source[v] = new Rgb48((ushort)v, (ushort)(ushort.MaxValue - v), (ushort)((v * 7) & 0xFFFF));
        }

        var wide = new Rgba64[source.Length];
        var back = new Rgb48[source.Length];
        PixelOps.Convert<Rgb48, Rgba64>(source, wide);
        PixelOps.Convert<Rgba64, Rgb48>(wide, back);

        Assert.Equal(source, back);
    }

    [Fact]
    public void CloneAs_BetweenWideFormats_KeepsFullPrecision()
    {
        using var image = new Image<Rgb48>(4, 4);
        image[0, 0] = new Rgb48(1, 2, 3);
        image[1, 0] = new Rgb48(0x0101, 0x8001, 0xFFFE);
        image[2, 0] = new Rgb48(ushort.MaxValue, 0, 12345);

        using Image<Rgba64> wide = image.CloneAs<Rgba64>();
        using Image<Rgb48> back = wide.CloneAs<Rgb48>();

        Assert.Equal(new Rgba64(1, 2, 3, ushort.MaxValue), wide[0, 0]);
        Assert.Equal(new Rgba64(0x0101, 0x8001, 0xFFFE, ushort.MaxValue), wide[1, 0]);
        Assert.Equal(new Rgb48(1, 2, 3), back[0, 0]);
        Assert.Equal(new Rgb48(0x0101, 0x8001, 0xFFFE), back[1, 0]);
        Assert.Equal(new Rgb48(ushort.MaxValue, 0, 12345), back[2, 0]);
    }

    [Fact]
    public void CloneAs_ToNarrowFormat_RoundsInsteadOfTruncating()
    {
        using var image = new Image<Rgb48>(2, 1);
        image[0, 0] = new Rgb48(0x0081, 0x0080, 0x00FF);
        image[1, 0] = new Rgb48(0xFFFF, 0x8080, 0x0000);

        using Image<Rgb24> narrow = image.CloneAs<Rgb24>();

        Assert.Equal(new Rgb24(1, 0, 1), narrow[0, 0]);
        Assert.Equal(new Rgb24(255, 128, 0), narrow[1, 0]);
    }

    [Fact]
    public void CloneAs_BetweenWideGrayscaleAndColor_KeepsSixteenBits()
    {
        using var image = new Image<Rgba64>(1, 1);
        image[0, 0] = new Rgba64(1000, 1000, 1000, 4321);

        using Image<La32> gray = image.CloneAs<La32>();

        // Equal colour components mean the luma is the same value, kept at 16 bits.
        Assert.InRange(gray[0, 0].L, (ushort)999, (ushort)1001);
        Assert.Equal(4321, gray[0, 0].A);

        using Image<L16> luminance = image.CloneAs<L16>();
        Assert.InRange(luminance[0, 0].PackedValue, (ushort)999, (ushort)1001);
    }

    [Fact]
    public void RgbaVector_StoresValuesOutsideTheUnitRange()
    {
        var pixel = new RgbaVector(-0.5f, 1.5f, 2.75f, 3f);

        Assert.Equal(new Vector4(-0.5f, 1.5f, 2.75f, 3f), pixel.ToScaledVector4());
        Assert.Equal(pixel, RgbaVector.FromScaledVector4(new Vector4(-0.5f, 1.5f, 2.75f, 3f)));

        using var image = new Image<RgbaVector>(1, 1);
        image[0, 0] = pixel;
        using Image<RgbaVector> clone = image.CloneAs<RgbaVector>();
        Assert.Equal(pixel, clone[0, 0]);
    }

    [Fact]
    public void RgbaVector_ClampsOnlyWhenConvertingToIntegerFormats()
    {
        var pixel = new RgbaVector(-0.5f, 1.5f, 0.5f, 2f);

        Assert.Equal(new Rgba32(0, 255, 128, 255), pixel.ToRgba32());

        var source = new RgbaVector[] { pixel };
        var wide = new Rgba64[1];
        PixelOps.Convert<RgbaVector, Rgba64>(source, wide);
        Assert.Equal(new Rgba64(0, ushort.MaxValue, 32768, ushort.MaxValue), wide[0]);
    }

    [Fact]
    public void RgbaVector_HoldsSixteenBitValuesExactly()
    {
        var source = new Rgba64[] { new(1, 2, 3, 4), new(ushort.MaxValue, 32768, 12345, 0) };
        var floats = new RgbaVector[source.Length];
        var back = new Rgba64[source.Length];

        PixelOps.Convert<Rgba64, RgbaVector>(source, floats);
        PixelOps.Convert<RgbaVector, Rgba64>(floats, back);

        Assert.Equal(1f / 65535f, floats[0].R);
        Assert.Equal(source, back);
    }

    [Fact]
    public void ScaledVector4_BulkHelpersRoundTrip()
    {
        var source = new Rgba64[] { new(0, 1, 2, 3), new(65535, 32768, 257, 1) };
        var scaled = new Vector4[source.Length];
        var back = new Rgba64[source.Length];

        PixelOps.ToScaledVector4<Rgba64>(source, scaled);
        PixelOps.FromScaledVector4<Rgba64>(scaled, back);

        Assert.Equal(source, back);
    }

    [Fact]
    public void EightBitConversions_StillRouteThroughRgba32()
    {
        // The 8-bit path is unchanged: every conversion agrees with the pixel-by-pixel Rgba32 route.
        var source = new Rgb24[256];
        for (int v = 0; v < source.Length; v++)
        {
            source[v] = new Rgb24((byte)v, (byte)(255 - v), (byte)((v * 3) & 0xFF));
        }

        var converted = new Bgra32[source.Length];
        PixelOps.Convert<Rgb24, Bgra32>(source, converted);

        for (int v = 0; v < source.Length; v++)
        {
            Assert.Equal(Bgra32.FromRgba32(source[v].ToRgba32()), converted[v]);
        }
    }

    [Fact]
    public void SixteenBitPng_DecodesIntoAWideFormatWithoutLosingBits()
    {
        // Truecolor, two pixels, samples chosen so that the high byte alone cannot tell them apart.
        ushort[] samples = { 0x0001, 0x8000, 0xFFFF, 0x00FF, 0x0100, 0x1234 };
        byte[] png = BuildSixteenBitPng(2, 1, colorType: 2, samples);

        using Image<Rgb48> wide = Image.Load<Rgb48>(png);
        Assert.Equal(new Rgb48(0x0001, 0x8000, 0xFFFF), wide[0, 0]);
        Assert.Equal(new Rgb48(0x00FF, 0x0100, 0x1234), wide[1, 0]);

        using Image<Rgba64> wideAlpha = Image.Load<Rgba64>(png);
        Assert.Equal(new Rgba64(0x0001, 0x8000, 0xFFFF, ushort.MaxValue), wideAlpha[0, 0]);

        // The 8-bit path is untouched: it still keeps the high byte of each sample.
        using Image<Rgba32> narrow = Image.Load<Rgba32>(png);
        Assert.Equal(new Rgba32(0x00, 0x80, 0xFF, 0xFF), narrow[0, 0]);
        Assert.Equal(new Rgba32(0x00, 0x01, 0x12, 0xFF), narrow[1, 0]);
    }

    [Fact]
    public void SixteenBitGrayscaleAlphaPng_KeepsBothChannelsWide()
    {
        ushort[] samples = { 0x1234, 0x00FF, 0xFFFF, 0x0001 };
        byte[] png = BuildSixteenBitPng(2, 1, colorType: 4, samples);

        using Image<La32> wide = Image.Load<La32>(png);
        Assert.Equal(new La32(0x1234, 0x00FF), wide[0, 0]);
        Assert.Equal(new La32(0xFFFF, 0x0001), wide[1, 0]);

        using Image<L16> luminance = Image.Load<L16>(png);
        Assert.Equal(0x1234, luminance[0, 0].PackedValue);
    }

    [Theory]
    [InlineData("png/rgb16.png")]
    [InlineData("png/rgb16_adam7.png")]
    [InlineData("png/rgb16_trns_key.png")]
    [InlineData("png/rgba16.png")]
    [InlineData("png/gray16.png")]
    [InlineData("png/gray16_trns_key.png")]
    [InlineData("tiff/hand_rgb16_raw.tif")]
    [InlineData("tiff/hand_rgb16_mm_lzw_pred2.tif")]
    [InlineData("tiff/hand_rgba16_raw.tif")]
    [InlineData("tiff/hand_gray16_mm.tif")]
    [InlineData("tiff/hand_gray16_ii_lzw_pred2.tif")]
    [InlineData("tiff/hand_graya16_raw.tif")]
    public void SixteenBitFixtures_KeepTheirLowBytesInAWideFormat(string fixture)
    {
        if (!FixturePath.Exists(fixture))
        {
            // The codec fixtures are generated; skip rather than fail if this one has been renamed.
            return;
        }

        byte[] data = FixturePath.Read(fixture);
        using Image<Rgba64> wide = Image.Load<Rgba64>(data);
        using Image<Rgba32> narrow = Image.Load<Rgba32>(data);

        Assert.Equal(narrow.Width, wide.Width);
        Assert.Equal(narrow.Height, wide.Height);

        bool sawLowByte = false;
        for (int y = 0; y < wide.Height; y++)
        {
            for (int x = 0; x < wide.Width; x++)
            {
                Rgba64 w = wide[x, y];
                Rgba32 n = narrow[x, y];

                // The 8-bit path keeps the high byte of every sample; the wide path keeps both bytes,
                // so the high bytes must still agree pixel for pixel.
                Assert.Equal(n.R, (byte)(w.R >> 8));
                Assert.Equal(n.G, (byte)(w.G >> 8));
                Assert.Equal(n.B, (byte)(w.B >> 8));
                Assert.Equal(n.A, (byte)(w.A >> 8));

                sawLowByte |= ((w.R | w.G | w.B) & 0xFF) != 0;
            }
        }

        Assert.True(sawLowByte, $"{fixture} carries no sub-8-bit detail, so it proves nothing.");
    }

    /// <summary>Builds a minimal non-interlaced 16-bit PNG so the decoder can be tested without a fixture.</summary>
    private static byte[] BuildSixteenBitPng(int width, int height, byte colorType, ushort[] samples)
    {
        int channels = colorType switch
        {
            0 => 1,
            2 => 3,
            4 => 2,
            _ => 4,
        };

        var scanlines = new byte[height * (1 + (width * channels * 2))];
        int position = 0;
        int sample = 0;
        for (int y = 0; y < height; y++)
        {
            scanlines[position++] = 0; // Filter type: none.
            for (int i = 0; i < width * channels; i++)
            {
                scanlines[position++] = (byte)(samples[sample] >> 8);
                scanlines[position++] = (byte)(samples[sample] & 0xFF);
                sample++;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(scanlines);
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 16; // Bit depth.
        header[9] = colorType;

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        Span<byte> scratch = stackalloc byte[4];

        BinaryPrimitives.WriteInt32BigEndian(scratch, data.Length);
        stream.Write(scratch);
        stream.Write(typeBytes);
        stream.Write(data);

        BinaryPrimitives.WriteUInt32BigEndian(scratch, Crc32.Append(Crc32.Append(0, typeBytes), data));
        stream.Write(scratch);
    }

    [Fact]
    public void Convert_BetweenIdenticalFormats_CopiesVerbatim()
    {
        var source = new RgbaVector[] { new(float.MaxValue, -1f, 0.5f, 7f) };
        var destination = new RgbaVector[1];

        PixelOps.Convert<RgbaVector, RgbaVector>(source, destination);

        Assert.Equal(source[0], destination[0]);
    }
}
