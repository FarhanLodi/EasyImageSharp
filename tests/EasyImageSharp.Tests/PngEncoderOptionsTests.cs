using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Xmp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing.Quantization;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// The PNG encoder options: every colour type and bit depth combination, Adam7 interlacing, palette output with
/// tRNS, 16-bit widening and the scanline filters. Every combination asserted here was also read back with
/// Pillow during development, which reported the same IHDR fields, size, resolution and pixels.
/// </summary>
public class PngEncoderOptionsTests
{
    /// <summary>Every colour type with the bit depths the specification allows for it.</summary>
    public static TheoryData<PngColorType, PngBitDepth> ValidCombinations()
    {
        var data = new TheoryData<PngColorType, PngBitDepth>();
        foreach ((PngColorType type, PngBitDepth[] depths) in Combinations)
        {
            foreach (PngBitDepth depth in depths)
            {
                data.Add(type, depth);
            }
        }

        return data;
    }

    private static readonly (PngColorType Type, PngBitDepth[] Depths)[] Combinations =
    {
        (PngColorType.Grayscale, new[] { PngBitDepth.Bit1, PngBitDepth.Bit2, PngBitDepth.Bit4, PngBitDepth.Bit8, PngBitDepth.Bit16 }),
        (PngColorType.Rgb, new[] { PngBitDepth.Bit8, PngBitDepth.Bit16 }),
        (PngColorType.Palette, new[] { PngBitDepth.Bit1, PngBitDepth.Bit2, PngBitDepth.Bit4, PngBitDepth.Bit8 }),
        (PngColorType.GrayscaleWithAlpha, new[] { PngBitDepth.Bit8, PngBitDepth.Bit16 }),
        (PngColorType.RgbWithAlpha, new[] { PngBitDepth.Bit8, PngBitDepth.Bit16 }),
    };

    // ----- Colour type and bit depth -----

    [Theory]
    [MemberData(nameof(ValidCombinations))]
    public void EveryCombinationWritesTheDeclaredHeaderAndDecodes(PngColorType colorType, PngBitDepth bitDepth)
    {
        using Image<Rgba32> source = Photo(17, 11);

        byte[] data = Encode(source, new PngEncoder { ColorType = colorType, BitDepth = bitDepth });

        Header header = ReadHeader(data);
        Assert.Equal(17, header.Width);
        Assert.Equal(11, header.Height);
        Assert.Equal((byte)colorType, header.ColorType);
        Assert.Equal((byte)bitDepth, header.BitDepth);
        Assert.Equal(0, header.Interlace);

        using Image<Rgba32> decoded = Image.Load<Rgba32>(data);
        Assert.Equal(17, decoded.Width);
        Assert.Equal(11, decoded.Height);
        Assert.Equal(colorType, decoded.Metadata.GetPngMetadata().ColorType);
        Assert.Equal(bitDepth, decoded.Metadata.GetPngMetadata().BitDepth);
    }

    [Theory]
    [MemberData(nameof(ValidCombinations))]
    public void EveryCombinationAlsoWorksInterlaced(PngColorType colorType, PngBitDepth bitDepth)
    {
        using Image<Rgba32> source = Photo(17, 11);
        var options = new PngEncoder { ColorType = colorType, BitDepth = bitDepth };

        byte[] straight = Encode(source, options);
        byte[] interlaced = Encode(source, new PngEncoder
        {
            ColorType = colorType,
            BitDepth = bitDepth,
            InterlaceMethod = PngInterlaceMethod.Adam7,
        });

        Assert.Equal(1, ReadHeader(interlaced).Interlace);
        using Image<Rgba32> fromStraight = Image.Load<Rgba32>(straight);
        using Image<Rgba32> fromInterlaced = Image.Load<Rgba32>(interlaced);
        Assert.True(fromInterlaced.Metadata.GetPngMetadata().Interlaced);
        AssertPixelsEqual(fromStraight, fromInterlaced);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 9)]
    [InlineData(9, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 3)]
    [InlineData(8, 8)]
    [InlineData(9, 9)]
    public void InterlacingHandlesSizesThatLeaveSomePassesEmpty(int width, int height)
    {
        using Image<Rgba32> source = Photo(width, height);

        byte[] interlaced = Encode(source, new PngEncoder { InterlaceMethod = PngInterlaceMethod.Adam7 });

        using Image<Rgba32> decoded = Image.Load<Rgba32>(interlaced);
        AssertPixelsEqual(source, decoded);
    }

    [Fact]
    public void TruecolorAndTruecolorAlphaRoundTripExactly()
    {
        using Image<Rgba32> source = Photo(17, 11);

        using Image<Rgba32> rgb = RoundTrip(source, new PngEncoder { ColorType = PngColorType.Rgb, BitDepth = PngBitDepth.Bit8 });
        using Image<Rgba32> rgba = RoundTrip(source, new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 });

        AssertPixelsEqual(source, rgb);
        AssertPixelsEqual(source, rgba);
    }

    [Fact]
    public void AlphaSurvivesTruecolorAlphaOutput()
    {
        using Image<Rgba32> source = AlphaImage(13, 9);

        using Image<Rgba32> decoded = RoundTrip(source, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });

        AssertPixelsEqual(source, decoded);
    }

    [Fact]
    public void SixteenBitOutputWidensSamplesExactly()
    {
        // An 8-bit sample v becomes v * 257, which is the exact 16-bit representation of the same fraction, so
        // narrowing it back on decode returns v.
        using Image<Rgba32> source = Photo(17, 11);

        // Unfiltered output so the raw 16-bit samples can be read straight out of the IDAT stream.
        byte[] data = Encode(
            source,
            new PngEncoder { ColorType = PngColorType.Rgb, BitDepth = PngBitDepth.Bit16, FilterMethod = PngFilterMethod.None });

        byte[] scanlines = Inflate(ConcatIdat(data));
        int bytesPerRow = (17 * 3 * 2) + 1;
        Assert.Equal(bytesPerRow * 11, scanlines.Length);
        Assert.Equal(0, scanlines[0]);
        for (int x = 0; x < 17; x++)
        {
            Rgba32 expected = source[x, 0];
            Assert.Equal(expected.R * 257, BinaryPrimitives.ReadUInt16BigEndian(scanlines.AsSpan(1 + (x * 6))));
            Assert.Equal(expected.G * 257, BinaryPrimitives.ReadUInt16BigEndian(scanlines.AsSpan(3 + (x * 6))));
            Assert.Equal(expected.B * 257, BinaryPrimitives.ReadUInt16BigEndian(scanlines.AsSpan(5 + (x * 6))));
        }

        using Image<Rgba32> decoded = Image.Load<Rgba32>(data);
        AssertPixelsEqual(source, decoded);
    }

    [Fact]
    public void SixteenBitAlphaRoundTripsExactly()
    {
        using Image<Rgba32> source = AlphaImage(13, 9);

        using Image<Rgba32> decoded = RoundTrip(
            source, new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit16 });

        AssertPixelsEqual(source, decoded);
    }

    [Fact]
    public void GrayscaleOutputUsesLuminance()
    {
        using Image<Rgba32> source = Photo(16, 8);

        using Image<Rgba32> decoded = RoundTrip(
            source, new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit8 });

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                Rgba32 pixel = decoded[x, y];
                Assert.Equal(pixel.R, pixel.G);
                Assert.Equal(pixel.G, pixel.B);
                Assert.Equal(255, pixel.A);
                Assert.Equal(PixelOps.Luminance8(source[x, y]), pixel.R);
            }
        }
    }

    [Theory]
    [InlineData(PngBitDepth.Bit1, 2)]
    [InlineData(PngBitDepth.Bit2, 4)]
    [InlineData(PngBitDepth.Bit4, 16)]
    public void SubByteGrayscaleUsesTheDeclaredNumberOfLevels(PngBitDepth bitDepth, int levels)
    {
        using Image<Rgba32> source = Photo(24, 16);

        using Image<Rgba32> decoded = RoundTrip(
            source, new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = bitDepth });

        var seen = new HashSet<byte>();
        for (int y = 0; y < decoded.Height; y++)
        {
            for (int x = 0; x < decoded.Width; x++)
            {
                seen.Add(decoded[x, y].R);
            }
        }

        Assert.InRange(seen.Count, 1, levels);
        foreach (byte value in seen)
        {
            Assert.Equal(0, value % (255 / (levels - 1)));
        }
    }

    [Fact]
    public void GrayscaleWithAlphaKeepsTheAlphaChannel()
    {
        using Image<Rgba32> source = AlphaImage(13, 9);

        using Image<Rgba32> decoded = RoundTrip(source, new PngEncoder { ColorType = PngColorType.GrayscaleWithAlpha });

        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Assert.Equal(source[x, y].A, decoded[x, y].A);
                Assert.Equal(PixelOps.Luminance8(source[x, y]), decoded[x, y].R);
            }
        }
    }

    // ----- Palette output -----

    [Fact]
    public void PaletteOutputPicksTheSmallestBitDepthThatHoldsThePalette()
    {
        using var twoColors = new Image<Rgba32>(8, 8, new Rgba32(10, 20, 30));
        for (int x = 0; x < 8; x++)
        {
            twoColors[x, 0] = new Rgba32(200, 100, 50);
        }

        byte[] data = Encode(twoColors, new PngEncoder { ColorType = PngColorType.Palette });

        Assert.Equal(1, ReadHeader(data).BitDepth);
        AssertPixelsEqual(twoColors, Image.Load<Rgba32>(data));
    }

    [Theory]
    [InlineData(PngBitDepth.Bit1, 2)]
    [InlineData(PngBitDepth.Bit2, 4)]
    [InlineData(PngBitDepth.Bit4, 16)]
    [InlineData(PngBitDepth.Bit8, 256)]
    public void PaletteOutputNeverExceedsTheEntriesTheBitDepthCanIndex(PngBitDepth bitDepth, int maxEntries)
    {
        using Image<Rgba32> source = Photo(32, 24);

        byte[] data = Encode(source, new PngEncoder { ColorType = PngColorType.Palette, BitDepth = bitDepth });

        int paletteBytes = FindChunk(data, "PLTE").Length;
        Assert.Equal(0, paletteBytes % 3);
        Assert.InRange(paletteBytes / 3, 1, maxEntries);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(data);
        Assert.Equal(source.Width, decoded.Width);
    }

    [Fact]
    public void PaletteOutputRoundTripsExactlyWhenTheImageFits()
    {
        using Image<Rgba32> source = FewColors(20, 12);

        using Image<Rgba32> decoded = RoundTrip(source, new PngEncoder { ColorType = PngColorType.Palette });

        AssertPixelsEqual(source, decoded);
    }

    [Fact]
    public void PaletteOutputWritesTrnsAndPreservesAlpha()
    {
        using Image<Rgba32> source = AlphaImage(13, 9);

        byte[] data = Encode(source, new PngEncoder { ColorType = PngColorType.Palette });

        byte[] trns = FindChunk(data, "tRNS");
        Assert.NotEmpty(trns);
        Assert.Contains(trns, b => b != 255);

        using Image<Rgba32> decoded = Image.Load<Rgba32>(data);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Assert.Equal(source[x, y].A, decoded[x, y].A);
            }
        }
    }

    [Fact]
    public void AnOpaquePaletteImageWritesNoTrnsChunk()
    {
        using Image<Rgba32> source = FewColors(20, 12);

        byte[] data = Encode(source, new PngEncoder { ColorType = PngColorType.Palette });

        Assert.Empty(FindChunk(data, "tRNS"));
    }

    [Fact]
    public void ThePaletteChunkComesAfterTheHeaderAndBeforeTheData()
    {
        using Image<Rgba32> source = FewColors(20, 12);

        byte[] data = Encode(source, new PngEncoder { ColorType = PngColorType.Palette });

        List<string> order = ChunkOrder(data);
        Assert.Equal("IHDR", order[0]);
        Assert.Equal("IEND", order[^1]);
        Assert.True(order.IndexOf("PLTE") > order.IndexOf("IHDR"));
        Assert.True(order.IndexOf("PLTE") < order.IndexOf("IDAT"));
        Assert.True(order.IndexOf("pHYs") < order.IndexOf("IDAT"));
    }

    [Fact]
    public void ThePaletteQuantizerIsConfigurable()
    {
        using Image<Rgba32> source = Photo(24, 18);

        using Image<Rgba32> decoded = RoundTrip(
            source,
            new PngEncoder
            {
                ColorType = PngColorType.Palette,
                Quantizer = new WebSafePaletteQuantizer(new QuantizerOptions { Dither = null }),
            });

        for (int y = 0; y < decoded.Height; y++)
        {
            for (int x = 0; x < decoded.Width; x++)
            {
                Assert.Equal(0, decoded[x, y].R % 0x33);
                Assert.Equal(0, decoded[x, y].G % 0x33);
                Assert.Equal(0, decoded[x, y].B % 0x33);
            }
        }
    }

    // ----- Filters -----

    [Theory]
    [InlineData(PngFilterMethod.None, 0)]
    [InlineData(PngFilterMethod.Sub, 1)]
    [InlineData(PngFilterMethod.Up, 2)]
    [InlineData(PngFilterMethod.Average, 3)]
    [InlineData(PngFilterMethod.Paeth, 4)]
    public void AFixedFilterIsUsedOnEveryScanline(PngFilterMethod method, int filterType)
    {
        using Image<Rgba32> source = Photo(17, 11);

        byte[] data = Encode(source, new PngEncoder { FilterMethod = method });

        byte[] scanlines = Inflate(ConcatIdat(data));
        int bytesPerRow = (17 * 4) + 1;
        Assert.Equal(bytesPerRow * 11, scanlines.Length);
        for (int y = 0; y < 11; y++)
        {
            Assert.Equal(filterType, scanlines[y * bytesPerRow]);
        }

        AssertPixelsEqual(source, Image.Load<Rgba32>(data));
    }

    [Fact]
    public void TheAdaptiveFilterChoosesPerScanlineAndStaysValid()
    {
        using Image<Rgba32> source = Photo(17, 11);

        byte[] data = Encode(source, new PngEncoder { FilterMethod = PngFilterMethod.Adaptive });

        byte[] scanlines = Inflate(ConcatIdat(data));
        int bytesPerRow = (17 * 4) + 1;
        for (int y = 0; y < 11; y++)
        {
            Assert.InRange(scanlines[y * bytesPerRow], 0, 4);
        }

        AssertPixelsEqual(source, Image.Load<Rgba32>(data));
    }

    [Theory]
    [InlineData(PngFilterMethod.None)]
    [InlineData(PngFilterMethod.Sub)]
    [InlineData(PngFilterMethod.Up)]
    [InlineData(PngFilterMethod.Average)]
    [InlineData(PngFilterMethod.Paeth)]
    [InlineData(PngFilterMethod.Adaptive)]
    public void EveryFilterRoundTripsInterlacedAndSubByteOutput(PngFilterMethod method)
    {
        using Image<Rgba32> source = Photo(13, 9);

        using Image<Rgba32> plain = RoundTrip(source, new PngEncoder { FilterMethod = method });
        using Image<Rgba32> interlaced = RoundTrip(
            source, new PngEncoder { FilterMethod = method, InterlaceMethod = PngInterlaceMethod.Adam7 });
        using Image<Rgba32> palette = RoundTrip(
            source, new PngEncoder { FilterMethod = method, ColorType = PngColorType.Palette, BitDepth = PngBitDepth.Bit4 });

        AssertPixelsEqual(source, plain);
        AssertPixelsEqual(source, interlaced);
        Assert.Equal(source.Width, palette.Width);
    }

    // ----- Compression -----

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Fastest)]
    [InlineData(CompressionLevel.Optimal)]
    [InlineData(CompressionLevel.SmallestSize)]
    public void EveryCompressionLevelProducesAValidFile(CompressionLevel level)
    {
        using Image<Rgba32> source = Photo(24, 16);

        using Image<Rgba32> decoded = RoundTrip(source, new PngEncoder { CompressionLevel = level });

        AssertPixelsEqual(source, decoded);
    }

    // ----- Transparent colour mode -----

    [Fact]
    public void ClearingTransparentPixelsZeroesTheirColour()
    {
        using var source = new Image<Rgba32>(4, 1);
        source[0, 0] = new Rgba32(200, 100, 50, 0);
        source[1, 0] = new Rgba32(10, 20, 30, 255);
        source[2, 0] = new Rgba32(90, 90, 90, 0);
        source[3, 0] = new Rgba32(1, 2, 3, 128);

        using Image<Rgba32> preserved = RoundTrip(source, new PngEncoder { TransparentColorMode = PngTransparentColorMode.Preserve });
        using Image<Rgba32> cleared = RoundTrip(source, new PngEncoder { TransparentColorMode = PngTransparentColorMode.Clear });

        Assert.Equal(new Rgba32(200, 100, 50, 0), preserved[0, 0]);
        Assert.Equal(default, cleared[0, 0]);
        Assert.Equal(default, cleared[2, 0]);
        Assert.Equal(new Rgba32(10, 20, 30, 255), cleared[1, 0]);
        Assert.Equal(new Rgba32(1, 2, 3, 128), cleared[3, 0]);
    }

    // ----- Validation -----

    [Theory]
    [InlineData(PngColorType.Rgb, PngBitDepth.Bit1)]
    [InlineData(PngColorType.Rgb, PngBitDepth.Bit2)]
    [InlineData(PngColorType.Rgb, PngBitDepth.Bit4)]
    [InlineData(PngColorType.RgbWithAlpha, PngBitDepth.Bit4)]
    [InlineData(PngColorType.GrayscaleWithAlpha, PngBitDepth.Bit1)]
    [InlineData(PngColorType.Palette, PngBitDepth.Bit16)]
    public void InvalidCombinationsAreRejected(PngColorType colorType, PngBitDepth bitDepth)
    {
        using Image<Rgba32> source = Photo(8, 8);

        Assert.Throws<NotSupportedException>(() => Encode(source, new PngEncoder { ColorType = colorType, BitDepth = bitDepth }));
    }

    [Fact]
    public void TheDefaultsFollowThePixelFormat()
    {
        using Image<Rgba32> rgba = Photo(8, 8);
        using Image<Rgb24> rgb = TestImages.Gradient(8, 8);
        using var gray = new Image<L8>(8, 8);

        Assert.Equal(6, ReadHeader(Encode(rgba, new PngEncoder())).ColorType);
        Assert.Equal(2, ReadHeader(Encode(rgb, new PngEncoder())).ColorType);
        Assert.Equal(0, ReadHeader(Encode(gray, new PngEncoder())).ColorType);
        Assert.Equal(8, ReadHeader(Encode(rgba, new PngEncoder())).BitDepth);
    }

    [Fact]
    public void EncodeRejectsNullArguments()
    {
        var encoder = new PngEncoder();
        using var image = new Image<Rgba32>(4, 4);

        Assert.Throws<ArgumentNullException>(() => encoder.Encode<Rgba32>(null!, new MemoryStream()));
        Assert.Throws<ArgumentNullException>(() => encoder.Encode(image, null!));
    }

    [Fact]
    public void EncodingIsDeterministic()
    {
        using Image<Rgba32> source = Photo(24, 16);
        var encoder = new PngEncoder { ColorType = PngColorType.Palette, BitDepth = PngBitDepth.Bit4 };

        Assert.Equal(Encode(source, encoder), Encode(source, encoder));
    }

    // ----- Metadata coexistence -----

    [Theory]
    [MemberData(nameof(ValidCombinations))]
    public void MetadataSurvivesEveryCombination(PngColorType colorType, PngBitDepth bitDepth)
    {
        using Image<Rgba32> source = Photo(17, 11);
        source.Metadata.SetResolution(200, 100, PixelResolutionUnit.PixelsPerInch);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "EasyImageSharp");
        exif.SetValue(ExifTag.Orientation, (ushort)1);
        source.Metadata.ExifProfile = exif;

        using Image<Rgba32> decoded = RoundTrip(source, new PngEncoder { ColorType = colorType, BitDepth = bitDepth });

        Assert.Equal(200, decoded.Metadata.GetHorizontalResolution(PixelResolutionUnit.PixelsPerInch), 0.01);
        Assert.Equal(100, decoded.Metadata.GetVerticalResolution(PixelResolutionUnit.PixelsPerInch), 0.01);
        Assert.Equal("EasyImageSharp", decoded.Metadata.ExifProfile!.GetValue(ExifTag.Make)!.Value);
    }

    [Fact]
    public void IccXmpAndTextSurviveInterlacedPaletteOutput()
    {
        byte[] icc = FixturePath.Read("metadata/icc_profile.bin");
        byte[] xmp = FixturePath.Read("metadata/xmp_packet.xml");
        using Image<Rgba32> source = FewColors(20, 12);
        source.Metadata.IccProfile = new IccProfile(icc);
        source.Metadata.XmpProfile = new XmpProfile(xmp);
        source.Metadata.GetPngMetadata().TextData.Add(new PngTextData("Title", "Interlaced palette"));
        source.Metadata.GetPngMetadata().Gamma = 0.45455f;

        using Image<Rgba32> decoded = RoundTrip(
            source,
            new PngEncoder
            {
                ColorType = PngColorType.Palette,
                BitDepth = PngBitDepth.Bit4,
                InterlaceMethod = PngInterlaceMethod.Adam7,
            });

        Assert.Equal(icc, decoded.Metadata.IccProfile!.ToByteArray());
        Assert.Equal(xmp, decoded.Metadata.XmpProfile!.ToByteArray());
        Assert.Equal("Interlaced palette", decoded.Metadata.GetPngMetadata().TextData.Single(t => t.Keyword == "Title").Value);
        Assert.Equal(0.45455f, decoded.Metadata.GetPngMetadata().Gamma!.Value, 0.00001);
        AssertPixelsEqual(source, decoded);
    }

    [Fact]
    public void IdentifyReportsTheEncodedHeaderFields()
    {
        using Image<Rgba32> source = Photo(17, 11);

        byte[] data = Encode(source, new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit4 });

        ImageInfo info = Image.Identify(data);
        Assert.Equal(17, info.Width);
        Assert.Equal(11, info.Height);
        Assert.Equal(4, info.PixelType.BitsPerPixel);
        Assert.Equal(PngColorType.Grayscale, info.Metadata.GetPngMetadata().ColorType);
        Assert.Equal(PngBitDepth.Bit4, info.Metadata.GetPngMetadata().BitDepth);
    }

    // ----- Helpers -----

    private readonly record struct Header(int Width, int Height, byte BitDepth, byte ColorType, byte Interlace);

    private static Header ReadHeader(byte[] data)
    {
        byte[] ihdr = FindChunk(data, "IHDR");
        Assert.Equal(13, ihdr.Length);
        return new Header(
            BinaryPrimitives.ReadInt32BigEndian(ihdr),
            BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(4)),
            ihdr[8],
            ihdr[9],
            ihdr[12]);
    }

    private static byte[] FindChunk(byte[] data, string type)
    {
        foreach ((string name, byte[] payload) in EnumerateChunks(data))
        {
            if (name == type)
            {
                return payload;
            }
        }

        return Array.Empty<byte>();
    }

    private static List<string> ChunkOrder(byte[] data)
        => EnumerateChunks(data).Select(c => c.Name).ToList();

    private static byte[] ConcatIdat(byte[] data)
    {
        using var buffer = new MemoryStream();
        foreach ((string name, byte[] payload) in EnumerateChunks(data))
        {
            if (name == "IDAT")
            {
                buffer.Write(payload);
            }
        }

        return buffer.ToArray();
    }

    private static List<(string Name, byte[] Payload)> EnumerateChunks(byte[] data)
    {
        var chunks = new List<(string, byte[])>();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, data.Take(8));
        int offset = 8;
        while (offset + 12 <= data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
            string name = Encoding.ASCII.GetString(data, offset + 4, 4);
            byte[] payload = data.AsSpan(offset + 8, length).ToArray();

            uint expected = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 8 + length));
            uint actual = Crc32.Append(Crc32.Append(0, Encoding.ASCII.GetBytes(name)), payload);
            Assert.Equal(expected, actual);

            chunks.Add((name, payload));
            offset += 12 + length;
        }

        return chunks;
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Encode<TPixel>(Image<TPixel> image, PngEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var buffer = new MemoryStream();
        image.Save(buffer, encoder);
        return buffer.ToArray();
    }

    private static Image<Rgba32> RoundTrip(Image<Rgba32> image, PngEncoder encoder)
        => Image.Load<Rgba32>(Encode(image, encoder));

    private static Image<Rgba32> Photo(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32((byte)((x * 9) % 256), (byte)((y * 13) % 256), (byte)(((x + y) * 5) % 256));
            }
        }

        return image;
    }

    private static Image<Rgba32> AlphaImage(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32((byte)(x * 8), (byte)(y * 8), (byte)((x * y) % 256), (byte)(x % 2 == 0 ? 255 : 64));
            }
        }

        return image;
    }

    /// <summary>A four-colour pattern: comfortably inside any palette, so palette output is lossless.</summary>
    private static Image<Rgba32> FewColors(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        Rgba32[] colors = { new(200, 30, 30), new(30, 200, 30), new(30, 30, 200), new(250, 250, 20) };
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = colors[((x / 2) + (y / 2)) % colors.Length];
            }
        }

        return image;
    }

    private static void AssertPixelsEqual(Image<Rgba32> expected, Image<Rgba32> actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                if (expected[x, y] != actual[x, y])
                {
                    Assert.Fail($"Pixel ({x}, {y}): expected {expected[x, y]}, got {actual[x, y]}.");
                }
            }
        }
    }
}
