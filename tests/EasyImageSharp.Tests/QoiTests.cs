using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Qoi;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// QOI: fixtures under <c>Fixtures/smallformats/qoi/</c> were produced by an independent Python reference
/// encoder written from the specification. The decoder must reproduce their <c>.rgba</c> dumps and the
/// encoder must reproduce the fixture bytes exactly from those dumps (same greedy chunk selection as the
/// reference implementation).
/// </summary>
public class QoiTests
{
    private const string Folder = "smallformats/qoi";

    public static IEnumerable<object[]> Fixtures => SmallFormatFixtures.Names(Folder);

    public static IEnumerable<object[]> ReferenceEncodedFixtures
        => SmallFormatFixtures.Names(Folder).Where(n => ((string)n[0]).StartsWith("ref_", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_DecodesToReference(string name) => SmallFormatFixtures.Verify(Folder, name);

    [Fact]
    public void Manifest_IsPresentAndNonEmpty() => SmallFormatFixtures.AssertManifest(Folder, minimumEntries: 12);

    [Theory]
    [MemberData(nameof(ReferenceEncodedFixtures))]
    public void Encoder_MatchesReferenceEncoderByteForByte(string name)
    {
        byte[] expected = SmallFormatFixtures.Bytes(Folder, name);
        int channels = SmallFormatFixtures.Fact(Folder, name, "channels");
        int colorSpace = SmallFormatFixtures.Fact(Folder, name, "colorspace");
        using Image<Rgba32> image = SmallFormatFixtures.LoadExpected(Folder, name);

        byte[] actual = Encode(image, new QoiEncoder { Channels = (QoiChannels)channels, ColorSpace = (QoiColorSpace)colorSpace });
        Assert.True(expected.AsSpan().SequenceEqual(actual),
            $"{name}: encoder output differs from the reference encoder (length {actual.Length} vs {expected.Length}, first difference at byte {expected.AsSpan().CommonPrefixLength(actual)}).");
    }

    [Theory]
    [InlineData("ref_rgba_alpha")]
    [InlineData("ref_index_palette")]
    public void Fixture_DecodesIntoOtherPixelFormats(string name)
    {
        byte[] bytes = SmallFormatFixtures.Bytes(Folder, name);
        using Image<Rgb24> rgb = Image.Load<Rgb24>(bytes);
        using Image<L8> gray = Image.Load<L8>(bytes);
        using Image<Bgra32> bgra = Image.Load<Bgra32>(bytes);
        Assert.Equal(rgb.Width, gray.Width);
        Assert.Equal(rgb.Height, bgra.Height);
    }

    // ----- Round trips -----

    [Fact]
    public void Rgba_RoundTripsExactly()
    {
        using Image<Rgba32> original = TestImages.AlphaGradient(53, 31);
        byte[] bytes = Encode(original, new QoiEncoder());
        Assert.Equal(ImageFormat.Qoi, Image.DetectFormat(bytes));
        Assert.Equal(4, bytes[12]);
        Assert.Equal(0, bytes[13]);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(bytes);
        for (int y = 0; y < original.Height; y++)
        {
            for (int x = 0; x < original.Width; x++)
            {
                Assert.Equal(original[x, y], decoded[x, y]);
            }
        }
    }

    [Fact]
    public void Rgb_And_Gray_RoundTripExactly()
    {
        using Image<Rgb24> rgb = TestImages.Gradient(64, 48);
        byte[] rgbBytes = Encode(rgb, new QoiEncoder());
        Assert.Equal(3, rgbBytes[12]);
        using Image<Rgb24> rgbDecoded = Image.Load<Rgb24>(rgbBytes);
        Assert.Equal(0, TestImages.AveragePixelDifference(rgb, rgbDecoded));

        using var gray = new Image<L8>(17, 9);
        for (int y = 0; y < 9; y++)
        {
            for (int x = 0; x < 17; x++)
            {
                gray[x, y] = new L8((byte)(x * 15 + y));
            }
        }

        byte[] grayBytes = Encode(gray, new QoiEncoder());
        using Image<L8> grayDecoded = Image.Load<L8>(grayBytes);
        for (int y = 0; y < 9; y++)
        {
            for (int x = 0; x < 17; x++)
            {
                Assert.Equal(gray[x, y], grayDecoded[x, y]);
            }
        }

        using Image<Bgra32> bgra = TestImages.AlphaGradient(9, 9).CloneAs<Bgra32>();
        byte[] bgraBytes = Encode(bgra, new QoiEncoder { ColorSpace = QoiColorSpace.Linear });
        Assert.Equal(1, bgraBytes[13]);
        using Image<Bgra32> bgraDecoded = Image.Load<Bgra32>(bgraBytes);
        Assert.Equal(bgra[8, 8], bgraDecoded[8, 8]);
    }

    [Fact]
    public void ThreeChannelOutput_ForcesOpaqueAlpha()
    {
        using Image<Rgba32> original = TestImages.AlphaGradient(10, 6);
        byte[] bytes = Encode(original, new QoiEncoder { Channels = QoiChannels.Rgb });
        Assert.Equal(3, bytes[12]);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(bytes);
        for (int y = 0; y < original.Height; y++)
        {
            for (int x = 0; x < original.Width; x++)
            {
                Rgba32 want = original[x, y];
                want.A = 255;
                Assert.Equal(want, decoded[x, y]);
            }
        }
    }

    [Fact]
    public void ExactChunkStream_ForTinyImage()
    {
        // (0,0,0) equals the initial state -> run of 1; then (255,255,255): each channel diff is -1 -> QOI_OP_DIFF 0x55.
        using var image = new Image<Rgb24>(2, 1);
        image[1, 0] = new Rgb24(255, 255, 255);
        byte[] bytes = Encode(image, new QoiEncoder());
        byte[] expected =
        {
            (byte)'q', (byte)'o', (byte)'i', (byte)'f', 0, 0, 0, 2, 0, 0, 0, 1, 3, 0,
            0xC0, 0x55,
            0, 0, 0, 0, 0, 0, 0, 1,
        };
        Assert.Equal(expected, bytes);
        using Image<Rgb24> decoded = Image.Load<Rgb24>(bytes);
        Assert.Equal(new Rgb24(255, 255, 255), decoded[1, 0]);
    }

    [Fact]
    public void Runs_SplitAt62_AndFlushAtEndOfImage()
    {
        using var image = new Image<Rgba32>(100, 1, new Rgba32(0, 0, 0, 255));
        byte[] bytes = Encode(image, new QoiEncoder());
        // 100 pixels equal to the initial state: RUN 62 + RUN 38, then the end marker.
        Assert.Equal(14 + 2 + 8, bytes.Length);
        Assert.Equal(0xC0 | 61, bytes[14]);
        Assert.Equal(0xC0 | 37, bytes[15]);
    }

    /// <summary>Fixed outputs; the reference-encoder theory above already proves the byte layout, this guards the C# path alone.</summary>
    [Fact]
    public void Encoder_OutputIsStable()
    {
        using Image<Rgb24> image = TestImages.Gradient(37, 29);
        byte[] bytes = Encode(image, new QoiEncoder());
        Assert.Equal(2397, bytes.Length);
        Assert.Equal("06fc4ea63dca9a2e", SmallFormatFixtures.Sha256Prefix(bytes));
    }

    [Fact]
    public async Task SaveByExtension_And_Async_WriteQoi()
    {
        using Image<Rgba32> image = TestImages.AlphaGradient(12, 8);
        string path = Path.Combine(Path.GetTempPath(), $"eis-{Guid.NewGuid():N}.qoi");
        try
        {
            image.Save(path);
            using Image<Rgba32> reloaded = Image.Load<Rgba32>(path);
            Assert.Equal(image[5, 5], reloaded[5, 5]);
        }
        finally
        {
            File.Delete(path);
        }

        using var ms = new MemoryStream();
        await image.SaveAsQoiAsync(ms);
        Assert.True(ms.ToArray().AsSpan(0, 4).SequenceEqual("qoif"u8));
    }

    // ----- Identify / detection / malformed input -----

    [Fact]
    public void Identify_ReportsChannelsAsBitsPerPixel()
    {
        ImageInfo info = Image.Identify(SmallFormatFixtures.Bytes(Folder, "ref_rgba_alpha"));
        Assert.Equal((29, 21, 32, 1, "QOI"), (info.Width, info.Height, info.PixelType.BitsPerPixel, info.FrameCount, info.Format.Name));
        Assert.Equal(24, Image.Identify(SmallFormatFixtures.Bytes(Folder, "ref_rgb_gradient")).PixelType.BitsPerPixel);
    }

    [Fact]
    public void HugeDimensions_AreRejectedBeforeAllocation()
    {
        byte[] header = { (byte)'q', (byte)'o', (byte)'i', (byte)'f', 0x7F, 0xFF, 0xFF, 0xFF, 0x7F, 0xFF, 0xFF, 0xFF, 4, 0, 0, 0, 0, 0, 0, 0, 0, 1 };
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load(header));
        Assert.Equal(int.MaxValue, Image.Identify(header).Width);

        header[4] = 0x80; // width > int.MaxValue
        Assert.Throws<InvalidImageContentException>(() => Image.Load(header));
    }

    [Theory]
    [InlineData(new byte[] { (byte)'q', (byte)'o', (byte)'i', (byte)'f', 0, 0, 0, 1, 0, 0, 0, 1, 4, 2, 0xC0, 0, 0, 0, 0, 0, 0, 0, 1 })] // colourspace 2
    [InlineData(new byte[] { (byte)'q', (byte)'o', (byte)'i', (byte)'f', 0, 0, 0, 1, 0, 0, 0, 1, 4, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0, 0 })] // bad end marker
    [InlineData(new byte[] { (byte)'q', (byte)'o', (byte)'i', (byte)'f', 0, 0, 0, 1, 0, 0, 0, 1, 4, 0, 0xFE, 1, 2 })]                  // truncated RGB chunk
    [InlineData(new byte[] { (byte)'q', (byte)'o', (byte)'i', (byte)'f', 0, 0, 0, 1, 0, 0, 0, 1, 4, 0, 0xC1, 0, 0, 0, 0, 0, 0, 0, 1 })] // run of 2 for 1 pixel
    [InlineData(new byte[] { (byte)'q', (byte)'o', (byte)'i', (byte)'f', 0, 0, 0, 1 })]                                                // truncated header
    public void Malformed_Streams_AreInvalid(byte[] data)
        => Assert.Throws<InvalidImageContentException>(() => Image.Load(data));

    [Fact]
    public void Detection_RequiresSignature()
    {
        Assert.True(ImageFormat.Qoi.Matches("qoif"u8));
        Assert.False(ImageFormat.Qoi.Matches("qoi"u8));
        Assert.False(ImageFormat.Qoi.Matches("QOIF...."u8));
        Assert.Contains("qoi", ImageFormat.Qoi.FileExtensions);
    }

    private static byte[] Encode<TPixel>(Image<TPixel> image, QoiEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var ms = new MemoryStream();
        image.Save(ms, encoder);
        return ms.ToArray();
    }
}
