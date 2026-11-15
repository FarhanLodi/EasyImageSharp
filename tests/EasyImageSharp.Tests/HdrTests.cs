using System.Numerics;
using System.Text;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Hdr;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Radiance HDR: fixtures under <c>Fixtures/hdr/</c> are assembled byte by byte by <c>gen_hdr.py</c> from the
/// format description, with float32 ground truth computed from the stored RGBE quads; the files OpenCV can read
/// were also checked against <c>cv2.imread</c> (<c>python gen_hdr.py --verify-opencv</c>).
/// </summary>
public class HdrTests
{
    private const string Folder = "hdr";

    public static IEnumerable<object[]> Decodable => HdrExrFixtures.Names(Folder, e => !HdrExrFixtures.IsExpectedFailure(e));

    public static IEnumerable<object[]> Failing => HdrExrFixtures.Names(Folder, HdrExrFixtures.IsExpectedFailure);

    [Fact]
    public void Manifest_CoversEveryEncodingAndOrientation()
    {
        System.Text.Json.JsonElement[] entries = HdrExrFixtures.Manifest(Folder);
        Assert.True(entries.Length >= 25, $"Fixtures/hdr/manifest.json lists {entries.Length} fixtures; run Fixtures/gen_hdr.py.");
        string[] orientations = entries.Select(e => HdrExrFixtures.Text(e, "orientation")).OfType<string>().Distinct().ToArray();
        Assert.Equal(8, orientations.Length);
        foreach (string encoding in new[] { "rle", "flat", "old-rle", "mixed" })
        {
            Assert.Contains(entries, e => HdrExrFixtures.Text(e, "encoding") == encoding);
        }
    }

    [Theory]
    [MemberData(nameof(Decodable))]
    public void Fixture_DecodesToReference(string name)
    {
        System.Text.Json.JsonElement entry = HdrExrFixtures.Entry(Folder, name);
        byte[] bytes = HdrExrFixtures.Bytes(Folder, entry);
        int width = entry.GetProperty("width").GetInt32();
        int height = entry.GetProperty("height").GetInt32();

        ImageInfo info = Image.Identify(bytes);
        Assert.Equal(ImageFormat.Hdr, info.Format);
        Assert.Equal((width, height), (info.Width, info.Height));

        using Image<RgbaVector> image = Image.Load<RgbaVector>(bytes);
        Assert.Equal((width, height), (image.Width, image.Height));
        Assert.Same(ImageFormat.Hdr, image.Metadata.DecodedImageFormat);
        HdrExrFixtures.AssertMatches(name, HdrExrFixtures.Truth(Folder, entry), image.Frames.RootFrame.PixelSpan, width);

        HdrMetadata metadata = image.Metadata.GetFormatMetadata<HdrMetadata>();
        Assert.Equal(HdrExrFixtures.Text(entry, "orientation"), metadata.Orientation);
        Assert.Equal(entry.GetProperty("xyze").GetBoolean() ? HdrColorFormat.Xyze : HdrColorFormat.Rgbe, metadata.ColorFormat);
    }

    [Theory]
    [MemberData(nameof(Failing))]
    public void MalformedFixture_ThrowsTheDocumentedException(string name)
    {
        System.Text.Json.JsonElement entry = HdrExrFixtures.Entry(Folder, name);
        byte[] bytes = HdrExrFixtures.Bytes(Folder, entry);
        string expected = HdrExrFixtures.Text(entry, "expect")!;

        Exception? ex = HdrExrFixtures.Catch(() => Image.Load<RgbaVector>(bytes).Dispose());
        Assert.True(ex?.GetType().Name == expected, $"{name}: expected {expected}, got {ex?.GetType().Name ?? "success"}: {ex?.Message}");

        Exception? identify = HdrExrFixtures.Catch(() => Image.Identify(bytes));
        Assert.True(identify is null or ImageFormatException or NotSupportedException, $"{name}: Identify threw {identify}");
    }

    [Fact]
    public void HeaderVariables_AreReportedCumulatively()
    {
        byte[] bytes = HdrExrFixtures.Bytes(Folder, HdrExrFixtures.Entry(Folder, "header_variants"));
        HdrMetadata metadata = Image.Identify(bytes).Metadata.GetFormatMetadata<HdrMetadata>();
        Assert.Equal("RGBE", metadata.ProgramName);
        Assert.Equal(0.5, metadata.Exposure, 12);
        Assert.Equal(new Vector3(1f, 0.5f, 2f), metadata.ColorCorrection);
        Assert.Equal(1.5, metadata.PixelAspectRatio, 12);
        Assert.Equal("gen_hdr.py", metadata.Software);
        Assert.Equal(new[] { 0.640f, 0.330f, 0.290f, 0.600f, 0.150f, 0.060f, 0.333f, 0.333f }, metadata.Primaries);
    }

    [Fact]
    public void ApplyExposure_DividesByTheCumulativeFactors()
    {
        System.Text.Json.JsonElement entry = HdrExrFixtures.Entry(Folder, "header_variants");
        byte[] bytes = HdrExrFixtures.Bytes(Folder, entry);
        float[] truth = HdrExrFixtures.Truth(Folder, entry);
        using Image<RgbaVector> image = new HdrDecoder { ApplyExposure = true }.Decode<RgbaVector>(bytes);
        Span<RgbaVector> pixels = image.Frames.RootFrame.PixelSpan;
        float[] divisor = { 0.5f * 1f, 0.5f * 0.5f, 0.5f * 2f };
        for (int i = 0; i < pixels.Length; i++)
        {
            AssertClose(truth[(i * 4) + 0] / divisor[0], pixels[i].R);
            AssertClose(truth[(i * 4) + 1] / divisor[1], pixels[i].G);
            AssertClose(truth[(i * 4) + 2] / divisor[2], pixels[i].B);
            Assert.Equal(1f, pixels[i].A);
        }
    }

    [Theory]
    [InlineData("rle_rgbe")]
    [InlineData("orient_pXmY")]
    public void IntegerPixelFormats_ReceiveClampedSrgb(string name)
    {
        System.Text.Json.JsonElement entry = HdrExrFixtures.Entry(Folder, name);
        byte[] bytes = HdrExrFixtures.Bytes(Folder, entry);
        float[] truth = HdrExrFixtures.Truth(Folder, entry);
        using Image<Rgba32> rgba = Image.Load<Rgba32>(bytes);
        using Image<Rgba64> wide = Image.Load<Rgba64>(bytes);
        Span<Rgba32> narrow = rgba.Frames.RootFrame.PixelSpan;
        Span<Rgba64> deep = wide.Frames.RootFrame.PixelSpan;
        for (int i = 0; i < narrow.Length; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                double encoded = SrgbEncode(truth[(i * 4) + c]);
                byte b8 = c == 0 ? narrow[i].R : c == 1 ? narrow[i].G : narrow[i].B;
                ushort b16 = c == 0 ? deep[i].R : c == 1 ? deep[i].G : deep[i].B;
                Assert.True(Math.Abs(b8 - (encoded * 255)) <= 0.5 + 1e-3, $"{name} pixel {i}: 8-bit {b8} vs {encoded * 255:F3}");
                Assert.True(Math.Abs(b16 - (encoded * 65535)) <= 0.5 + 0.1, $"{name} pixel {i}: 16-bit {b16} vs {encoded * 65535:F3}");
            }

            Assert.Equal(255, narrow[i].A);
            Assert.Equal(ushort.MaxValue, deep[i].A);
        }

        // The other integer formats decode without throwing.
        Image.Load<L8>(bytes).Dispose();
        Image.Load<Rgb24>(bytes).Dispose();
        Image.Load<La32>(bytes).Dispose();
    }

    [Fact]
    public void Detection_RequiresARadianceSignature()
    {
        Assert.True(ImageFormat.Hdr.Matches("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n"u8));
        Assert.True(ImageFormat.Hdr.Matches("#?RGBE\n"u8));
        Assert.True(ImageFormat.Hdr.Matches("#?AUTOPANO\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 1\n"u8));
        Assert.False(ImageFormat.Hdr.Matches("#?AUTOPANO\n\n-Y 1 +X 1\n"u8));
        Assert.False(ImageFormat.Hdr.Matches("#? not a picture\n"u8));
        Assert.False(ImageFormat.Hdr.Matches("#!/bin/sh\n"u8));
        Assert.Contains("hdr", ImageFormat.Hdr.FileExtensions);
        Assert.Contains(ImageFormat.Hdr, ImageFormat.All);
    }

    [Fact]
    public void Limits_AreEnforcedBeforeDecoding()
    {
        byte[] bytes = HdrExrFixtures.Bytes(Folder, HdrExrFixtures.Entry(Folder, "rle_long_runs"));
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<RgbaVector>(bytes, new DecoderOptions { MaxPixels = 1000 }));

        // A header promising far more scanlines than the data could hold is rejected without allocating them.
        byte[] huge = Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 60000 +X 60000\n").Concat(new byte[64]).ToArray();
        Assert.Throws<InvalidImageContentException>(() => Image.Load<RgbaVector>(huge, new DecoderOptions { MaxPixels = long.MaxValue }));
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<RgbaVector>(
            Encoding.ASCII.GetBytes("#?RADIANCE\n\n-Y 60000 +X 60000\n").Concat(new byte[300_000]).ToArray()));
        Assert.Equal(60000, Image.Identify(huge).Width);
    }

    // ----- Encoder -----

    [Theory]
    [InlineData(HdrCompression.RunLength, 61, 17)]
    [InlineData(HdrCompression.None, 61, 17)]
    [InlineData(HdrCompression.RunLength, 5, 9)]
    [InlineData(HdrCompression.RunLength, 1, 1)]
    [InlineData(HdrCompression.RunLength, 300, 3)]
    public void Encoder_RoundTripsWithinTheMantissaStep(HdrCompression compression, int width, int height)
    {
        using Image<RgbaVector> source = HdrImage(width, height, seed: width + height);
        byte[] encoded = Encode(source, new HdrEncoder { Compression = compression });
        using Image<RgbaVector> decoded = Image.Load<RgbaVector>(encoded);
        Assert.Equal((width, height), (decoded.Width, decoded.Height));
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                RgbaVector want = source[x, y];
                RgbaVector got = decoded[x, y];
                float max = MathF.Max(want.R, MathF.Max(want.G, want.B));

                // The shared exponent is chosen for the largest component; every component is rounded to a step
                // of max / 128 at worst, so the error is at most half of that.
                float tolerance = (max / 256f) + 1e-30f;
                Assert.True(MathF.Abs(want.R - got.R) <= tolerance && MathF.Abs(want.G - got.G) <= tolerance
                    && MathF.Abs(want.B - got.B) <= tolerance, $"({x}, {y}): {got} vs {want}");
                Assert.Equal(1f, got.A);
            }
        }
    }

    [Fact]
    public void Encoder_StoresPowersOfTwoAndZeroExactly()
    {
        using var image = new Image<RgbaVector>(9, 1);
        float[] values = { 0f, 1f, 0.5f, 2f, 1024f, 1f / 4096f, 3f, 0.75f, 65536f };
        for (int x = 0; x < values.Length; x++)
        {
            image[x, 0] = new RgbaVector(values[x], values[x] / 2, values[x] / 4);
        }

        using Image<RgbaVector> decoded = Image.Load<RgbaVector>(Encode(image, new HdrEncoder()));
        for (int x = 0; x < values.Length; x++)
        {
            Assert.Equal(new RgbaVector(values[x], values[x] / 2, values[x] / 4), decoded[x, 0]);
        }
    }

    [Fact]
    public void Encoder_FlatAndRunLengthOutputDecodeIdentically()
    {
        using Image<RgbaVector> image = HdrImage(64, 9, seed: 21);
        using Image<RgbaVector> flat = Image.Load<RgbaVector>(Encode(image, new HdrEncoder { Compression = HdrCompression.None }));
        using Image<RgbaVector> rle = Image.Load<RgbaVector>(Encode(image, new HdrEncoder { Compression = HdrCompression.RunLength }));
        Assert.True(flat.Frames.RootFrame.PixelSpan.SequenceEqual(rle.Frames.RootFrame.PixelSpan));

        // Normalised mantissas (the largest one is at least 128) never form a (1, 1, 1, e) or (2, 2, b, e) marker.
        byte[] encoded = Encode(image, new HdrEncoder { Compression = HdrCompression.None });
        int data = encoded.AsSpan().IndexOf("+X 64\n"u8) + 6;
        for (int i = data; i < encoded.Length; i += 4)
        {
            Assert.False(encoded[i] == 1 && encoded[i + 1] == 1 && encoded[i + 2] == 1);
            Assert.False(encoded[i] == 2 && encoded[i + 1] == 2);
        }
    }

    [Fact]
    public void Encoder_RoundTripsEightBitGrayExactly()
    {
        using var image = new Image<Rgba32>(256, 2);
        for (int x = 0; x < 256; x++)
        {
            image[x, 0] = new Rgba32((byte)x, (byte)x, (byte)x, 255);
            image[x, 1] = new Rgba32((byte)(255 - x), (byte)(255 - x), (byte)(255 - x), 255);
        }

        using Image<Rgba32> decoded = Image.Load<Rgba32>(Encode(image, new HdrEncoder()));
        for (int x = 0; x < 256; x++)
        {
            Assert.Equal(image[x, 0], decoded[x, 0]);
            Assert.Equal(image[x, 1], decoded[x, 1]);
        }
    }

    [Fact]
    public void SaveHelpers_WriteRadianceFiles()
    {
        using Image<RgbaVector> image = HdrImage(20, 4, seed: 3);
        using var stream = new MemoryStream();
        image.SaveAsHdr(stream);
        Assert.StartsWith("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 4 +X 20\n", Encoding.ASCII.GetString(stream.ToArray()));

        string path = Path.Combine(Path.GetTempPath(), $"easyimagesharp-hdr-{Guid.NewGuid():N}.hdr");
        try
        {
            image.Save(path);
            Assert.Equal(ImageFormat.Hdr, Image.Identify(File.ReadAllBytes(path)).Format);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_HonoursTheEncoderSettings()
    {
        using Image<RgbaVector> image = HdrImage(20, 4, seed: 4);
        using var stream = new MemoryStream();
        await image.SaveAsHdrAsync(stream, new HdrEncoder { Compression = HdrCompression.None });
        byte[] bytes = stream.ToArray();
        int header = Encoding.ASCII.GetByteCount("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 4 +X 20\n");
        Assert.Equal(header + (20 * 4 * 4), bytes.Length);
    }

    private static void AssertClose(double expected, double actual)
        => Assert.True(Math.Abs(expected - actual) <= 1e-6 * Math.Max(1, Math.Abs(expected)), $"{actual:R} vs {expected:R}");

    internal static Image<RgbaVector> HdrImage(int width, int height, int seed)
    {
        var random = new Random(seed);
        var image = new Image<RgbaVector>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float scale = MathF.ScaleB(1f, random.Next(-12, 13));
                image[x, y] = (x + y) % 11 == 0
                    ? new RgbaVector(0f, 0f, 0f)
                    : new RgbaVector((float)random.NextDouble() * scale, (float)random.NextDouble() * scale, (float)random.NextDouble() * scale);
            }
        }

        return image;
    }

    internal static byte[] Encode<TPixel>(Image<TPixel> image, IImageEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    internal static double SrgbEncode(float linear)
    {
        double v = linear > 0 ? Math.Min(linear, 1.0) : 0.0;
        return v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055;
    }
}
