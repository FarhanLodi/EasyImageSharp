using System.Text.Json;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Quality metrics against the numpy references of <c>Fixtures/gen_quality.py</c> (written from the papers;
/// its SSIM and PSNR agree with scikit-image 0.24 to within 6e-15). The library computes in double precision
/// with a different summation order, so the declared tolerance is 1e-9 absolute for SSIM/MS-SSIM and the
/// no-reference statistics (relative for the large sharpness numbers) and 1e-9 dB for PSNR.
/// </summary>
public class ImageQualityTests
{
    private const double Tolerance = 1e-9;

    private static readonly JsonElement Manifest = JsonDocument.Parse(FixturePath.Read("quality/manifest.json")).RootElement;

    private static Image<Rgb24> Load(string name) => Image.Load<Rgb24>(FixturePath.Get("quality/" + name));

    public static TheoryData<string> Distortions => new() { "noise.png", "blur.png", "posterize.png", "bright.png", "ref.png" };

    [Theory]
    [MemberData(nameof(Distortions))]
    public void FullReferenceMetricsMatchNumpyReference(string distortedName)
    {
        JsonElement entry = Manifest.GetProperty("comparisons").EnumerateArray()
            .Single(e => e.GetProperty("distorted").GetString() == distortedName);
        using Image<Rgb24> reference = Load("ref.png");
        using Image<Rgb24> distorted = Load(distortedName);
        foreach ((QualityChannels channels, string suffix) in new[] { (QualityChannels.Luma, "luma"), (QualityChannels.Rgb, "rgb") })
        {
            double psnr = ImageQuality.Psnr(reference, distorted, channels);
            JsonElement expectedPsnr = entry.GetProperty("psnr_" + suffix);
            if (expectedPsnr.ValueKind == JsonValueKind.Null)
            {
                Assert.True(double.IsPositiveInfinity(psnr));
            }
            else
            {
                Assert.Equal(expectedPsnr.GetDouble(), psnr, Tolerance);
            }

            Assert.Equal(entry.GetProperty("ssim_" + suffix).GetDouble(), ImageQuality.Ssim(reference, distorted, channels), Tolerance);
            Assert.Equal(entry.GetProperty("msssim_" + suffix).GetDouble(), ImageQuality.MsSsim(reference, distorted, channels), Tolerance);
        }
    }

    [Fact]
    public void NoReferenceMetricsMatchNumpyReference()
    {
        foreach (JsonElement entry in Manifest.GetProperty("no_reference").EnumerateArray())
        {
            using Image<Rgb24> image = Load(entry.GetProperty("file").GetString()!);
            SharpnessMetrics sharpness = ImageQuality.MeasureSharpness(image);
            AssertRelative(entry.GetProperty("laplacian_variance").GetDouble(), sharpness.LaplacianVariance);
            AssertRelative(entry.GetProperty("tenengrad").GetDouble(), sharpness.Tenengrad);
            Assert.Equal(entry.GetProperty("noise_sigma").GetDouble(), ImageQuality.EstimateNoise(image), 1e-8);
        }
    }

    [Fact]
    public void ImmerkaerRecoversTheSyntheticNoiseLevel()
    {
        // Ground truth by construction: grey 128 plus Gaussian noise of sigma 5, rounded to 8 bits.
        using Image<Rgb24> image = Load("flatnoise.png");
        double sigma = ImageQuality.EstimateNoise(image);
        Assert.InRange(sigma, 4.5, 5.5);
    }

    [Fact]
    public void BlurLowersSharpnessAndNoiseRaisesTheNoiseEstimate()
    {
        using Image<Rgb24> reference = Load("ref.png");
        using Image<Rgb24> blurred = Load("blur.png");
        using Image<Rgb24> noisy = Load("noise.png");
        Assert.True(ImageQuality.MeasureSharpness(blurred).LaplacianVariance < ImageQuality.MeasureSharpness(reference).LaplacianVariance / 10);
        Assert.True(ImageQuality.EstimateNoise(noisy) > ImageQuality.EstimateNoise(reference) * 3);
    }

    [Fact]
    public void ExposureMatchesConstructedImage()
    {
        JsonElement expected = Manifest.GetProperty("exposure");
        using Image<Rgba32> image = Image.Load<Rgba32>(FixturePath.Get("quality/exposure.png"));
        ExposureMetrics metrics = ImageQuality.MeasureExposure(image);
        Assert.Equal(expected.GetProperty("pixels").GetInt64(), metrics.PixelCount);
        Assert.Equal(expected.GetProperty("mean_luma").GetDouble(), metrics.MeanLuma, Tolerance);
        Assert.Equal(expected.GetProperty("shadow").GetDouble(), metrics.ShadowClipping, Tolerance);
        Assert.Equal(expected.GetProperty("highlight").GetDouble(), metrics.HighlightClipping, Tolerance);
        Assert.Equal(expected.GetProperty("channel_shadow").GetDouble(), metrics.ChannelShadowClipping, Tolerance);
        Assert.Equal(expected.GetProperty("channel_highlight").GetDouble(), metrics.ChannelHighlightClipping, Tolerance);

        // Percentiles come from a 4096-bin histogram: the true order statistic lies in the reported bin.
        double median = expected.GetProperty("median_luma_sorted").GetDouble();
        Assert.InRange(metrics.Median, median, median + (1.0 / 4096));
        Assert.Equal(1.0 / 4096, metrics.Percentile1, Tolerance); // the black rows hold the lowest quarter
        Assert.Equal(1.0, metrics.Percentile99, Tolerance);
        Assert.Throws<ArgumentOutOfRangeException>(() => metrics.GetPercentile(101));
    }

    [Fact]
    public void WideFormatsUseTheirFullPrecision()
    {
        using Image<Rgb24> reference = Load("ref.png");
        using Image<Rgb24> distorted = Load("noise.png");
        using Image<Rgb48> wideReference = reference.CloneAs<Rgb48>();
        using Image<Rgba64> wideDistorted = distorted.CloneAs<Rgba64>();

        // 8-bit values widen exactly (v * 257); wide formats are read through their single-precision normalised
        // components, so the metrics agree to float precision.
        Assert.Equal(ImageQuality.Ssim(reference, distorted), ImageQuality.Ssim(wideReference, wideDistorted), 1e-6);
        Assert.Equal(ImageQuality.Psnr(reference, distorted, QualityChannels.Rgb), ImageQuality.Psnr(wideReference, wideDistorted, QualityChannels.Rgb), 1e-5);

        // A one-step 16-bit change is invisible at 8 bits but must register.
        using Image<Rgb48> nudged = wideReference.Clone();
        Rgb48 p = nudged[10, 10];
        nudged[10, 10] = new Rgb48((ushort)(p.R ^ 1), p.G, p.B);
        Assert.False(double.IsPositiveInfinity(ImageQuality.Psnr(wideReference, nudged)));
    }

    [Fact]
    public void ResultsDoNotDependOnParallelism()
    {
        using Image<Rgb24> reference = Load("ref.png");
        using Image<Rgb24> distorted = Load("noise.png");
        double parallelSsim = ImageQuality.MsSsim(reference, distorted, QualityChannels.Rgb);
        double parallelSharpness = ImageQuality.MeasureSharpness(distorted).LaplacianVariance;
        using var serial = OperationExecution.Enter(new ImageExecutionOptions
        {
            Configuration = new Configuration { MaxDegreeOfParallelism = 1 },
        });
        Assert.Equal(parallelSsim, ImageQuality.MsSsim(reference, distorted, QualityChannels.Rgb));
        Assert.Equal(parallelSharpness, ImageQuality.MeasureSharpness(distorted).LaplacianVariance);
    }

    [Fact]
    public void ValidatesInputs()
    {
        using var a = new Image<Rgb24>(20, 20);
        using var b = new Image<Rgb24>(21, 20);
        using var tiny = new Image<Rgb24>(10, 10);
        Assert.Throws<ArgumentException>(() => ImageQuality.Psnr(a, b));
        Assert.Throws<ArgumentException>(() => ImageQuality.Ssim(tiny, tiny));
        Assert.Throws<ArgumentException>(() => ImageQuality.MsSsim(a, a));
        Assert.Throws<ArgumentNullException>(() => ImageQuality.Ssim<Rgb24, Rgb24>(null!, a));
        Assert.Throws<ArgumentException>(() => ImageQuality.MeasureSharpness(new Image<Rgb24>(2, 5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageQuality.Psnr(a, a, (QualityChannels)7));
        Assert.Equal(0, ImageQuality.MeasureExposure(new Image<Rgba32>(4, 4)).PixelCount);
    }

    [Fact]
    public void HonoursCancellation()
    {
        using Image<Rgb24> reference = Load("ref.png");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ImageQuality.Ssim(reference, reference, cancellationToken: cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => ImageQuality.MeasureExposure(reference, cancelled.Token));
    }

    private static void AssertRelative(double expected, double actual)
        => Assert.True(Math.Abs(expected - actual) <= Tolerance * Math.Max(1, Math.Abs(expected)), $"expected {expected}, got {actual}");
}
