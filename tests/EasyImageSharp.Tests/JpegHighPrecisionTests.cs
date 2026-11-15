using System.IO.Compression;
using System.Text.Json;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests;

/// <summary>
/// 12-bit DCT frames (<c>Fixtures/jpeg12/</c>) and lossless frames (<c>Fixtures/jpeglossless/</c>). Every fixture
/// was written by libjpeg-turbo or by a T.81 Annex H encoder in the folder's generator and is paired with
/// libjpeg-turbo's decode of it (see the folders' PROVENANCE.md). Lossless frames must decode exactly; 12-bit DCT
/// frames within the tolerance a floating-point IDCT leaves against libjpeg-turbo's integer one.
/// </summary>
public class JpegHighPrecisionTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> LosslessFixtures() => Manifest("jpeglossless").Select(e => new object[] { e.Name });

    public static IEnumerable<object[]> TwelveBitFixtures() => Manifest("jpeg12").Select(e => new object[] { e.Name });

    // ----- Lossless -----

    [Theory]
    [MemberData(nameof(LosslessFixtures))]
    public void Lossless_DecodesExactlyAtFullPrecision(string name)
    {
        Fixture fixture = Find("jpeglossless", name);
        byte[] data = FixturePath.Read($"jpeglossless/{fixture.Name}.jpg");
        ushort[] truth = fixture.ReadTruth("jpeglossless");
        int max = (1 << fixture.Precision) - 1;
        int tolerance = fixture.ColorType == "YCbCr" ? 1 : 0; // The colour transform rounds differently by design.

        int[] expected = ExpectedRgb(fixture, truth, max);
        using Image<Rgba64> wide = Image.Load<Rgba64>(data);
        Assert.Equal((fixture.Width, fixture.Height), (wide.Width, wide.Height));
        int worst16 = 0;
        int worst8 = 0;
        using Image<Rgb24> narrow = Image.Load<Rgb24>(data);
        for (int y = 0; y < fixture.Height; y++)
        {
            for (int x = 0; x < fixture.Width; x++)
            {
                int i = ((y * fixture.Width) + x) * 3;
                Rgba64 p = wide[x, y];
                Rgb24 q = narrow[x, y];
                Assert.Equal(ushort.MaxValue, p.A);
                worst16 = Math.Max(worst16, MaxDelta(p.R, p.G, p.B, To16(expected[i], max), To16(expected[i + 1], max), To16(expected[i + 2], max)));
                worst8 = Math.Max(worst8, MaxDelta(q.R, q.G, q.B, To8(expected[i], max), To8(expected[i + 1], max), To8(expected[i + 2], max)));
            }
        }

        output.WriteLine($"{name}: worst 16-bit delta {worst16}, worst 8-bit delta {worst8}");
        Assert.True(worst8 <= tolerance, $"{name}: an 8-bit channel is off by {worst8}");
        Assert.True(worst16 <= tolerance * ((65535 + (max / 2)) / max), $"{name}: a 16-bit channel is off by {worst16}");
    }

    [Theory]
    [MemberData(nameof(LosslessFixtures))]
    public void Lossless_GrayscaleKeepsEveryBitInL16(string name)
    {
        Fixture fixture = Find("jpeglossless", name);
        if (fixture.Channels != 1)
        {
            return;
        }

        ushort[] truth = fixture.ReadTruth("jpeglossless");
        int max = (1 << fixture.Precision) - 1;
        using Image<L16> image = Image.Load<L16>(FixturePath.Read($"jpeglossless/{fixture.Name}.jpg"));
        for (int y = 0; y < fixture.Height; y++)
        {
            for (int x = 0; x < fixture.Width; x++)
            {
                Assert.Equal(To16(truth[(y * fixture.Width) + x], max), image[x, y].PackedValue);
            }
        }
    }

    [Theory]
    [InlineData("gray16_p4")]
    [InlineData("rgb12_p3")]
    [InlineData("cmyk12_p4")]
    public void Lossless_IdentifyReportsPrecisionAndProcess(string name)
    {
        Fixture fixture = Find("jpeglossless", name);
        byte[] data = FixturePath.Read($"jpeglossless/{fixture.Name}.jpg");
        ImageInfo info = Image.Identify(data);
        int components = fixture.ColorType == "Cmyk" ? 4 : fixture.Channels;
        Assert.Equal(components * fixture.Precision, info.PixelType.BitsPerPixel);
        JpegMetadata jpeg = info.Metadata.GetJpegMetadata();
        Assert.True(jpeg.Lossless);
        Assert.False(jpeg.Progressive);
        Assert.Equal(fixture.Precision, jpeg.BitsPerSample);

        using Image<Rgb48> image = Image.Load<Rgb48>(data);
        Assert.Equal(fixture.Precision, image.Metadata.GetJpegMetadata().BitsPerSample);
        Assert.True(image.Metadata.GetJpegMetadata().Lossless);
        Assert.Equal(fixture.Precision, image.Metadata.GetJpegMetadata().DeepClone().BitsPerSample);
    }

    [Fact]
    public void Lossless_ReducedDecodeIsNotSupported()
    {
        byte[] data = FixturePath.Read("jpeglossless/gray8_p1.jpg");
        Assert.Throws<NotSupportedException>(() => new JpegDecoder().DecodeReduced<L8>(data, 2));
        using Image<L8> full = new JpegDecoder().DecodeReduced<L8>(data, 1);
        Assert.Equal(33, full.Width);
    }

    [Theory]
    [InlineData(0)]   // Ss = 0 is reserved for hierarchical differential frames.
    [InlineData(8)]   // Beyond the seven predictors.
    public void Lossless_InvalidPredictor_ThrowsInvalidContent(byte predictor)
    {
        byte[] data = FixturePath.Read("jpeglossless/gray12_p6.jpg");
        int sos = IndexOfMarker(data, 0xDA);
        int ns = data[sos + 4];
        data[sos + 5 + (2 * ns)] = predictor;
        Assert.Throws<InvalidImageContentException>(() => Image.Load<L16>(data));
    }

    [Fact]
    public void Lossless_PointTransformAtPrecision_ThrowsInvalidContent()
    {
        byte[] data = FixturePath.Read("jpeglossless/gray8_p1.jpg");
        int sos = IndexOfMarker(data, 0xDA);
        int ns = data[sos + 4];
        data[sos + 5 + (2 * ns) + 2] = 8; // Al = 8 leaves no bits of an 8-bit sample.
        Assert.Throws<InvalidImageContentException>(() => Image.Load<L8>(data));
    }

    [Fact]
    public void Lossless_RestartIntervalSplittingARow_IsNotSupported()
    {
        byte[] data = FixturePath.Read("jpeglossless/gray12_restart_p5.jpg");
        int dri = IndexOfMarker(data, 0xDD);
        data[dri + 4] = 0;
        data[dri + 5] = 5; // Five samples: not a whole line of 37.
        Assert.Throws<NotSupportedException>(() => Image.Load<L16>(data));
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(0.9)]
    public void Lossless_Truncated_ThrowsInvalidContent(double fraction)
    {
        byte[] full = FixturePath.Read("jpeglossless/rgb10_restart_pt2_p7.jpg");
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgb48>(full[..(int)(full.Length * fraction)]));
    }

    // ----- 12-bit DCT -----

    [Theory]
    [MemberData(nameof(TwelveBitFixtures))]
    public void TwelveBit_MatchesLibjpegTurboDecode(string name)
    {
        Fixture fixture = Find("jpeg12", name);
        byte[] data = FixturePath.Read($"jpeg12/{fixture.Name}.jpg");
        ushort[] truth = fixture.ReadTruth("jpeg12");
        int[] rows = fixture.TruthRows ?? Enumerable.Range(0, fixture.Height).ToArray();
        int channels = fixture.Channels;

        using Image<Rgba64> wide = Image.Load<Rgba64>(data);
        using Image<Rgb24> narrow = Image.Load<Rgb24>(data);
        Assert.Equal((fixture.Width, fixture.Height), (wide.Width, wide.Height));
        double squared = 0;
        int worst12 = 0;
        int worst8 = 0;
        long count = 0;
        for (int r = 0; r < rows.Length; r++)
        {
            int y = rows[r];
            for (int x = 0; x < fixture.Width; x++)
            {
                int i = ((r * fixture.Width) + x) * channels;
                (int er, int eg, int eb) = channels == 1 ? (truth[i], truth[i], truth[i]) : (truth[i], truth[i + 1], truth[i + 2]);
                Rgba64 p = wide[x, y];
                Rgb24 q = narrow[x, y];

                // A 16-bit channel decoded from a 12-bit file is round(v * 65535 / 4095): recover v exactly.
                int ar = To12(p.R);
                int ag = To12(p.G);
                int ab = To12(p.B);
                Assert.Equal(p.R, To16(ar, 4095));
                foreach (int d in new[] { ar - er, ag - eg, ab - eb })
                {
                    squared += d * d;
                    worst12 = Math.Max(worst12, Math.Abs(d));
                    count++;
                }

                worst8 = Math.Max(worst8, MaxDelta(q.R, q.G, q.B, To8(er, 4095), To8(eg, 4095), To8(eb, 4095)));
            }
        }

        double psnr = squared == 0 ? double.PositiveInfinity : 10 * Math.Log10(4095.0 * 4095.0 / (squared / count));
        output.WriteLine($"{name}: 12-bit PSNR {psnr:F2} dB, worst 12-bit delta {worst12}, worst 8-bit delta {worst8}");
        // Measured: 80.7-85.8 dB, at most 3 of 4095 and 1 of 255 (libjpeg-turbo 3.1.3, integer IDCT).
        Assert.True(psnr >= 72, $"{name}: 12-bit PSNR {psnr:F2} dB against libjpeg-turbo is below 72 dB");
        Assert.True(worst12 <= 8, $"{name}: a 12-bit sample is off by {worst12}");
        Assert.True(worst8 <= 1, $"{name}: an 8-bit channel is off by {worst8}");
    }

    [Theory]
    [MemberData(nameof(TwelveBitFixtures))]
    public void TwelveBit_IdentifyAndMetadataReportPrecision(string name)
    {
        Fixture fixture = Find("jpeg12", name);
        byte[] data = FixturePath.Read($"jpeg12/{fixture.Name}.jpg");
        ImageInfo info = Image.Identify(data);
        Assert.Equal(fixture.Channels * 12, info.PixelType.BitsPerPixel);
        Assert.Equal(12, info.Metadata.GetJpegMetadata().BitsPerSample);
        Assert.False(info.Metadata.GetJpegMetadata().Lossless);
        using Image<L16> gray = Image.Load<L16>(data);
        Assert.Equal(12, gray.Metadata.GetJpegMetadata().BitsPerSample);
    }

    [Fact]
    public void TwelveBit_EmbeddedIccProfileSurvives()
    {
        byte[] data = FixturePath.Read("jpeg12/monkey12.jpg");
        using Image<Rgb48> image = Image.Load<Rgb48>(data);
        Assert.NotNull(image.Metadata.IccProfile);
    }

    [Theory]
    [InlineData("ycc12_420_q85", 2)]
    [InlineData("ycc12_422_q88", 4)]
    [InlineData("monkey12", 8)]
    [InlineData("gray12_q90", 2)]
    public void TwelveBit_ReducedDecodeAveragesTheFullDecode(string name, int denominator)
    {
        // libjpeg-turbo's scaled decode is not reachable from the fixture generator, so this checks the reduced
        // transform against box averages of this decoder's own full-size output, which the test above ties to
        // libjpeg-turbo.
        byte[] data = FixturePath.Read($"jpeg12/{name}.jpg");
        using Image<Rgba64> full = Image.Load<Rgba64>(data);
        using Image<Rgba64> reduced = new JpegDecoder().DecodeReduced<Rgba64>(data, denominator);
        Assert.Equal((full.Width + denominator - 1) / denominator, reduced.Width);
        double squared = 0;
        long count = 0;
        for (int y = 0; y < full.Height / denominator; y++)
        {
            for (int x = 0; x < full.Width / denominator; x++)
            {
                double r = 0;
                for (int dy = 0; dy < denominator; dy++)
                {
                    for (int dx = 0; dx < denominator; dx++)
                    {
                        r += full[(x * denominator) + dx, (y * denominator) + dy].R;
                    }
                }

                double d = (r / (denominator * denominator)) - reduced[x, y].R;
                squared += d * d;
                count++;
            }
        }

        double psnr = 10 * Math.Log10(65535.0 * 65535.0 / Math.Max(squared / count, 1e-9));
        output.WriteLine($"{name}/{denominator}: PSNR against box-averaged full decode {psnr:F2} dB");
        Assert.True(psnr >= 30, $"{name}/{denominator}: PSNR {psnr:F2} dB");
    }

    // ----- Unsupported coding -----

    // Sequential arithmetic coding (0xC9) is decoded; see LibjpegTurboConformanceTests (testimgari.jpg).
    [Theory]
    [InlineData(0xCA)] // Progressive, arithmetic
    [InlineData(0xCB)] // Lossless, arithmetic
    [InlineData(0xCD)] // Hierarchical, arithmetic
    public void UnsupportedArithmeticCoding_ThrowsNotSupportedWithClearMessage(byte sof)
    {
        byte[] data = FixturePath.Read("jpeg/baseline_444.jpg");
        data[IndexOfMarker(data, 0xC0) + 1] = sof;
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => Image.Load<Rgb24>(data));
        Assert.Contains("arithmetic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0xC5)]
    [InlineData(0xC7)]
    public void Hierarchical_ThrowsNotSupported(byte sof)
    {
        byte[] data = FixturePath.Read("jpeg/baseline_444.jpg");
        data[IndexOfMarker(data, 0xC0) + 1] = sof;
        Assert.Throws<NotSupportedException>(() => Image.Load<Rgb24>(data));
    }

    [Fact]
    public void ByteFlipFuzz_OnlyDocumentedExceptionsEscape()
    {
        string[] files =
        {
            "jpeg12/monkey12.jpg", "jpeg12/ycc12_420_q85.jpg", "jpeglossless/gray16_p4.jpg",
            "jpeglossless/rgb10_restart_pt2_p7.jpg", "jpeglossless/cmyk12_p4.jpg", "jpeglossless/rgb8_sub22_p1.jpg",
        };
        var rng = new Random(20261104);
        int decoded = 0;
        for (int iteration = 0; iteration < 240; iteration++)
        {
            byte[] data = FixturePath.Read(files[iteration % files.Length]);
            for (int f = 0; f < 1 + rng.Next(3); f++)
            {
                data[rng.Next(data.Length)] ^= (byte)(1 << rng.Next(8));
            }

            try
            {
                using Image<Rgba64> image = Image.Load<Rgba64>(data);
                decoded++;
            }
            catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
            {
            }
        }

        output.WriteLine($"fuzz: {decoded} of 240 mutated files still decoded");
    }

    // ----- Helpers -----

    /// <summary>The RGB triple, at the file's precision, that each pixel of a lossless fixture must decode to.</summary>
    private static int[] ExpectedRgb(Fixture fixture, ushort[] truth, int max)
    {
        int pixels = fixture.Width * fixture.Height;
        var rgb = new int[pixels * 3];
        for (int p = 0; p < pixels; p++)
        {
            switch (fixture.ColorType)
            {
                case "Grayscale":
                    rgb[3 * p] = rgb[(3 * p) + 1] = rgb[(3 * p) + 2] = truth[p];
                    break;
                case "Cmyk":
                    // Plain CMYK (no Adobe marker): R = (max - C)(max - K) / max, rounded.
                    int k = max - truth[(4 * p) + 3];
                    for (int c = 0; c < 3; c++)
                    {
                        rgb[(3 * p) + c] = (int)((((long)(max - truth[(4 * p) + c]) * k) + (max / 2)) / max);
                    }

                    break;
                default:
                    rgb[3 * p] = truth[3 * p];
                    rgb[(3 * p) + 1] = truth[(3 * p) + 1];
                    rgb[(3 * p) + 2] = truth[(3 * p) + 2];
                    break;
            }
        }

        return rgb;
    }

    private static int MaxDelta(int r, int g, int b, int er, int eg, int eb)
        => Math.Max(Math.Abs(r - er), Math.Max(Math.Abs(g - eg), Math.Abs(b - eb)));

    private static ushort To16(int value, int max) => (ushort)((((long)value * 65535) + (max / 2)) / max);

    private static byte To8(int value, int max) => (byte)((((long)value * 255) + (max / 2)) / max);

    private static int To12(ushort value) => (int)((((long)value * 4095) + 32767) / 65535);

    private static int IndexOfMarker(byte[] data, byte marker)
    {
        for (int i = 2; i + 1 < data.Length; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == marker)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"marker {marker:X2} not found");
    }

    private static Fixture Find(string folder, string name) => Manifest(folder).Single(f => f.Name == name);

    private static List<Fixture> Manifest(string folder)
    {
        using JsonDocument document = JsonDocument.Parse(FixturePath.Read($"{folder}/manifest.json"));
        var fixtures = new List<Fixture>();
        foreach (JsonElement entry in document.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            fixtures.Add(new Fixture(
                entry.GetProperty("name").GetString()!,
                entry.GetProperty("width").GetInt32(),
                entry.GetProperty("height").GetInt32(),
                entry.GetProperty("channels").GetInt32(),
                entry.GetProperty("precision").GetInt32(),
                entry.TryGetProperty("colorType", out JsonElement color) ? color.GetString()! : "Unknown",
                entry.TryGetProperty("truthRows", out JsonElement rows) ? rows.EnumerateArray().Select(r => r.GetInt32()).ToArray() : null));
        }

        return fixtures;
    }

    private sealed record Fixture(string Name, int Width, int Height, int Channels, int Precision, string ColorType, int[]? TruthRows)
    {
        public ushort[] ReadTruth(string folder)
        {
            using var compressed = new MemoryStream(FixturePath.Read($"{folder}/{this.Name}.u16.gz"));
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var raw = new MemoryStream();
            gzip.CopyTo(raw);
            byte[] bytes = raw.ToArray();
            var samples = new ushort[bytes.Length / 2];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = (ushort)(bytes[2 * i] | (bytes[(2 * i) + 1] << 8));
            }

            return samples;
        }
    }
}
