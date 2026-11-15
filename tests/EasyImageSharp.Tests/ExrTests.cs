using System.Numerics;
using System.Text.Json;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Exr;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// OpenEXR: every fixture under <c>Fixtures/exr/</c> was written by the OpenEXR reference library - either copied
/// from the official openexr-images repository or written through its Python bindings by
/// <c>exr/build_exr_fixtures.py</c> - and its ground truth is the reference library's own decode.
/// </summary>
public class ExrTests
{
    private const string Folder = "exr";

    public static IEnumerable<object[]> Decodable => HdrExrFixtures.Names(Folder, e => !HdrExrFixtures.IsExpectedFailure(e));

    public static IEnumerable<object[]> Unsupported => HdrExrFixtures.Names(Folder, HdrExrFixtures.IsExpectedFailure);

    [Fact]
    public void Manifest_CoversEveryImplementedCompressionAndLayout()
    {
        JsonElement[] decodable = HdrExrFixtures.Manifest(Folder).Where(e => !HdrExrFixtures.IsExpectedFailure(e)).ToArray();
        foreach (string compression in new[] { "NO_COMPRESSION", "RLE_COMPRESSION", "ZIPS_COMPRESSION", "ZIP_COMPRESSION", "PIZ_COMPRESSION", "PXR24_COMPRESSION" })
        {
            Assert.Contains(decodable, e => HdrExrFixtures.Text(e, "compression") == compression && e.GetProperty("tiled").GetBoolean());
            Assert.Contains(decodable, e => HdrExrFixtures.Text(e, "compression") == compression && !e.GetProperty("tiled").GetBoolean());
        }

        Assert.Contains(decodable, e => HdrExrFixtures.Text(e, "line_order") == "DECREASING_Y");
        Assert.Contains(decodable, e => e.GetProperty("raw_chunks").GetInt32() > 0 && e.GetProperty("raw_chunks").GetInt32() < e.GetProperty("chunks").GetInt32());
    }

    [Theory]
    [MemberData(nameof(Decodable))]
    public void Fixture_DecodesToReference(string name)
    {
        JsonElement entry = HdrExrFixtures.Entry(Folder, name);
        byte[] bytes = HdrExrFixtures.Bytes(Folder, entry);
        int width = entry.GetProperty("width").GetInt32();
        int height = entry.GetProperty("height").GetInt32();

        ImageInfo info = Image.Identify(bytes);
        Assert.Equal(ImageFormat.Exr, info.Format);
        Assert.Equal((width, height), (info.Width, info.Height));
        ExrMetadata metadata = info.Metadata.GetFormatMetadata<ExrMetadata>();
        Assert.Equal(Window(entry, "data_window"), metadata.DataWindow);
        Assert.Equal(Window(entry, "display_window"), metadata.DisplayWindow);
        Assert.Equal(entry.GetProperty("tiled").GetBoolean(), metadata.IsTiled);
        Assert.Equal(HdrExrFixtures.Text(entry, "compression"), CompressionName(metadata.Compression));

        using Image<RgbaVector> image = Image.Load<RgbaVector>(bytes);
        Assert.Equal((width, height), (image.Width, image.Height));
        ReadOnlySpan<RgbaVector> pixels = image.Frames.RootFrame.PixelSpan;
        if (entry.TryGetProperty("f32", out _))
        {
            HdrExrFixtures.AssertMatches(name, HdrExrFixtures.Truth(Folder, entry), pixels, width);
        }

        Assert.True(HdrExrFixtures.CanonicalSha256(pixels) == entry.GetProperty("sha256_f32").GetString(),
            $"{name}: decoded pixels differ from the reference decode. {DescribeSamples(entry, image)}");
    }

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void UnsupportedFeature_ThrowsNotSupportedNamingIt(string name)
    {
        JsonElement entry = HdrExrFixtures.Entry(Folder, name);
        byte[] bytes = HdrExrFixtures.Bytes(Folder, entry);
        Exception? ex = HdrExrFixtures.Catch(() => Image.Load<RgbaVector>(bytes).Dispose());
        Assert.True(ex is NotSupportedException, $"{name}: expected NotSupportedException, got {ex?.GetType().Name ?? "success"}: {ex?.Message}");

        string compression = HdrExrFixtures.Text(entry, "compression")!.Replace("_COMPRESSION", string.Empty, StringComparison.Ordinal);
        if (name.Contains("multipart", StringComparison.Ordinal))
        {
            Assert.Contains("Multi-part", ex!.Message, StringComparison.Ordinal);
            Assert.Throws<NotSupportedException>(() => Image.Identify(bytes));
        }
        else if (name.Contains("depth", StringComparison.Ordinal))
        {
            Assert.Contains("Z", ex!.Message, StringComparison.Ordinal);
            Assert.Equal(ImageFormat.Exr, Image.Identify(bytes).Format);
        }
        else
        {
            Assert.Contains(compression, ex!.Message, StringComparison.Ordinal);

            // The header is still readable.
            ImageInfo info = Image.Identify(bytes);
            Assert.Equal(23, info.Width);
        }
    }

    [Fact]
    public void DisplayWindow_PlacesTheDataWindowInsideIt()
    {
        byte[] bytes = HdrExrFixtures.Bytes(Folder, HdrExrFixtures.Entry(Folder, "oei_display_window_t08"));
        using Image<RgbaVector> data = Image.Load<RgbaVector>(bytes);
        using Image<RgbaVector> framed = new ExrDecoder { UseDisplayWindow = true }.Decode<RgbaVector>(bytes);
        Assert.Equal((400, 300), (data.Width, data.Height));
        Assert.Equal((501, 401), (framed.Width, framed.Height));
        ImageInfo info = new ExrDecoder { UseDisplayWindow = true }.Identify(bytes);
        Assert.Equal((501, 401), (info.Width, info.Height));
        for (int y = 0; y < framed.Height; y++)
        {
            for (int x = 0; x < framed.Width; x++)
            {
                bool inside = x >= 30 && x <= 429 && y >= 40 && y <= 339;
                RgbaVector expected = inside ? data[x - 30, y - 40] : default;
                Assert.Equal(expected, framed[x, y]);
            }
        }
    }

    [Fact]
    public void DisplayWindow_DisjointFromTheDataWindowIsTransparent()
    {
        byte[] bytes = HdrExrFixtures.Bytes(Folder, HdrExrFixtures.Entry(Folder, "oei_display_window_t09"));
        using Image<Rgba32> framed = new ExrDecoder { UseDisplayWindow = true }.Decode<Rgba32>(bytes);
        Assert.Equal((200, 300), (framed.Width, framed.Height));
        Assert.All(framed.Frames.RootFrame.PixelSpan.ToArray(), p => Assert.Equal(default, p));
    }

    [Fact]
    public void DisplayWindow_HandlesNegativeDataWindowOrigins()
    {
        JsonElement entry = HdrExrFixtures.Entry(Folder, "ref_scan_none_data_window");
        byte[] bytes = HdrExrFixtures.Bytes(Folder, entry);
        float[] truth = HdrExrFixtures.Truth(Folder, entry);
        using Image<RgbaVector> framed = new ExrDecoder { UseDisplayWindow = true }.Decode<RgbaVector>(bytes);
        Assert.Equal((64, 48), (framed.Width, framed.Height));

        // Data window (-5, 3)-(17, 39) of 23x37 pixels inside the display window (0, 0)-(63, 47).
        for (int y = 0; y < 48; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                int dx = x + 5, dy = y - 3;
                RgbaVector expected = dx is >= 0 and < 23 && dy is >= 0 and < 37
                    ? new RgbaVector(truth[((dy * 23) + dx) * 4], truth[(((dy * 23) + dx) * 4) + 1], truth[(((dy * 23) + dx) * 4) + 2], 1f)
                    : default;
                Assert.Equal(expected, framed[x, y]);
            }
        }
    }

    [Theory]
    [InlineData("ref_scan_zip_rgba_half")]
    [InlineData("oei_gray_ramps_horizontal")]
    public void IntegerPixelFormats_ReceiveClampedSrgb(string name)
    {
        JsonElement entry = HdrExrFixtures.Entry(Folder, name);
        byte[] bytes = HdrExrFixtures.Bytes(Folder, entry);
        using Image<RgbaVector> linear = Image.Load<RgbaVector>(bytes);
        using Image<Rgba32> narrow = Image.Load<Rgba32>(bytes);
        Span<RgbaVector> source = linear.Frames.RootFrame.PixelSpan;
        Span<Rgba32> target = narrow.Frames.RootFrame.PixelSpan;
        for (int i = 0; i < source.Length; i++)
        {
            Assert.True(Math.Abs(target[i].R - (HdrTests.SrgbEncode(source[i].R) * 255)) <= 0.501, $"{name} pixel {i} R");
            Assert.True(Math.Abs(target[i].G - (HdrTests.SrgbEncode(source[i].G) * 255)) <= 0.501, $"{name} pixel {i} G");
            Assert.True(Math.Abs(target[i].B - (HdrTests.SrgbEncode(source[i].B) * 255)) <= 0.501, $"{name} pixel {i} B");
            double alpha = source[i].A > 0 ? Math.Min(source[i].A, 1f) : 0;
            Assert.True(Math.Abs(target[i].A - (alpha * 255)) <= 0.501, $"{name} pixel {i} A");
        }

        Image.Load<L16>(bytes).Dispose();
        Image.Load<Rgb48>(bytes).Dispose();
    }

    [Theory]
    [InlineData("oei_gamma_chart")]
    [InlineData("oei_color_coded_levels")]
    [InlineData("oei_display_window_t08")]
    public void ParallelDecode_MatchesSingleThreadedAndKeepsTheExceptionContract(string name)
    {
        byte[] bytes = HdrExrFixtures.Bytes(Folder, HdrExrFixtures.Entry(Folder, name));
        var single = new DecoderOptions { ExecutionOptions = new ImageExecutionOptions { Configuration = new Configuration { MaxDegreeOfParallelism = 1 } } };
        var multi = new DecoderOptions
        {
            ExecutionOptions = new ImageExecutionOptions { Configuration = new Configuration { MaxDegreeOfParallelism = 8, MinimumPixelsPerTask = 1024 } },
        };
        using Image<Rgba64> a = Image.Load<Rgba64>(bytes, single);
        using Image<Rgba64> b = Image.Load<Rgba64>(bytes, multi);
        Assert.True(a.Frames.RootFrame.PixelSpan.SequenceEqual(b.Frames.RootFrame.PixelSpan));

        // A chunk that fails in a worker surfaces as the documented exception, not as an AggregateException.
        byte[] damaged = (byte[])bytes.Clone();
        damaged.AsSpan(damaged.Length / 3, 64).Fill(0xFF);
        Exception? ex = HdrExrFixtures.Catch(() => Image.Load<RgbaVector>(damaged, multi).Dispose());
        Assert.True(ex is InvalidImageContentException, $"{name}: {ex}");
    }

    [Fact]
    public void Metadata_ReportsChannelsAndStringAttributes()
    {
        byte[] bytes = HdrExrFixtures.Bytes(Folder, HdrExrFixtures.Entry(Folder, "ref_scan_zip_mixed_types"));
        ExrMetadata metadata = Image.Identify(bytes).Metadata.GetFormatMetadata<ExrMetadata>();
        Assert.Equal(new[] { "B", "G", "R", "Z", "diffuse.R" }, metadata.Channels.Select(c => c.Name));
        Assert.Equal(new[] { ExrPixelType.UInt, ExrPixelType.Float, ExrPixelType.Half, ExrPixelType.Float, ExrPixelType.Half },
            metadata.Channels.Select(c => c.PixelType));
        Assert.Equal(ExrCompression.Zip, metadata.Compression);
        Assert.Equal(ExrLineOrder.IncreasingY, metadata.LineOrder);

        ExrMetadata tiled = Image.Identify(HdrExrFixtures.Bytes(Folder, HdrExrFixtures.Entry(Folder, "oei_color_coded_levels")))
            .Metadata.GetFormatMetadata<ExrMetadata>();
        Assert.True(tiled.IsTiled);
        Assert.Equal(new Size(64, 64), tiled.TileSize);
        Assert.Equal(ExrLevelMode.MipmapLevels, tiled.LevelMode);
    }

    // ----- Encoder -----

    public static IEnumerable<object[]> EncoderSettings()
    {
        foreach (ExrCompression compression in new[] { ExrCompression.None, ExrCompression.Rle, ExrCompression.Zips, ExrCompression.Zip, ExrCompression.Piz })
        {
            foreach (ExrPixelType type in new[] { ExrPixelType.Half, ExrPixelType.Float })
            {
                yield return new object[] { compression, type };
            }
        }
    }

    [Theory]
    [MemberData(nameof(EncoderSettings))]
    public void Encoder_RoundTripsLosslessly(ExrCompression compression, ExrPixelType pixelType)
    {
        foreach ((int width, int height) in new[] { (1, 1), (37, 45), (130, 3) })
        {
            using Image<RgbaVector> source = SpecialValueImage(width, height, seed: width * 31 + height);
            byte[] encoded = HdrTests.Encode(source, new ExrEncoder { Compression = compression, PixelType = pixelType });
            ExrMetadata metadata = Image.Identify(encoded).Metadata.GetFormatMetadata<ExrMetadata>();
            Assert.Equal(compression, metadata.Compression);
            Assert.All(metadata.Channels, c => Assert.Equal(pixelType, c.PixelType));
            Assert.Equal(new[] { "A", "B", "G", "R" }, metadata.Channels.Select(c => c.Name));

            using Image<RgbaVector> decoded = Image.Load<RgbaVector>(encoded);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Vector4 want = source[x, y].ToScaledVector4();
                    if (pixelType == ExrPixelType.Half)
                    {
                        want = new Vector4((float)(Half)want.X, (float)(Half)want.Y, (float)(Half)want.Z, (float)(Half)want.W);
                    }

                    Vector4 got = decoded[x, y].ToScaledVector4();
                    Assert.True(SameBits(want, got), $"{compression}/{pixelType} {width}x{height} ({x}, {y}): {got} vs {want}");
                }
            }
        }
    }

    [Theory]
    [InlineData(ExrCompression.Rle)]
    [InlineData(ExrCompression.Zips)]
    [InlineData(ExrCompression.Zip)]
    [InlineData(ExrCompression.Piz)]
    public void Encoder_CompressesSmoothImagesAndStaysLossless(ExrCompression compression)
    {
        // Random test data is stored raw (compression cannot win on it), which would leave the codec untested.
        using var image = new Image<RgbaVector>(123, 77);
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                float v = (x + 1) / 64f * ((y + 3) / 32f);
                image[x, y] = new RgbaVector(v, v * 0.75f, MathF.Sqrt(v), (x % 17) / 16f);
            }
        }

        byte[] encoded = HdrTests.Encode(image, new ExrEncoder { Compression = compression, PixelType = ExrPixelType.Half });
        Assert.True(encoded.Length < 123 * 77 * 8, $"{compression}: {encoded.Length} bytes is not compressed");
        using Image<RgbaVector> decoded = Image.Load<RgbaVector>(encoded);
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Vector4 v = image[x, y].ToScaledVector4();
                Assert.Equal(new RgbaVector((float)(Half)v.X, (float)(Half)v.Y, (float)(Half)v.Z, (float)(Half)v.W), decoded[x, y]);
            }
        }
    }

    [Fact]
    public void Encoder_PizUsesTheSixteenBitWaveletForWideValueSets()
    {
        // 17000 consecutive half bit patterns give the first 32-line chunk more than 2^14 distinct words, which
        // selects the 16-bit wavelet, while staying smooth enough that the chunk is genuinely compressed.
        using var image = new Image<RgbaVector>(1024, 40, new RgbaVector(0f, 0f, 0f, 1f));
        for (int i = 0; i < 17000; i++)
        {
            float v = (float)BitConverter.UInt16BitsToHalf((ushort)(i + 1));
            image[i % 1024, i / 1024] = new RgbaVector(v, v, v, 1f);
        }

        byte[] encoded = HdrTests.Encode(image, new ExrEncoder { Compression = ExrCompression.Piz, Channels = ExrChannelLayout.Luminance });
        Assert.True(encoded.Length < 1024 * 40, $"{encoded.Length} bytes is not compressed");
        using Image<RgbaVector> decoded = Image.Load<RgbaVector>(encoded);
        Assert.True(image.Frames.RootFrame.PixelSpan.SequenceEqual(decoded.Frames.RootFrame.PixelSpan));
    }

    [Fact]
    public void Encoder_EightBitImagesRoundTripThroughHalf()
    {
        using var image = new Image<Rgba32>(256, 3);
        for (int x = 0; x < 256; x++)
        {
            image[x, 0] = new Rgba32((byte)x, (byte)(255 - x), (byte)(x * 7), (byte)(x ^ 0x5A));
            image[x, 1] = new Rgba32((byte)x, (byte)x, (byte)x, 255);
            image[x, 2] = new Rgba32((byte)(x * 3), (byte)(x * 5), (byte)(x * 11), (byte)x);
        }

        foreach (ExrCompression compression in new[] { ExrCompression.Zip, ExrCompression.Piz })
        {
            using Image<Rgba32> decoded = Image.Load<Rgba32>(HdrTests.Encode(image, new ExrEncoder { Compression = compression }));
            Assert.True(image.Frames.RootFrame.PixelSpan.SequenceEqual(decoded.Frames.RootFrame.PixelSpan), compression.ToString());
        }

        using var gray = new Image<L8>(256, 1);
        for (int x = 0; x < 256; x++)
        {
            gray[x, 0] = new L8((byte)x);
        }

        byte[] encodedGray = HdrTests.Encode(gray, new ExrEncoder());
        Assert.Equal(new[] { "Y" }, Image.Identify(encodedGray).Metadata.GetFormatMetadata<ExrMetadata>().Channels.Select(c => c.Name));
        using Image<L8> decodedGray = Image.Load<L8>(encodedGray);
        Assert.True(gray.Frames.RootFrame.PixelSpan.SequenceEqual(decodedGray.Frames.RootFrame.PixelSpan));

        using var grayAlpha = new Image<La16>(4, 1);
        Assert.Equal(new[] { "A", "Y" }, Image.Identify(HdrTests.Encode(grayAlpha, new ExrEncoder())).Metadata.GetFormatMetadata<ExrMetadata>().Channels.Select(c => c.Name));
        using var rgb = new Image<Rgb24>(4, 1);
        Assert.Equal(new[] { "B", "G", "R" }, Image.Identify(HdrTests.Encode(rgb, new ExrEncoder())).Metadata.GetFormatMetadata<ExrMetadata>().Channels.Select(c => c.Name));
    }

    [Fact]
    public void Encoder_LuminanceLayoutWritesRec709Luminance()
    {
        using var image = new Image<RgbaVector>(1, 1);
        image[0, 0] = new RgbaVector(1f, 2f, 4f, 1f);
        byte[] encoded = HdrTests.Encode(image, new ExrEncoder { Channels = ExrChannelLayout.Luminance, PixelType = ExrPixelType.Float });
        using Image<RgbaVector> decoded = Image.Load<RgbaVector>(encoded);
        float y = (0.2126f * 1f) + (0.7152f * 2f) + (0.0722f * 4f);
        Assert.Equal(new RgbaVector(y, y, y, 1f), decoded[0, 0]);
    }

    [Fact]
    public void Encoder_WritesMetadataBack()
    {
        byte[] source = HdrExrFixtures.Bytes(Folder, HdrExrFixtures.Entry(Folder, "oei_stripes"));
        using Image<RgbaVector> image = Image.Load<RgbaVector>(source);
        ExrMetadata original = image.Metadata.GetFormatMetadata<ExrMetadata>();
        original.StringAttributes["comments"] = "written back";
        original.StringAttributes["a.rather.long.attribute.name.beyond.thirty.one"] = "long names flag";
        original.Chromaticities = new ExrChromaticities(new Vector2(0.7f, 0.3f), new Vector2(0.2f, 0.7f), new Vector2(0.1f, 0.05f), new Vector2(0.3127f, 0.329f));
        original.PixelAspectRatio = 2f;

        byte[] encoded = HdrTests.Encode(image, new ExrEncoder { Compression = ExrCompression.Piz });
        ExrMetadata copy = Image.Identify(encoded).Metadata.GetFormatMetadata<ExrMetadata>();
        Assert.Equal("written back", copy.StringAttributes["comments"]);
        Assert.Equal("long names flag", copy.StringAttributes["a.rather.long.attribute.name.beyond.thirty.one"]);
        Assert.Equal(original.Chromaticities.Red, copy.Chromaticities!.Red);
        Assert.Equal(original.Chromaticities.White, copy.Chromaticities.White);
        Assert.Equal(2f, copy.PixelAspectRatio);
        using Image<RgbaVector> decoded = Image.Load<RgbaVector>(encoded);
        Assert.True(image.Frames.RootFrame.PixelSpan.SequenceEqual(decoded.Frames.RootFrame.PixelSpan));
    }

    [Fact]
    public void Encoder_RejectsSettingsItCannotWrite()
    {
        using var image = new Image<RgbaVector>(2, 2);
        Assert.Throws<NotSupportedException>(() => HdrTests.Encode(image, new ExrEncoder { Compression = ExrCompression.Pxr24 }));
        Assert.Throws<NotSupportedException>(() => HdrTests.Encode(image, new ExrEncoder { Compression = ExrCompression.Dwaa }));
        Assert.Throws<ArgumentOutOfRangeException>(() => HdrTests.Encode(image, new ExrEncoder { PixelType = ExrPixelType.UInt }));
    }

    [Fact]
    public async Task SaveHelpers_WriteOpenExrFiles()
    {
        using Image<RgbaVector> image = SpecialValueImage(9, 7, seed: 5);
        using var stream = new MemoryStream();
        image.SaveAsExr(stream);
        Assert.Equal(ImageFormat.Exr, Image.Identify(stream.ToArray()).Format);

        using var asyncStream = new MemoryStream();
        await image.SaveAsExrAsync(asyncStream, new ExrEncoder { Compression = ExrCompression.Rle });
        Assert.Equal(ExrCompression.Rle, Image.Identify(asyncStream.ToArray()).Metadata.GetFormatMetadata<ExrMetadata>().Compression);

        string path = Path.Combine(Path.GetTempPath(), $"easyimagesharp-exr-{Guid.NewGuid():N}.exr");
        try
        {
            image.Save(path);
            Assert.Equal(ImageFormat.Exr, Image.Identify(File.ReadAllBytes(path)).Format);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Detection_UsesTheMagicNumber()
    {
        Assert.True(ImageFormat.Exr.Matches(new byte[] { 0x76, 0x2F, 0x31, 0x01, 2, 0, 0, 0 }));
        Assert.False(ImageFormat.Exr.Matches(new byte[] { 0x76, 0x2F, 0x31 }));
        Assert.Contains(ImageFormat.Exr, ImageFormat.All);
        Assert.Equal("image/x-exr", ImageFormat.Exr.DefaultMimeType);
    }

    internal static Image<RgbaVector> SpecialValueImage(int width, int height, int seed)
    {
        var random = new Random(seed);
        float[] specials = { 0f, -0f, 1f, -1f, 65504f, 1e-7f, float.PositiveInfinity, float.NegativeInfinity, float.NaN, 0.5f };
        var image = new Image<RgbaVector>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float Next() => random.Next(8) == 0
                    ? specials[random.Next(specials.Length)]
                    : (float)(random.NextDouble() * Math.Pow(2, random.Next(-10, 11)) * (random.Next(10) == 0 ? -1 : 1));

                // Smooth areas give the run-length and wavelet coders something to compress.
                image[x, y] = (x / 4 + y / 3) % 3 == 0 ? new RgbaVector(0.25f, 0.5f, 1f, 1f) : new RgbaVector(Next(), Next(), Next(), Next());
            }
        }

        return image;
    }

    private static bool SameBits(Vector4 a, Vector4 b)
        => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z) && Same(a.W, b.W);

    private static bool Same(float a, float b)
        => (float.IsNaN(a) && float.IsNaN(b)) || BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    private static Rectangle Window(JsonElement entry, string property)
    {
        int[] w = entry.GetProperty(property).EnumerateArray().Select(v => v.GetInt32()).ToArray();
        return new Rectangle(w[0], w[1], w[2] - w[0] + 1, w[3] - w[1] + 1);
    }

    private static string CompressionName(ExrCompression compression) => compression switch
    {
        ExrCompression.None => "NO_COMPRESSION",
        _ => compression.ToString().ToUpperInvariant() + "_COMPRESSION",
    };

    private static string DescribeSamples(JsonElement entry, Image<RgbaVector> image)
    {
        var parts = new List<string>();
        foreach (JsonElement sample in entry.GetProperty("samples").EnumerateArray())
        {
            JsonElement[] s = sample.EnumerateArray().ToArray();
            int x = s[0].GetInt32(), y = s[1].GetInt32();
            parts.Add($"({x},{y}) expected [{string.Join(", ", s.Skip(2).Select(v => v.ToString()))}] got {image[x, y]}");
        }

        return string.Join("; ", parts);
    }
}
