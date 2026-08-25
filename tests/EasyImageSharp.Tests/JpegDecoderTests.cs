using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests;

/// <summary>
/// Decode-path coverage for JPEG features this library's own encoder never produces: progressive (SOF2)
/// frames, chroma subsampling with fancy upsampling, restart intervals, Adobe CMYK/YCCK. Every fixture under
/// <c>Fixtures/jpeg/</c> was written by Pillow (libjpeg-turbo) together with libjpeg's own decode of it, so
/// the tests compare this decoder against libjpeg rather than against the pre-compression source.
/// </summary>
public class JpegDecoderTests
{
    private readonly ITestOutputHelper output;

    public JpegDecoderTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    // ----- Reference decodes -----

    [Theory]
    [InlineData("baseline_444", 45.0, 8)]
    [InlineData("baseline_gray", 45.0, 8)]
    [InlineData("progressive_444", 45.0, 8)]
    [InlineData("progressive_gray", 45.0, 8)]
    [InlineData("baseline_422", 40.0, 0)]
    [InlineData("baseline_420", 40.0, 0)]
    [InlineData("baseline_420_odd", 40.0, 0)]
    [InlineData("progressive_422", 40.0, 0)]
    [InlineData("progressive_420", 40.0, 0)]
    [InlineData("progressive_420_odd", 40.0, 0)]
    [InlineData("restart_baseline_420", 40.0, 0)]
    [InlineData("restart_progressive_420", 40.0, 0)]
    [InlineData("cmyk_adobe", 38.0, 0)]
    [InlineData("ycck_adobe", 38.0, 0)]
    public void Fixture_MatchesLibjpegReferenceDecode(string name, double minPsnr, int maxAbsError)
    {
        using Image<Rgb24> actual = Image.Load<Rgb24>(FixturePath.Get($"jpeg/{name}.jpg"));
        using Image<Rgb24> expected = Image.Load<Rgb24>(FixturePath.Get($"jpeg/{name}.decoded.png"));

        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);

        (double psnr, int maxAbs) = Compare(expected, actual);
        this.output.WriteLine($"{name}: PSNR {psnr:F2} dB, max abs channel error {maxAbs}");
        Assert.True(psnr >= minPsnr, $"{name}: PSNR {psnr:F2} dB is below the required {minPsnr} dB (max abs error {maxAbs}).");
        if (maxAbsError > 0)
        {
            Assert.True(maxAbs <= maxAbsError, $"{name}: max abs channel error {maxAbs} exceeds {maxAbsError} (PSNR {psnr:F2} dB).");
        }
    }

    [Theory]
    [InlineData("444")]
    [InlineData("422")]
    [InlineData("420")]
    [InlineData("420_odd")]
    [InlineData("gray")]
    public void Progressive_DecodesLikeBaselineOfSameSource(string layout)
    {
        using Image<Rgb24> baseline = Image.Load<Rgb24>(FixturePath.Get($"jpeg/baseline_{layout}.jpg"));
        using Image<Rgb24> progressive = Image.Load<Rgb24>(FixturePath.Get($"jpeg/progressive_{layout}.jpg"));

        (double psnr, int maxAbs) = Compare(baseline, progressive);
        this.output.WriteLine($"progressive vs baseline {layout}: PSNR {psnr:F2} dB, max abs {maxAbs}");
        Assert.True(psnr >= 45.0, $"progressive_{layout} vs baseline_{layout}: PSNR {psnr:F2} dB is below 45 dB.");
    }

    [Fact]
    public void ProgressiveGray_DecodesToL8()
    {
        byte[] data = FixturePath.Read("jpeg/progressive_gray.jpg");
        using Image<L8> gray = Image.Load<L8>(data);
        using Image<Rgb24> rgb = Image.Load<Rgb24>(data);
        using Image<L8> reference = Image.Load<L8>(FixturePath.Get("jpeg/progressive_gray.decoded.png"));

        Assert.Equal(96, gray.Width);
        Assert.Equal(72, gray.Height);

        double sumSq = 0;
        for (int y = 0; y < gray.Height; y++)
        {
            for (int x = 0; x < gray.Width; x++)
            {
                byte l = gray[x, y].PackedValue;
                Rgb24 p = rgb[x, y];
                Assert.True(l == p.R && p.R == p.G && p.G == p.B, $"Gray decode differs from RGB decode at ({x},{y}).");
                int d = l - reference[x, y].PackedValue;
                sumSq += d * d;
            }
        }

        double psnr = Psnr(sumSq / (gray.Width * gray.Height));
        this.output.WriteLine($"progressive_gray as L8: PSNR {psnr:F2} dB");
        Assert.True(psnr >= 45.0, $"progressive_gray as L8: PSNR {psnr:F2} dB is below 45 dB.");
    }

    [Theory]
    [InlineData("progressive_420", 96, 72, 3)]
    [InlineData("cmyk_adobe", 96, 72, 4)]
    [InlineData("progressive_gray", 96, 72, 1)]
    public void Identify_ReportsHeaderOfProgressiveAndCmykFiles(string name, int width, int height, int components)
    {
        ImageInfo info = Image.Identify(FixturePath.Read($"jpeg/{name}.jpg"));
        Assert.Equal(width, info.Width);
        Assert.Equal(height, info.Height);
        Assert.Equal(components * 8, info.PixelType.BitsPerPixel);
        Assert.Equal("JPEG", info.Format.Name);
    }

    [Fact]
    public void Cmyk_DecodesToAllPixelFormats()
    {
        byte[] data = FixturePath.Read("jpeg/cmyk_adobe.jpg");
        using Image<Rgba32> rgba = Image.Load<Rgba32>(data);
        using Image<Bgr24> bgr = Image.Load<Bgr24>(data);
        using Image<L8> gray = Image.Load<L8>(data);
        Assert.Equal(255, rgba[10, 10].A);
        Assert.Equal(rgba[10, 10].R, bgr[10, 10].R);
        Assert.Equal(96, gray.Width);
    }

    // ----- Fancy upsampling arithmetic -----

    [Fact]
    public void FancyUpsampling_H2V1_MatchesTriangleFilter()
    {
        byte[] input = { 0, 100, 200 };
        byte[] actual = new byte[6];
        JpegUpsampler.UpsampleH2V1(input, actual);
        Assert.Equal(new byte[] { 0, 25, 75, 125, 175, 200 }, actual);
    }

    [Fact]
    public void FancyUpsampling_H2V2_MatchesTriangleFilter()
    {
        byte[] near = { 0, 100, 200 };
        byte[] far = { 0, 0, 0 };
        byte[] actual = new byte[6];
        JpegUpsampler.UpsampleH2V2(near, far, actual);
        Assert.Equal(new byte[] { 0, 19, 56, 94, 131, 150 }, actual);
    }

    // ----- Malformed input -----

    [Theory]
    [InlineData(0.3)]
    [InlineData(0.6)]
    public void TruncatedProgressive_ThrowsInvalidContentWithoutCrashing(double fraction)
    {
        byte[] full = FixturePath.Read("jpeg/progressive_420.jpg");
        byte[] truncated = full[..(int)(full.Length * fraction)];

        // A stream that ends before EOI is malformed under the documented decoder contract; what matters here is
        // that the progressive path fails cleanly rather than with an index or null-reference exception.
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgb24>(truncated));
    }

    [Theory]
    [InlineData(0, 5, 0x00)]   // DC scan with Se != 0
    [InlineData(1, 0, 0x00)]   // Ss > Se
    [InlineData(1, 64, 0x00)]  // Se out of range
    [InlineData(1, 63, 0x00)]  // AC scan with three components
    [InlineData(0, 0, 0x0E)]   // Al = 14
    [InlineData(0, 0, 0x30)]   // Ah = 3 with Al = 0 (refinement must have Al = Ah - 1)
    public void Progressive_InvalidScanHeader_ThrowsInvalidContent(byte ss, byte se, byte ahAl)
    {
        byte[] data = FixturePath.Read("jpeg/progressive_444.jpg");
        int sos = IndexOfMarker(data, 0xDA);
        Assert.True(sos > 0, "fixture has no SOS marker");
        int componentCount = data[sos + 4];
        Assert.Equal(3, componentCount); // libjpeg's default script starts with an interleaved DC scan.
        int paramsOffset = sos + 5 + (componentCount * 2);
        data[paramsOffset] = ss;
        data[paramsOffset + 1] = se;
        data[paramsOffset + 2] = ahAl;

        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgb24>(data));
    }

    [Fact]
    public void FrameWithoutScan_ThrowsInvalidContent()
    {
        byte[] jpeg =
        {
            0xFF, 0xD8,
            0xFF, 0xC2, 0x00, 0x0B, 0x08, 0x00, 0x08, 0x00, 0x08, 0x01,
            0x01, 0x11, 0x00,
            0xFF, 0xD9,
        };

        Assert.Throws<InvalidImageContentException>(() => Image.Load<L8>(jpeg));
    }

    [Fact]
    public async Task ByteFlipFuzz_OnlyDocumentedExceptionsEscapeAndDecodingTerminates()
    {
        string[] names =
        {
            "baseline_420", "progressive_444", "progressive_420", "progressive_gray",
            "restart_progressive_420", "cmyk_adobe", "ycck_adobe", "progressive_420_odd",
        };
        byte[][] sources = new byte[names.Length][];
        for (int i = 0; i < names.Length; i++)
        {
            sources[i] = FixturePath.Read($"jpeg/{names[i]}.jpg");
        }

        var rng = new Random(20240816);
        var decoder = new JpegDecoder();
        int decodedOk = 0;
        for (int iteration = 0; iteration < 300; iteration++)
        {
            byte[] source = sources[iteration % sources.Length];
            byte[] data = (byte[])source.Clone();
            int flips = 1 + rng.Next(3);
            var positions = new int[flips];
            for (int f = 0; f < flips; f++)
            {
                positions[f] = rng.Next(data.Length);
                data[positions[f]] ^= (byte)(1 << rng.Next(8));
            }

            Task<Exception?> attempt = Task.Run(() =>
            {
                try
                {
                    using Image<Rgb24> image = decoder.Decode<Rgb24>(data, DecoderOptions.Default);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            });

            Exception? result;
            try
            {
                result = await attempt.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                Assert.Fail($"Iteration {iteration} ({names[iteration % names.Length]}, flips at {string.Join(",", positions)}) did not finish within 30 seconds.");
                return;
            }

            if (result is null)
            {
                decodedOk++;
                continue;
            }

            Assert.True(
                result is ImageFormatException or NotSupportedException,
                $"Iteration {iteration} ({names[iteration % names.Length]}, flips at {string.Join(",", positions)}) threw {result.GetType().Name}: {result.Message}");
        }

        this.output.WriteLine($"fuzz: {decodedOk} of 300 mutated files still decoded");
    }

    // ----- Helpers -----

    private static int IndexOfMarker(byte[] data, byte marker)
    {
        for (int i = 0; i + 1 < data.Length; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == marker)
            {
                return i;
            }
        }

        return -1;
    }

    private static (double Psnr, int MaxAbs) Compare(Image<Rgb24> expected, Image<Rgb24> actual)
    {
        double sumSq = 0;
        int maxAbs = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Rgb24 e = expected[x, y];
                Rgb24 a = actual[x, y];
                int dr = e.R - a.R;
                int dg = e.G - a.G;
                int db = e.B - a.B;
                sumSq += (dr * dr) + (dg * dg) + (db * db);
                maxAbs = Math.Max(maxAbs, Math.Max(Math.Abs(dr), Math.Max(Math.Abs(dg), Math.Abs(db))));
            }
        }

        return (Psnr(sumSq / (expected.Width * expected.Height * 3.0)), maxAbs);
    }

    private static double Psnr(double mse)
        => mse <= 0 ? double.PositiveInfinity : 10.0 * Math.Log10(255.0 * 255.0 / mse);
}
