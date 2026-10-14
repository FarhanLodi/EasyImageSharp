using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests;

public class JpegReducedDecodeTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cases()
    {
        string[] names = ["baseline_444", "baseline_gray", "baseline_422", "baseline_420", "baseline_420_odd",
            "progressive_444", "progressive_gray", "progressive_422", "progressive_420", "progressive_420_odd",
            "restart_baseline_420", "restart_progressive_420", "cmyk_adobe", "ycck_adobe"];
        foreach (string name in names)
        foreach (int denominator in new[] { 2, 4, 8 })
            yield return [name, denominator];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ReducedDecodeMatchesIndependentLibjpegReference(string name, int denominator)
    {
        byte[] data = FixturePath.Read($"jpeg/{name}.jpg");
        using Image<Rgb24> actual = new JpegDecoder().DecodeReduced<Rgb24>(data, denominator);
        using Image<Rgb24> expected = Image.Load<Rgb24>(FixturePath.Get($"jpeg-reduced/{name}-{denominator}.png"));
        Assert.Equal(expected.Size, actual.Size);
        double squared = 0;
        int maximum = 0;
        for (int y = 0; y < actual.Height; y++)
        for (int x = 0; x < actual.Width; x++)
        {
            Rgb24 a = actual[x, y], b = expected[x, y];
            foreach (int delta in new[] { a.R - b.R, a.G - b.G, a.B - b.B })
            {
                squared += delta * delta;
                maximum = Math.Max(maximum, Math.Abs(delta));
            }
        }

        double mse = squared / (actual.Width * actual.Height * 3);
        double psnr = mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255 * 255 / mse);
        output.WriteLine($"{name}/{denominator}: PSNR={psnr:F3}dB, maximum={maximum}");
        Assert.True(psnr >= 40, $"PSNR {psnr:F3}dB <40, maximum={maximum}");
        Assert.InRange(maximum, 0, 8);
    }

    [Theory]
    [InlineData("baseline_420_odd")]
    [InlineData("progressive_444")]
    [InlineData("cmyk_adobe")]
    public void ScaleOnePreservesDefaultBytes(string name)
    {
        byte[] data = FixturePath.Read($"jpeg/{name}.jpg");
        using Image<Rgb24> original = Image.Load<Rgb24>(data);
        using Image<Rgb24> reduced = new JpegDecoder().DecodeReduced<Rgb24>(data, 1);
        Assert.Equal(original.Frames.RootFrame.PixelSpan.ToArray(), reduced.Frames.RootFrame.PixelSpan.ToArray());
    }

    [Fact]
    public void ReducedDecodeRetainsSourceLimitsAndRejectsInvalidScale()
    {
        byte[] data = FixturePath.Read("jpeg/baseline_444.jpg");
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegDecoder().DecodeReduced<Rgb24>(data, 3));
        Assert.Throws<ImageSizeLimitExceededException>(() => new JpegDecoder().DecodeReduced<Rgb24>(data, 8, new DecoderOptions { MaxPixels = 100 }));
        Assert.Throws<InvalidImageContentException>(() => new JpegDecoder().DecodeReduced<Rgb24>(data.AsSpan(0, data.Length - 2), 8));
    }

    [Theory]
    [InlineData("baseline_444")]
    [InlineData("baseline_420")]
    [InlineData("progressive_420")]
    public void ReducedDecodeActuallyAllocatesSmallerComponentPlanes(string name)
    {
        byte[] data = FixturePath.Read($"jpeg/{name}.jpg");
        var full = new JpegDecoderCore(data, data.Length, DecoderOptions.Default);
        full.ParseAndDecode();
        foreach (int denominator in new[] { 2, 4, 8 })
        {
            var reduced = new JpegDecoderCore(data, data.Length, DecoderOptions.Default, denominator);
            reduced.ParseAndDecode();
            Assert.True(reduced.DecodedPlaneBytes <= full.DecodedPlaneBytes / 2);
            Assert.Equal(full.CoefficientBytes, reduced.CoefficientBytes);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void TinyImageRoundsDimensionsUp(int denominator)
    {
        using var image = new Image<Rgb24>(1, 1, new Rgb24(80, 100, 120));
        using var bytes = new MemoryStream();
        image.Save(bytes, new JpegEncoder());
        using Image<Rgb24> actual = new JpegDecoder().DecodeReduced<Rgb24>(bytes.ToArray(), denominator);
        Assert.Equal(new Size(1, 1), actual.Size);
    }

    [Fact]
    public void ReducedDecodeRetainsDensityMetadata()
    {
        using var image = new Image<Rgb24>(17, 13, new Rgb24(80, 100, 120));
        image.Metadata.HorizontalResolution = 300;
        image.Metadata.VerticalResolution = 200;
        image.Metadata.ResolutionUnits = EasyImageSharp.Metadata.PixelResolutionUnit.PixelsPerInch;
        using var encoded = new MemoryStream();
        image.Save(encoded, new JpegEncoder());
        using Image<Rgb24> actual = new JpegDecoder().DecodeReduced<Rgb24>(encoded.ToArray(), 4);
        Assert.Equal(new Size(5, 4), actual.Size);
        Assert.Equal(300, actual.Metadata.HorizontalResolution, 3);
        Assert.Equal(200, actual.Metadata.VerticalResolution, 3);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void DirectReducedTransformMatchesIndependentDoubleDctAverages(int denominator)
    {
        float[] coefficients = Enumerable.Range(0, 64).Select(i => (float)((i * 37) % 43 - 21)).ToArray();
        float[] actual = coefficients.ToArray();
        int size = 8 / denominator;
        JpegReducedIdct.Transform(actual, new float[64], size);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double expected = 0;
            for (int dy = 0; dy < denominator; dy++)
            for (int dx = 0; dx < denominator; dx++)
            for (int v = 0; v < 8; v++)
            for (int u = 0; u < 8; u++)
            {
                double cu = u == 0 ? 1 / Math.Sqrt(2) : 1;
                double cv = v == 0 ? 1 / Math.Sqrt(2) : 1;
                expected += 0.25 * cu * cv * coefficients[v * 8 + u]
                    * Math.Cos((2 * (x * denominator + dx) + 1) * u * Math.PI / 16)
                    * Math.Cos((2 * (y * denominator + dy) + 1) * v * Math.PI / 16)
                    / (denominator * denominator);
            }

            Assert.InRange(Math.Abs(expected - actual[y * size + x]), 0, 0.00001);
        }
    }
}
