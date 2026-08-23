using System.Buffers.Binary;
using System.Text;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Bmp;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing.Quantization;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// The BMP encoder options: 1-, 4- and 8-bit colour table output, 16-bit 5-6-5, 24-bit BGR and 32-bit BGRA
/// behind a <c>BITMAPV4HEADER</c>. Header fields are checked byte by byte and every file is decoded back.
/// Pillow read all of them during development and agreed on size, mode, bit depth and pixels.
/// </summary>
public class BmpEncoderOptionsTests
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int V4HeaderSize = 108;

    // ----- Round trips -----

    [Theory]
    [InlineData(BmpBitsPerPixel.Pixel1)]
    [InlineData(BmpBitsPerPixel.Pixel4)]
    [InlineData(BmpBitsPerPixel.Pixel8)]
    [InlineData(BmpBitsPerPixel.Pixel16)]
    [InlineData(BmpBitsPerPixel.Pixel24)]
    [InlineData(BmpBitsPerPixel.Pixel32)]
    public void EveryBitDepthProducesADecodableFileOfTheRightSize(BmpBitsPerPixel bitsPerPixel)
    {
        using Image<Rgba32> source = Photo(23, 15);

        byte[] data = Encode(source, new BmpEncoder { BitsPerPixel = bitsPerPixel });

        Bmp header = ReadHeader(data);
        Assert.Equal((int)bitsPerPixel, header.BitsPerPixel);
        Assert.Equal(23, header.Width);
        Assert.Equal(15, header.Height);
        Assert.Equal(1, header.Planes);

        using Image<Rgba32> decoded = Image.Load<Rgba32>(data);
        Assert.Equal(23, decoded.Width);
        Assert.Equal(15, decoded.Height);
        Assert.Equal(bitsPerPixel, decoded.Metadata.GetBmpMetadata().BitsPerPixel);
    }

    [Fact]
    public void TwentyFourBitOutputIsExact()
    {
        using Image<Rgba32> source = Photo(23, 15);

        using Image<Rgba32> decoded = RoundTrip(source, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel24 });

        AssertPixelsEqual(source, decoded);
    }

    [Fact]
    public void TheDefaultIsStillTwentyFourBit()
    {
        using Image<Rgba32> source = Photo(23, 15);

        byte[] withDefaults = Encode(source, new BmpEncoder());
        byte[] explicitDepth = Encode(source, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel24 });

        Assert.Equal(explicitDepth, withDefaults);
        Assert.Equal(24, ReadHeader(withDefaults).BitsPerPixel);
        Assert.Equal(InfoHeaderSize, ReadHeader(withDefaults).HeaderSize);
    }

    [Fact]
    public void ThirtyTwoBitOutputKeepsTheAlphaChannelExactly()
    {
        using Image<Rgba32> source = AlphaImage(12, 9);

        using Image<Rgba32> decoded = RoundTrip(source, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel32 });

        AssertPixelsEqual(source, decoded);
    }

    [Fact]
    public void ThirtyTwoBitOutputUsesAV4HeaderWithChannelMasks()
    {
        using Image<Rgba32> source = AlphaImage(12, 9);

        byte[] data = Encode(source, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel32 });

        Bmp header = ReadHeader(data);
        Assert.Equal(V4HeaderSize, header.HeaderSize);
        Assert.Equal(3, header.Compression); // BI_BITFIELDS.
        Assert.Equal(0x00FF0000u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(FileHeaderSize + 40)));
        Assert.Equal(0x0000FF00u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(FileHeaderSize + 44)));
        Assert.Equal(0x000000FFu, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(FileHeaderSize + 48)));
        Assert.Equal(0xFF000000u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(FileHeaderSize + 52)));
    }

    [Fact]
    public void SixteenBitOutputUsesFiveSixFiveBitfields()
    {
        using Image<Rgba32> source = Photo(23, 15);

        byte[] data = Encode(source, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel16 });

        Bmp header = ReadHeader(data);
        Assert.Equal(InfoHeaderSize, header.HeaderSize);
        Assert.Equal(3, header.Compression);

        // A 40-byte header carries its masks immediately after it.
        Assert.Equal(0xF800u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(FileHeaderSize + 40)));
        Assert.Equal(0x07E0u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(FileHeaderSize + 44)));
        Assert.Equal(0x001Fu, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(FileHeaderSize + 48)));
        Assert.Equal(FileHeaderSize + InfoHeaderSize + 12, header.DataOffset);
    }

    [Fact]
    public void SixteenBitOutputKeepsTheTopBitsOfEveryChannel()
    {
        using Image<Rgba32> source = Photo(23, 15);

        using Image<Rgba32> decoded = RoundTrip(source, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel16 });

        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Rgba32 a = source[x, y];
                Rgba32 b = decoded[x, y];

                // 5, 6 and 5 bits are kept, so the decoded value is the original rounded to that grid.
                Assert.Equal(Expand(a.R >> 3, 31), b.R);
                Assert.Equal(Expand(a.G >> 2, 63), b.G);
                Assert.Equal(Expand(a.B >> 3, 31), b.B);
                Assert.Equal(255, b.A);
            }
        }
    }

    [Fact]
    public void SixteenBitColoursOnTheGridRoundTripExactly()
    {
        using var source = new Image<Rgba32>(4, 1);
        source[0, 0] = new Rgba32(0, 0, 0);
        source[1, 0] = new Rgba32(255, 255, 255);
        source[2, 0] = new Rgba32(0xFF, 0x00, 0x00);
        source[3, 0] = new Rgba32(0x00, 0xFF, 0x00);

        using Image<Rgba32> decoded = RoundTrip(source, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel16 });

        AssertPixelsEqual(source, decoded);
    }

    // ----- Colour-table output -----

    [Theory]
    [InlineData(BmpBitsPerPixel.Pixel1, 2)]
    [InlineData(BmpBitsPerPixel.Pixel4, 16)]
    [InlineData(BmpBitsPerPixel.Pixel8, 256)]
    public void PaletteOutputWritesAColourTableWithinTheIndexRange(BmpBitsPerPixel bitsPerPixel, int maxEntries)
    {
        using Image<Rgba32> source = Photo(23, 15);

        byte[] data = Encode(source, new BmpEncoder { BitsPerPixel = bitsPerPixel });

        Bmp header = ReadHeader(data);
        Assert.Equal(0, header.Compression); // BI_RGB.
        Assert.InRange(header.ColorsUsed, 1, maxEntries);
        Assert.Equal(FileHeaderSize + InfoHeaderSize + (header.ColorsUsed * 4), header.DataOffset);

        // Every colour-table entry ends with a zero reserved byte.
        for (int i = 0; i < header.ColorsUsed; i++)
        {
            Assert.Equal(0, data[FileHeaderSize + InfoHeaderSize + (i * 4) + 3]);
        }
    }

    [Theory]
    [InlineData(BmpBitsPerPixel.Pixel1)]
    [InlineData(BmpBitsPerPixel.Pixel4)]
    [InlineData(BmpBitsPerPixel.Pixel8)]
    public void PaletteOutputRoundTripsExactlyWhenTheImageFits(BmpBitsPerPixel bitsPerPixel)
    {
        int colors = 1 << (int)bitsPerPixel;
        using Image<Rgba32> source = FewColors(20, 12, Math.Min(colors, 4));

        using Image<Rgba32> decoded = RoundTrip(source, new BmpEncoder { BitsPerPixel = bitsPerPixel });

        AssertPixelsEqual(source, decoded);
    }

    [Fact]
    public void OneBitOutputUsesTwoColours()
    {
        using Image<Rgba32> source = FewColors(16, 8, 2);

        byte[] data = Encode(source, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel1 });

        Assert.Equal(2, ReadHeader(data).ColorsUsed);
        AssertPixelsEqual(source, Image.Load<Rgba32>(data));
    }

    [Fact]
    public void ThePaletteQuantizerIsConfigurable()
    {
        using Image<Rgba32> source = Photo(23, 15);

        using Image<Rgba32> decoded = RoundTrip(
            source,
            new BmpEncoder
            {
                BitsPerPixel = BmpBitsPerPixel.Pixel8,
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

    // ----- Header arithmetic -----

    [Theory]
    [InlineData(BmpBitsPerPixel.Pixel1, 1, 4)]
    [InlineData(BmpBitsPerPixel.Pixel1, 33, 8)]
    [InlineData(BmpBitsPerPixel.Pixel4, 3, 4)]
    [InlineData(BmpBitsPerPixel.Pixel4, 9, 8)]
    [InlineData(BmpBitsPerPixel.Pixel8, 5, 8)]
    [InlineData(BmpBitsPerPixel.Pixel16, 3, 8)]
    [InlineData(BmpBitsPerPixel.Pixel24, 3, 12)]
    [InlineData(BmpBitsPerPixel.Pixel24, 5, 16)]
    [InlineData(BmpBitsPerPixel.Pixel32, 3, 12)]
    public void RowsArePaddedToFourByteBoundaries(BmpBitsPerPixel bitsPerPixel, int width, int expectedStride)
    {
        using Image<Rgba32> source = Photo(width, 7);

        byte[] data = Encode(source, new BmpEncoder { BitsPerPixel = bitsPerPixel });

        Bmp header = ReadHeader(data);
        Assert.Equal(expectedStride * 7, header.ImageSize);
        Assert.Equal(header.DataOffset + header.ImageSize, header.FileSize);
        Assert.Equal(header.FileSize, data.Length);
    }

    [Theory]
    [InlineData(BmpBitsPerPixel.Pixel1)]
    [InlineData(BmpBitsPerPixel.Pixel4)]
    [InlineData(BmpBitsPerPixel.Pixel8)]
    [InlineData(BmpBitsPerPixel.Pixel16)]
    [InlineData(BmpBitsPerPixel.Pixel24)]
    [InlineData(BmpBitsPerPixel.Pixel32)]
    public void TheFileStartsWithTheBmSignature(BmpBitsPerPixel bitsPerPixel)
    {
        using Image<Rgba32> source = Photo(9, 5);

        byte[] data = Encode(source, new BmpEncoder { BitsPerPixel = bitsPerPixel });

        Assert.Equal("BM", Encoding.ASCII.GetString(data, 0, 2));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(6))); // Reserved.
        Assert.Equal(ImageFormat.Bmp, ImageFormatDetector.DetectOrThrow(data));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 7)]
    [InlineData(7, 1)]
    [InlineData(2, 2)]
    public void TinyImagesEncodeAtEveryDepth(int width, int height)
    {
        using Image<Rgba32> source = Photo(width, height);

        foreach (BmpBitsPerPixel depth in Enum.GetValues<BmpBitsPerPixel>())
        {
            using Image<Rgba32> decoded = RoundTrip(source, new BmpEncoder { BitsPerPixel = depth });
            Assert.Equal(width, decoded.Width);
            Assert.Equal(height, decoded.Height);
        }
    }

    // ----- Metadata -----

    [Theory]
    [InlineData(BmpBitsPerPixel.Pixel1)]
    [InlineData(BmpBitsPerPixel.Pixel4)]
    [InlineData(BmpBitsPerPixel.Pixel8)]
    [InlineData(BmpBitsPerPixel.Pixel16)]
    [InlineData(BmpBitsPerPixel.Pixel24)]
    [InlineData(BmpBitsPerPixel.Pixel32)]
    public void TheResolutionIsWrittenAsPixelsPerMetreAtEveryDepth(BmpBitsPerPixel bitsPerPixel)
    {
        using Image<Rgba32> source = Photo(11, 7);
        source.Metadata.SetResolution(150, 300, PixelResolutionUnit.PixelsPerInch);

        byte[] data = Encode(source, new BmpEncoder { BitsPerPixel = bitsPerPixel });

        Bmp header = ReadHeader(data);
        Assert.Equal(5906, header.PixelsPerMeterX); // 150 dpi * 39.3700787.
        Assert.Equal(11811, header.PixelsPerMeterY); // 300 dpi * 39.3700787.

        using Image<Rgba32> decoded = Image.Load<Rgba32>(data);
        Assert.Equal(PixelResolutionUnit.PixelsPerMeter, decoded.Metadata.ResolutionUnits);
        Assert.Equal(150, decoded.Metadata.GetHorizontalResolution(PixelResolutionUnit.PixelsPerInch), 0.02);
        Assert.Equal(300, decoded.Metadata.GetVerticalResolution(PixelResolutionUnit.PixelsPerInch), 0.02);
    }

    [Fact]
    public void ImagesWithoutAResolutionFallBackToNinetySixDpi()
    {
        using Image<Rgba32> source = Photo(9, 5);

        byte[] data = Encode(source, new BmpEncoder());

        Assert.Equal(3780, ReadHeader(data).PixelsPerMeterX);
        Assert.Equal(3780, ReadHeader(data).PixelsPerMeterY);
    }

    [Fact]
    public void IdentifyReportsTheEncodedBitDepth()
    {
        using Image<Rgba32> source = Photo(23, 15);

        byte[] data = Encode(source, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel16 });

        ImageInfo info = Image.Identify(data);
        Assert.Equal(23, info.Width);
        Assert.Equal(15, info.Height);
        Assert.Equal(16, info.PixelType.BitsPerPixel);
        Assert.Equal(BmpBitsPerPixel.Pixel16, info.Metadata.GetBmpMetadata().BitsPerPixel);
    }

    // ----- Validation -----

    [Fact]
    public void UnsupportedBitDepthsAreRejected()
    {
        using Image<Rgba32> source = Photo(8, 8);

        Assert.Throws<NotSupportedException>(() => Encode(source, new BmpEncoder { BitsPerPixel = (BmpBitsPerPixel)2 }));
        Assert.Throws<NotSupportedException>(() => Encode(source, new BmpEncoder { BitsPerPixel = (BmpBitsPerPixel)64 }));
    }

    [Fact]
    public void EncodeRejectsNullArguments()
    {
        var encoder = new BmpEncoder();
        using var image = new Image<Rgba32>(4, 4);

        Assert.Throws<ArgumentNullException>(() => encoder.Encode<Rgba32>(null!, new MemoryStream()));
        Assert.Throws<ArgumentNullException>(() => encoder.Encode(image, null!));
    }

    [Fact]
    public void EncodingIsDeterministic()
    {
        using Image<Rgba32> source = Photo(23, 15);
        var encoder = new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel8 };

        Assert.Equal(Encode(source, encoder), Encode(source, encoder));
    }

    [Fact]
    public void TheSaveAsBmpHelpersUseTheEncoder()
    {
        using Image<Rgba32> source = Photo(11, 7);

        using var withDefaults = new MemoryStream();
        source.SaveAsBmp(withDefaults);
        using var withEncoder = new MemoryStream();
        source.SaveAsBmp(withEncoder, new BmpEncoder());

        Assert.Equal(withDefaults.ToArray(), withEncoder.ToArray());
    }

    [Fact]
    public void ImagesInOtherPixelFormatsEncodeToo()
    {
        using Image<Rgb24> source = TestImages.Gradient(12, 9);

        using var buffer = new MemoryStream();
        source.Save(buffer, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel32 });

        using Image<Rgba32> decoded = Image.Load<Rgba32>(buffer.ToArray());
        Assert.Equal(12, decoded.Width);
        for (int x = 0; x < 12; x++)
        {
            Assert.Equal(255, decoded[x, 0].A);
        }
    }

    [Fact]
    public void RowsAreStoredBottomUp()
    {
        using var source = new Image<Rgba32>(2, 2);
        source[0, 0] = new Rgba32(1, 2, 3);
        source[1, 0] = new Rgba32(4, 5, 6);
        source[0, 1] = new Rgba32(7, 8, 9);
        source[1, 1] = new Rgba32(10, 11, 12);

        byte[] data = Encode(source, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel24 });

        // The first row of pixel data is the bottom row of the image, stored as BGR triples.
        int offset = ReadHeader(data).DataOffset;
        Assert.Equal(new byte[] { 9, 8, 7, 12, 11, 10 }, data.AsSpan(offset, 6).ToArray());
        AssertPixelsEqual(source, Image.Load<Rgba32>(data));
    }

    // ----- Helpers -----

    private readonly record struct Bmp(
        int FileSize, int DataOffset, int HeaderSize, int Width, int Height, short Planes,
        int BitsPerPixel, int Compression, int ImageSize, int PixelsPerMeterX, int PixelsPerMeterY, int ColorsUsed);

    private static Bmp ReadHeader(byte[] data) => new(
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(2)),
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(10)),
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(14)),
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18)),
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22)),
        BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(26)),
        BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(28)),
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(30)),
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(34)),
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(38)),
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(42)),
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(46)));

    /// <summary>Scales a truncated channel back to 0-255 the way the decoder does.</summary>
    private static byte Expand(int value, int max) => (byte)(((value * 255) + (max / 2)) / max);

    private static byte[] Encode<TPixel>(Image<TPixel> image, BmpEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var buffer = new MemoryStream();
        image.Save(buffer, encoder);
        return buffer.ToArray();
    }

    private static Image<Rgba32> RoundTrip(Image<Rgba32> image, BmpEncoder encoder)
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

    private static Image<Rgba32> FewColors(int width, int height, int colorCount)
    {
        Rgba32[] colors = { new(200, 30, 30), new(30, 200, 30), new(30, 30, 200), new(250, 250, 20) };
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = colors[((x / 2) + (y / 2)) % colorCount];
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
