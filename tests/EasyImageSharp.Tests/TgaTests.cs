using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Tga;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// TGA: every fixture under <c>Fixtures/smallformats/tga/</c> (Pillow-written or hand-assembled by
/// <c>gen_smallformats.py</c>) decodes pixel-exactly to its <c>.rgba</c> dump, the encoder round-trips every
/// depth/compression combination, and format detection stays strict for a format without a magic number.
/// </summary>
public class TgaTests
{
    private const string Folder = "smallformats/tga";

    public static IEnumerable<object[]> Fixtures => SmallFormatFixtures.Names(Folder);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_DecodesToReference(string name) => SmallFormatFixtures.Verify(Folder, name);

    [Fact]
    public void Manifest_IsPresentAndNonEmpty() => SmallFormatFixtures.AssertManifest(Folder, minimumEntries: 20);

    [Theory]
    [InlineData("pil_rgb24_rle_tl")]
    [InlineData("hand_pal8_map16_first4_rle_tr")]
    [InlineData("hand_rgb16_alpha1_rle")]
    public void Fixture_DecodesIntoOtherPixelFormats(string name)
    {
        byte[] bytes = SmallFormatFixtures.Bytes(Folder, name);
        using Image<Rgb24> rgb = Image.Load<Rgb24>(bytes);
        using Image<L8> gray = Image.Load<L8>(bytes);
        using Image<Bgra32> bgra = Image.Load<Bgra32>(bytes);
        Assert.Equal(rgb.Width, gray.Width);
        Assert.Equal(rgb.Height, bgra.Height);
    }

    // ----- Encoder round trips -----

    [Theory]
    [InlineData(TgaBitsPerPixel.Pixel24, TgaCompression.None)]
    [InlineData(TgaBitsPerPixel.Pixel24, TgaCompression.RunLength)]
    [InlineData(TgaBitsPerPixel.Pixel32, TgaCompression.None)]
    [InlineData(TgaBitsPerPixel.Pixel32, TgaCompression.RunLength)]
    public void Rgba_RoundTripsExactly(TgaBitsPerPixel bits, TgaCompression compression)
    {
        using Image<Rgba32> original = TestImages.AlphaGradient(37, 23);
        byte[] encoded = Encode(original, new TgaEncoder { BitsPerPixel = bits, Compression = compression });
        Assert.Equal(ImageFormat.Tga, Image.DetectFormat(encoded));

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        for (int y = 0; y < original.Height; y++)
        {
            for (int x = 0; x < original.Width; x++)
            {
                Rgba32 want = original[x, y];
                if (bits == TgaBitsPerPixel.Pixel24)
                {
                    want.A = 255;
                }

                Assert.Equal(want, decoded[x, y]);
            }
        }
    }

    [Theory]
    [InlineData(TgaCompression.None)]
    [InlineData(TgaCompression.RunLength)]
    public void Gray_RoundTripsExactly(TgaCompression compression)
    {
        using var original = new Image<L8>(41, 9);
        for (int y = 0; y < original.Height; y++)
        {
            for (int x = 0; x < original.Width; x++)
            {
                original[x, y] = new L8((byte)((x * 5) + (y * 3)));
            }
        }

        byte[] encoded = Encode(original, new TgaEncoder { Compression = compression });
        Assert.Equal(compression == TgaCompression.RunLength ? 11 : 3, encoded[2]); // Grayscale image type.
        Assert.Equal(8, encoded[16]);
        using Image<L8> decoded = Image.Load<L8>(encoded);
        for (int y = 0; y < original.Height; y++)
        {
            for (int x = 0; x < original.Width; x++)
            {
                Assert.Equal(original[x, y], decoded[x, y]);
            }
        }
    }

    [Fact]
    public void SixteenBit_QuantisesTo555()
    {
        using Image<Rgb24> original = TestImages.Gradient(31, 17);
        byte[] encoded = Encode(original, new TgaEncoder { BitsPerPixel = TgaBitsPerPixel.Pixel16, Compression = TgaCompression.None });
        Assert.Equal(16, encoded[16]);
        Assert.Equal(0, encoded[17]); // No attribute bits: opaque.
        Assert.Equal(18 + (31 * 17 * 2) + 26, encoded.Length);

        using Image<Rgb24> decoded = Image.Load<Rgb24>(encoded);
        for (int y = 0; y < original.Height; y++)
        {
            for (int x = 0; x < original.Width; x++)
            {
                Rgb24 p = original[x, y];
                var want = new Rgb24(Widen5(p.R >> 3), Widen5(p.G >> 3), Widen5(p.B >> 3));
                Assert.Equal(want, decoded[x, y]);
            }
        }
    }

    [Fact]
    public void Encoder_DefaultsDependOnPixelFormat()
    {
        using Image<Rgb24> rgb = TestImages.Gradient(8, 4);
        using Image<Rgba32> rgba = TestImages.AlphaGradient(8, 4);
        using var gray = new Image<L8>(8, 4);

        byte[] rgbBytes = Encode(rgb, new TgaEncoder());
        byte[] rgbaBytes = Encode(rgba, new TgaEncoder());
        byte[] grayBytes = Encode(gray, new TgaEncoder());

        Assert.Equal((10, 24, 0), (rgbBytes[2], rgbBytes[16], rgbBytes[17]));   // RLE truecolor, bottom-left, no alpha bits.
        Assert.Equal((10, 32, 8), (rgbaBytes[2], rgbaBytes[16], rgbaBytes[17])); // 8 attribute bits.
        Assert.Equal((11, 8, 0), (grayBytes[2], grayBytes[16], grayBytes[17]));  // RLE grayscale.
        Assert.True(rgbBytes.AsSpan()[^18..].SequenceEqual("TRUEVISION-XFILE.\0"u8));
    }

    [Fact]
    public void Uncompressed_HasExactLength()
    {
        using Image<Rgb24> image = TestImages.Gradient(13, 7);
        byte[] bytes = Encode(image, new TgaEncoder { Compression = TgaCompression.None });
        Assert.Equal(18 + (13 * 7 * 3) + 26, bytes.Length);
        Assert.Equal(2, bytes[2]);
    }

    /// <summary>Fixed outputs, verified once with an independent reader during development; guards against silent encoder drift.</summary>
    [Theory]
    [InlineData(TgaBitsPerPixel.Pixel24, TgaCompression.RunLength, 3292, "5c34940c253976db")]
    [InlineData(TgaBitsPerPixel.Pixel32, TgaCompression.None, 4336, "12794aab78a52982")]
    [InlineData(TgaBitsPerPixel.Pixel8, TgaCompression.RunLength, 1146, "dac4988b05c1ef89")]
    public void Encoder_OutputIsStable(TgaBitsPerPixel bits, TgaCompression compression, int expectedLength, string expectedHashPrefix)
    {
        using Image<Rgb24> image = TestImages.Gradient(37, 29);
        byte[] bytes = Encode(image, new TgaEncoder { BitsPerPixel = bits, Compression = compression });
        Assert.Equal(expectedLength, bytes.Length);
        Assert.Equal(expectedHashPrefix, SmallFormatFixtures.Sha256Prefix(bytes));
    }

    [Fact]
    public void RunLength_PacketsNeverSpanRowsAndCompressFlatRows()
    {
        using var image = new Image<Rgb24>(300, 3, new Rgb24(9, 8, 7));
        image[299, 1] = new Rgb24(1, 2, 3);
        byte[] bytes = Encode(image, new TgaEncoder());

        // Row 0 (stored first, bottom-up = image row 2): 300 identical pixels -> packets of 128, 128, 44.
        int pos = 18;
        Assert.Equal(0x80 | 127, bytes[pos]);
        pos += 4;
        Assert.Equal(0x80 | 127, bytes[pos]);
        pos += 4;
        Assert.Equal(0x80 | 43, bytes[pos]);
        pos += 4;

        // Row 1 (image row 1): 299 identical then one different pixel -> 128, 128, 43 run packets and a raw packet of 1.
        Assert.Equal(0x80 | 127, bytes[pos]);
        pos += 4;
        Assert.Equal(0x80 | 127, bytes[pos]);
        pos += 4;
        Assert.Equal(0x80 | 42, bytes[pos]);
        pos += 4;
        Assert.Equal(0x00, bytes[pos]);
        Assert.Equal(new byte[] { 3, 2, 1 }, bytes[(pos + 1)..(pos + 4)]);

        using Image<Rgb24> decoded = Image.Load<Rgb24>(bytes);
        Assert.Equal(new Rgb24(1, 2, 3), decoded[299, 1]);
        Assert.Equal(new Rgb24(9, 8, 7), decoded[0, 0]);
    }

    [Fact]
    public async Task SaveByExtension_And_Async_WriteTga()
    {
        using Image<Rgb24> image = TestImages.Gradient(20, 10);
        string path = Path.Combine(Path.GetTempPath(), $"eis-{Guid.NewGuid():N}.tga");
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
        await image.SaveAsTgaAsync(ms, new TgaEncoder { Compression = TgaCompression.None });
        Assert.Equal(18 + (20 * 10 * 3) + 26, ms.Length);
    }

    // ----- Identify / detection -----

    [Fact]
    public void Identify_ReportsHeaderFacts()
    {
        ImageInfo info = Image.Identify(SmallFormatFixtures.Bytes(Folder, "pil_rgba32_rle_bl"));
        Assert.Equal("TGA", info.Format.Name);
        Assert.Equal((23, 17, 32, 1), (info.Width, info.Height, info.PixelType.BitsPerPixel, info.FrameCount));

        info = Image.Identify(SmallFormatFixtures.Bytes(Folder, "hand_pal8_map16_first4_rle_tr"));
        Assert.Equal(8, info.PixelType.BitsPerPixel);
    }

    [Fact]
    public void Detection_RejectsImplausibleHeaders()
    {
        // All zero: image type 0.
        Assert.False(ImageFormat.Tga.Matches(new byte[64]));

        // Valid uncompressed header but not enough pixel data and no footer.
        byte[] truncated = new byte[18 + 10];
        truncated[2] = 2;
        truncated[12] = 16;
        truncated[14] = 16;
        truncated[16] = 24;
        Assert.False(ImageFormat.Tga.Matches(truncated));
        Assert.Throws<UnknownImageFormatException>(() => Image.Load(truncated));

        // Same header with the footer appended is recognised and then rejected as malformed.
        byte[] withFooter = truncated.Concat(new byte[8]).Concat("TRUEVISION-XFILE.\0"u8.ToArray()).ToArray();
        Assert.True(ImageFormat.Tga.Matches(withFooter));
        Assert.Throws<InvalidImageContentException>(() => Image.Load(withFooter));

        // Reserved descriptor bits, invalid depth, colour-mapped without a map.
        byte[] rle = new byte[64];
        rle[2] = 10;
        rle[12] = 4;
        rle[14] = 4;
        rle[16] = 24;
        Assert.True(ImageFormat.Tga.Matches(rle));
        rle[17] = 0x40;
        Assert.False(ImageFormat.Tga.Matches(rle));
        rle[17] = 0;
        rle[16] = 20;
        Assert.False(ImageFormat.Tga.Matches(rle));
        rle[16] = 8;
        rle[2] = 9;
        Assert.False(ImageFormat.Tga.Matches(rle));

        // Other formats' fixtures never look like TGA.
        Assert.False(ImageFormat.Tga.Matches(FixturePath.Read("bmp/" + FixtureDecodeTests.Manifest.Load("bmp")[0].File)));
        Assert.False(ImageFormat.Tga.Matches(FixturePath.Read("png/" + FixtureDecodeTests.Manifest.Load("png")[0].File)));
    }

    [Fact]
    public void Tga_IsLastInDetectionOrder()
    {
        Assert.Same(ImageFormat.Tga, ImageFormat.All[^1]);
        Assert.Contains("tga", ImageFormat.Tga.FileExtensions);
        Assert.True(ImageFormat.Tga.CanDecode && ImageFormat.Tga.CanEncode);
    }

    [Fact]
    public void SizeLimit_IsEnforcedBeforeAllocation()
    {
        byte[] bytes = SmallFormatFixtures.Bytes(Folder, "pil_rgb24_raw_bl"); // 23x17
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load(bytes, new DecoderOptions { MaxPixels = 100 }));
        ImageInfo info = Image.Identify(bytes, new DecoderOptions { MaxPixels = 100 });
        Assert.Equal(23, info.Width);
    }

    [Fact]
    public void ColorMapIndexOutOfRange_IsMalformed()
    {
        // 2x1 colour-mapped image, 1-entry map, second pixel index 1 -> out of range.
        byte[] tga = new byte[18 + 3 + 2];
        tga[1] = 1;
        tga[2] = 1;
        tga[5] = 1;
        tga[7] = 24;
        tga[12] = 2;
        tga[14] = 1;
        tga[16] = 8;
        tga[18 + 3 + 1] = 1;
        Assert.True(ImageFormat.Tga.Matches(tga));
        Assert.Throws<InvalidImageContentException>(() => Image.Load(tga));
    }

    private static byte Widen5(int v) => (byte)(((v * 255) + 15) / 31);

    private static byte[] Encode<TPixel>(Image<TPixel> image, TgaEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var ms = new MemoryStream();
        image.Save(ms, encoder);
        return ms.ToArray();
    }
}

/// <summary>Manifest-driven fixture verification shared by the TGA, PBM, QOI and ICO tests.</summary>
internal static class SmallFormatFixtures
{
    public static IEnumerable<object[]> Names(string folder)
    {
        if (!FixturePath.Exists($"{folder}/manifest.json"))
        {
            yield return new object[] { "(manifest missing)" };
            yield break;
        }

        foreach (FixtureDecodeTests.FixtureEntry entry in FixtureDecodeTests.Manifest.Load(folder))
        {
            yield return new object[] { entry.Name };
        }
    }

    public static void AssertManifest(string folder, int minimumEntries)
    {
        Assert.True(FixturePath.Exists($"{folder}/manifest.json"), $"Fixtures/{folder}/manifest.json is missing; run Fixtures/generate.py.");
        Assert.True(FixtureDecodeTests.Manifest.Load(folder).Length >= minimumEntries);
    }

    public static FixtureDecodeTests.FixtureEntry Entry(string folder, string name)
        => FixtureDecodeTests.Manifest.Load(folder).SingleOrDefault(e => e.Name == name)
           ?? throw new Xunit.Sdk.XunitException($"Fixture '{folder}/{name}' is not listed in manifest.json; run Fixtures/generate.py.");

    public static byte[] Bytes(string folder, string name) => FixturePath.Read($"{folder}/{Entry(folder, name).File}");

    public static byte[] ExpectedRgba(string folder, string name) => FixturePath.Read($"{folder}/{name}.rgba");

    /// <summary>Reads an extra per-format fact (e.g. "channels") straight from the manifest JSON.</summary>
    public static int Fact(string folder, string name, string key)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(FixturePath.Get($"{folder}/manifest.json")));
        foreach (JsonElement e in doc.RootElement.EnumerateArray())
        {
            if (e.GetProperty("name").GetString() == name)
            {
                return e.GetProperty(key).GetInt32();
            }
        }

        throw new Xunit.Sdk.XunitException($"{folder}/{name}: manifest has no '{key}' fact.");
    }

    /// <summary>Wraps the .rgba dump of a single-frame fixture in an image.</summary>
    public static Image<Rgba32> LoadExpected(string folder, string name)
    {
        FixtureDecodeTests.FixtureEntry entry = Entry(folder, name);
        return Image.LoadPixelData<Rgba32>(ExpectedRgba(folder, name), entry.Width, entry.Height);
    }

    public static string Sha256Prefix(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();

    /// <summary>Same contract as <c>FixtureDecodeTests.VerifyFixture</c>: exact pixels or exactly the expected exception type.</summary>
    public static void Verify(string folder, string name)
    {
        FixtureDecodeTests.FixtureEntry entry = Entry(folder, name);
        byte[] bytes = FixturePath.Read($"{folder}/{entry.File}");

        if (entry.Expect is not null)
        {
            Exception ex = Assert.ThrowsAny<Exception>(() => Image.Load<Rgba32>(bytes));
            Assert.True(ex.GetType().Name == entry.Expect, $"{folder}/{name}: expected {entry.Expect} but got {ex.GetType().Name}: {ex.Message}");
            try
            {
                Image.Identify(bytes);
            }
            catch (Exception identifyEx) when (identifyEx is ImageFormatException or NotSupportedException)
            {
            }

            return;
        }

        ImageInfo info = Image.Identify(bytes);
        Assert.True(info.Width == entry.Width && info.Height == entry.Height,
            $"{folder}/{name}: Identify reported {info.Width}x{info.Height}, manifest says {entry.Width}x{entry.Height}.");
        Assert.True(info.FrameCount == entry.Frames, $"{folder}/{name}: Identify reported {info.FrameCount} frame(s), manifest says {entry.Frames}.");

        byte[] expected = FixturePath.Read($"{folder}/{entry.Name}.rgba");
        using Image<Rgba32> image = Image.Load<Rgba32>(bytes);
        Assert.True(image.Frames.Count == entry.Frames, $"{folder}/{name}: decoded {image.Frames.Count} frame(s), manifest says {entry.Frames}.");

        int offset = 0;
        for (int f = 0; f < entry.Frames; f++)
        {
            (int frameWidth, int frameHeight) = entry.FrameSizes is { Length: > 0 } sizes ? (sizes[f][0], sizes[f][1]) : (entry.Width, entry.Height);
            ImageFrame<Rgba32> frame = image.Frames[f];
            Assert.True(frame.Width == frameWidth && frame.Height == frameHeight,
                $"{folder}/{name} frame {f}: decoded {frame.Width}x{frame.Height}, expected {frameWidth}x{frameHeight}.");

            int byteCount = frameWidth * frameHeight * 4;
            Assert.True(offset + byteCount <= expected.Length, $"{folder}/{name}: .rgba dump is shorter than the manifest implies.");
            ReadOnlySpan<byte> want = expected.AsSpan(offset, byteCount);
            ReadOnlySpan<byte> got = MemoryMarshal.AsBytes(frame.PixelSpan);
            if (!want.SequenceEqual(got))
            {
                int i = want.CommonPrefixLength(got) / 4;
                int x = i % frameWidth;
                int y = i / frameWidth;
                Rgba32 wantPixel = MemoryMarshal.Cast<byte, Rgba32>(want)[i];
                Assert.Fail($"{folder}/{name} frame {f}: first mismatch at pixel #{i} ({x},{y}): expected {wantPixel}, decoded {frame[x, y]}. [{entry.Notes}]");
            }

            offset += byteCount;
        }

        Assert.True(offset == expected.Length, $"{folder}/{name}: .rgba dump has {expected.Length - offset} unexpected trailing bytes.");
    }
}
