using System.Text;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Pbm;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Netpbm (PBM/PGM/PPM/PAM): fixtures under <c>Fixtures/smallformats/pbm/</c> decode exactly, hand-written
/// headers cover the tokenizer corners, and the encoder round-trips every colour type / encoding / component
/// width combination.
/// </summary>
public class PbmTests
{
    private const string Folder = "smallformats/pbm";

    public static IEnumerable<object[]> Fixtures => SmallFormatFixtures.Names(Folder);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_DecodesToReference(string name) => SmallFormatFixtures.Verify(Folder, name);

    [Fact]
    public void Manifest_IsPresentAndNonEmpty() => SmallFormatFixtures.AssertManifest(Folder, minimumEntries: 20);

    [Theory]
    [InlineData("hand_p7_rgb_alpha")]
    [InlineData("pil_p5_gray16")]
    [InlineData("hand_p1_bilevel_comments")]
    public void Fixture_DecodesIntoOtherPixelFormats(string name)
    {
        byte[] bytes = SmallFormatFixtures.Bytes(Folder, name);
        using Image<Rgb24> rgb = Image.Load<Rgb24>(bytes);
        using Image<L8> gray = Image.Load<L8>(bytes);
        using Image<Bgra32> bgra = Image.Load<Bgra32>(bytes);
        Assert.Equal(rgb.Width, gray.Width);
        Assert.Equal(rgb.Height, bgra.Height);
    }

    // ----- Hand-written inline inputs -----

    [Fact]
    public void PlainBitmap_ParsesUnseparatedDigitsAndComments()
    {
        byte[] data = Encoding.ASCII.GetBytes("P1\n# comment\n3 2 # trailing\n10\n1 # mid-raster comment\n0 1 1\n");
        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        Assert.Equal((3, 2), (image.Width, image.Height));
        Assert.Equal(Rgba32.Black, image[0, 0]);
        Assert.Equal(Rgba32.White, image[1, 0]);
        Assert.Equal(Rgba32.Black, image[2, 0]);
        Assert.Equal(Rgba32.White, image[0, 1]);
        Assert.Equal(Rgba32.Black, image[1, 1]);
        Assert.Equal(Rgba32.Black, image[2, 1]);
    }

    [Fact]
    public void PlainGraymap_ScalesSmallMaxval()
    {
        using Image<L8> image = Image.Load<L8>(Encoding.ASCII.GetBytes("P2 4 1 3\n0 1 2 3\n"));
        Assert.Equal(new byte[] { 0, 85, 170, 255 }, new[] { image[0, 0].PackedValue, image[1, 0].PackedValue, image[2, 0].PackedValue, image[3, 0].PackedValue });
    }

    [Fact]
    public void BinaryPixmap_WithSingleWhitespaceHeader()
    {
        byte[] data = Encoding.ASCII.GetBytes("P6 2 1 255 ").Concat(new byte[] { 1, 2, 3, 250, 251, 252 }).ToArray();
        using Image<Rgb24> image = Image.Load<Rgb24>(data);
        Assert.Equal(new Rgb24(1, 2, 3), image[0, 0]);
        Assert.Equal(new Rgb24(250, 251, 252), image[1, 0]);
    }

    [Fact]
    public void Sixteen_BitSamples_AreScaledWithRounding()
    {
        // maxval 65535: 0x0101 (257) -> 1, 0x8000 -> 128, 0xFFFF -> 255; maxval 1000: 500 -> 128 (127.5 rounds up).
        byte[] data = Encoding.ASCII.GetBytes("P5\n3 1\n65535\n").Concat(new byte[] { 0x01, 0x01, 0x80, 0x00, 0xFF, 0xFF }).ToArray();
        using Image<L8> image = Image.Load<L8>(data);
        Assert.Equal(new byte[] { 1, 128, 255 }, new[] { image[0, 0].PackedValue, image[1, 0].PackedValue, image[2, 0].PackedValue });

        using Image<L8> plain = Image.Load<L8>(Encoding.ASCII.GetBytes("P2\n1 1\n1000\n500\n"));
        Assert.Equal(128, plain[0, 0].PackedValue);
    }

    [Fact]
    public void Pam_HeaderKeywordsInAnyOrderWithComments()
    {
        byte[] data = Encoding.ASCII.GetBytes("P7\n# leading comment\nMAXVAL 255\nDEPTH 2\nTUPLTYPE GRAYSCALE_ALPHA\nHEIGHT 1\nWIDTH 2\nENDHDR\n")
            .Concat(new byte[] { 10, 200, 20, 0 }).ToArray();
        using Image<Rgba32> image = Image.Load<Rgba32>(data);
        Assert.Equal(new Rgba32(10, 10, 10, 200), image[0, 0]);
        Assert.Equal(new Rgba32(20, 20, 20, 0), image[1, 0]);
        Assert.Equal(16, Image.Identify(data).PixelType.BitsPerPixel);
    }

    [Theory]
    [InlineData("P7\nWIDTH 2\nHEIGHT 1\nDEPTH 3\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n", "InvalidImageContentException")]
    [InlineData("P7\nWIDTH 2\nHEIGHT 1\nDEPTH 3\nMAXVAL 255\n", "InvalidImageContentException")]
    [InlineData("P7\nWIDTH 2\nHEIGHT 1\nDEPTH 6\nMAXVAL 255\nENDHDR\n", "NotSupportedException")]
    [InlineData("P6\n0 4\n255\n", "InvalidImageContentException")]
    [InlineData("P6\n2 2\n255", "InvalidImageContentException")]
    [InlineData("P6\n2 2\n255\nabc", "InvalidImageContentException")]
    [InlineData("P3\n1 1\n255\n1 2\n", "InvalidImageContentException")]
    [InlineData("P3\n1 1\n255\n1 x 3\n", "InvalidImageContentException")]
    [InlineData("P1\n2 1\n1 2\n", "InvalidImageContentException")]
    [InlineData("P5\n1 1\n99999999999\n0", "InvalidImageContentException")]
    public void Malformed_Inputs_ThrowTheDocumentedType(string text, string exceptionType)
    {
        byte[] data = Encoding.ASCII.GetBytes(text);
        Exception ex = Assert.ThrowsAny<Exception>(() => Image.Load(data));
        Assert.Equal(exceptionType, ex.GetType().Name);
    }

    [Fact]
    public void HugeDimensions_AreRejectedBeforeAllocation()
    {
        byte[] data = Encoding.ASCII.GetBytes("P6\n1000000 1000000\n255\n");
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load(data));
        ImageInfo info = Image.Identify(data);
        Assert.Equal(1000000, info.Width);
    }

    [Fact]
    public void MultiImageStream_RespectsMaxFrames()
    {
        byte[] bytes = SmallFormatFixtures.Bytes(Folder, "hand_p6_two_images");
        Assert.Equal(2, Image.Identify(bytes).FrameCount);
        using Image<Rgba32> all = Image.Load<Rgba32>(bytes);
        Assert.Equal(2, all.Frames.Count);
        using Image<Rgba32> first = Image.Load<Rgba32>(bytes, new DecoderOptions { MaxFrames = 1 });
        Assert.Single(first.Frames);
        Assert.Equal((5, 4), (first.Width, first.Height));
    }

    [Fact]
    public void TrailingGarbage_AfterRaster_IsIgnored()
    {
        byte[] data = Encoding.ASCII.GetBytes("P5 1 1 255 ").Concat(new byte[] { 7, 1, 2, 3, 4 }).ToArray();
        using Image<L8> image = Image.Load<L8>(data);
        Assert.Equal(7, image[0, 0].PackedValue);
        Assert.Equal(1, Image.Identify(data).FrameCount);
    }

    // ----- Encoder -----

    [Theory]
    [InlineData(PbmEncoding.Binary, PbmComponentType.Byte)]
    [InlineData(PbmEncoding.Plain, PbmComponentType.Byte)]
    [InlineData(PbmEncoding.Binary, PbmComponentType.Short)]
    [InlineData(PbmEncoding.Plain, PbmComponentType.Short)]
    public void Rgb_RoundTripsExactly(PbmEncoding encoding, PbmComponentType component)
    {
        using Image<Rgb24> original = TestImages.Gradient(29, 13);
        byte[] bytes = Encode(original, new PbmEncoder { Encoding = encoding, ComponentType = component });
        Assert.Equal(ImageFormat.Pbm, Image.DetectFormat(bytes));
        Assert.Equal(encoding == PbmEncoding.Binary ? (byte)'6' : (byte)'3', bytes[1]);
        using Image<Rgb24> decoded = Image.Load<Rgb24>(bytes);
        Assert.Equal(0, TestImages.AveragePixelDifference(original, decoded));
    }

    [Theory]
    [InlineData(PbmEncoding.Binary, PbmComponentType.Byte)]
    [InlineData(PbmEncoding.Plain, PbmComponentType.Byte)]
    [InlineData(PbmEncoding.Binary, PbmComponentType.Short)]
    public void Gray_RoundTripsExactly(PbmEncoding encoding, PbmComponentType component)
    {
        using var original = new Image<L8>(70, 3);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 70; x++)
            {
                original[x, y] = new L8((byte)((x * 3) + y));
            }
        }

        byte[] bytes = Encode(original, new PbmEncoder { Encoding = encoding, ComponentType = component });
        Assert.Equal(encoding == PbmEncoding.Binary ? (byte)'5' : (byte)'2', bytes[1]);
        using Image<L8> decoded = Image.Load<L8>(bytes);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 70; x++)
            {
                Assert.Equal(original[x, y], decoded[x, y]);
            }
        }
    }

    [Theory]
    [InlineData(PbmEncoding.Binary)]
    [InlineData(PbmEncoding.Plain)]
    public void BlackAndWhite_ThresholdsAndRoundTrips(PbmEncoding encoding)
    {
        using Image<Rgb24> original = TestImages.Gradient(75, 5); // Wider than 70 so plain rows wrap.
        byte[] bytes = Encode(original, new PbmEncoder { ColorType = PbmColorType.BlackAndWhite, Encoding = encoding });
        Assert.Equal(encoding == PbmEncoding.Binary ? (byte)'4' : (byte)'1', bytes[1]);
        using Image<L8> decoded = Image.Load<L8>(bytes);
        for (int y = 0; y < original.Height; y++)
        {
            for (int x = 0; x < original.Width; x++)
            {
                byte lum = L8.FromRgba32(original[x, y].ToRgba32()).PackedValue;
                Assert.Equal(lum < 128 ? 0 : 255, decoded[x, y].PackedValue);
            }
        }
    }

    [Fact]
    public void Plain_LinesNeverExceed70Characters()
    {
        using Image<Rgb24> image = TestImages.Gradient(64, 4);
        string text = Encoding.ASCII.GetString(Encode(image, new PbmEncoder { Encoding = PbmEncoding.Plain, ComponentType = PbmComponentType.Short }));
        Assert.StartsWith("P3\n64 4\n65535\n", text);
        Assert.All(text.Split('\n'), line => Assert.True(line.Length <= 70, $"line too long: {line.Length}"));
    }

    [Fact]
    public void Encoder_ColorTypeDefaultsFromPixelFormat_AndAlphaIsDropped()
    {
        using var gray = new Image<L8>(3, 2);
        using Image<Rgba32> rgba = TestImages.AlphaGradient(3, 2);
        Assert.StartsWith("P5\n3 2\n255\n", Encoding.ASCII.GetString(Encode(gray, new PbmEncoder())));
        byte[] ppm = Encode(rgba, new PbmEncoder());
        Assert.StartsWith("P6\n3 2\n255\n", Encoding.ASCII.GetString(ppm));
        Assert.Equal(11 + (3 * 2 * 3), ppm.Length);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(ppm);
        Assert.Equal(255, decoded[0, 1].A);
        Assert.Equal(rgba[2, 1].R, decoded[2, 1].R);
    }

    /// <summary>Fixed outputs, verified once with an independent reader during development.</summary>
    [Theory]
    [InlineData(PbmColorType.Rgb, PbmEncoding.Binary, PbmComponentType.Byte, 3232, "f741c7539bde33ec")]
    [InlineData(PbmColorType.Grayscale, PbmEncoding.Plain, PbmComponentType.Byte, 3875, "8303818347d58d1d")]
    [InlineData(PbmColorType.BlackAndWhite, PbmEncoding.Binary, PbmComponentType.Byte, 154, "e5b9c9ad0ae86b40")]
    public void Encoder_OutputIsStable(PbmColorType colorType, PbmEncoding encoding, PbmComponentType component, int expectedLength, string expectedHashPrefix)
    {
        using Image<Rgb24> image = TestImages.Gradient(37, 29);
        byte[] bytes = Encode(image, new PbmEncoder { ColorType = colorType, Encoding = encoding, ComponentType = component });
        Assert.Equal(expectedLength, bytes.Length);
        Assert.Equal(expectedHashPrefix, SmallFormatFixtures.Sha256Prefix(bytes));
    }

    [Fact]
    public async Task SaveByExtension_And_Async_WritePbm()
    {
        using Image<Rgb24> image = TestImages.Gradient(11, 7);
        string path = Path.Combine(Path.GetTempPath(), $"eis-{Guid.NewGuid():N}.ppm");
        try
        {
            image.Save(path);
            using Image<Rgb24> reloaded = Image.Load<Rgb24>(path);
            Assert.Equal(0, TestImages.AveragePixelDifference(image, reloaded));
        }
        finally
        {
            File.Delete(path);
        }

        using var ms = new MemoryStream();
        await image.SaveAsPbmAsync(ms, new PbmEncoder { ColorType = PbmColorType.Grayscale });
        Assert.StartsWith("P5\n11 7\n255\n", Encoding.ASCII.GetString(ms.ToArray()));
    }

    [Fact]
    public void Identify_ReportsBitsPerPixel()
    {
        Assert.Equal(24, Image.Identify(SmallFormatFixtures.Bytes(Folder, "pil_p6_rgb")).PixelType.BitsPerPixel);
        Assert.Equal(8, Image.Identify(SmallFormatFixtures.Bytes(Folder, "pil_p5_gray")).PixelType.BitsPerPixel);
        Assert.Equal(1, Image.Identify(SmallFormatFixtures.Bytes(Folder, "pil_p4_bilevel")).PixelType.BitsPerPixel);
        Assert.Equal(16, Image.Identify(SmallFormatFixtures.Bytes(Folder, "pil_p5_gray16")).PixelType.BitsPerPixel);
        Assert.Equal(48, Image.Identify(SmallFormatFixtures.Bytes(Folder, "hand_p6_rgb16")).PixelType.BitsPerPixel);
        Assert.Equal(32, Image.Identify(SmallFormatFixtures.Bytes(Folder, "hand_p7_rgb_alpha")).PixelType.BitsPerPixel);
        Assert.Equal("PBM", Image.Identify(SmallFormatFixtures.Bytes(Folder, "hand_p7_rgb_alpha")).Format.Name);
    }

    [Fact]
    public void Detection_RequiresMagicPlusWhitespace()
    {
        Assert.True(ImageFormat.Pbm.Matches("P6\n1 1 255 xxx"u8));
        Assert.True(ImageFormat.Pbm.Matches("P1#c\n1 1 0"u8));
        Assert.False(ImageFormat.Pbm.Matches("P8\n"u8));
        Assert.False(ImageFormat.Pbm.Matches("P6x"u8));
        Assert.False(ImageFormat.Pbm.Matches("PNG"u8));
        Assert.Throws<UnknownImageFormatException>(() => Image.Load("P0\n1 1\n"u8.ToArray()));
    }

    private static byte[] Encode<TPixel>(Image<TPixel> image, PbmEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var ms = new MemoryStream();
        image.Save(ms, encoder);
        return ms.ToArray();
    }
}
