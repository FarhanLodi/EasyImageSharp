using System.Text.Json;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests;

/// <summary>Bilateral and guided filters against the numpy references of <c>Fixtures/gen_denoise.py</c>.</summary>
public class DenoiseTests
{
    private readonly ITestOutputHelper output;

    public DenoiseTests(ITestOutputHelper output) => this.output = output;

    private static JsonElement Case(string expected)
    {
        using JsonDocument manifest = JsonDocument.Parse(FixturePath.Read("denoise/manifest.json"));
        return manifest.RootElement.GetProperty("cases").EnumerateArray()
            .Single(c => c.GetProperty("expected").GetString() == expected).Clone();
    }

    private static Image<Rgba32> Load(string name) => Image.Load<Rgba32>(FixturePath.Get("denoise/" + name));

    [Theory]
    [InlineData("bilateral_r3.png")]
    [InlineData("bilateral_r5.png")]
    public void ExactBilateralMatchesBruteForceReference(string expectedName)
    {
        JsonElement c = Case(expectedName);
        using Image<Rgba32> image = Load(c.GetProperty("source").GetString()!);
        using Image<Rgba32> expected = Load(expectedName);
        image.Mutate(x => x.BilateralFilter(new BilateralFilterOptions
        {
            Radius = c.GetProperty("radius").GetInt32(),
            SigmaSpatial = c.GetProperty("sigma_spatial").GetSingle(),
            SigmaRange = c.GetProperty("sigma_range").GetSingle(),
            Method = BilateralFilterMethod.Exact,
        }));
        Assert.True(EffectsTests.MaxDifference(expected, image) <= 1, $"max difference {EffectsTests.MaxDifference(expected, image)}");
    }

    [Fact]
    public void GridApproximationStaysCloseToTheExactFilter()
    {
        // Declared tolerance of the approximation against the brute-force reference: mean absolute error at most
        // 2 levels and no channel off by more than 40 levels (measured: 1.46 and 30). The largest errors sit on the
        // strongest colour edges, where the grid's per-channel range term and window-free falloff differ most from
        // the exact filter's joint RGB distance.
        JsonElement c = Case("bilateral_large_r12.png");
        using Image<Rgba32> image = Load("noisy_large.png");
        using Image<Rgba32> expected = Load("bilateral_large_r12.png");
        var options = new BilateralFilterOptions
        {
            Radius = c.GetProperty("radius").GetInt32(),
            SigmaSpatial = c.GetProperty("sigma_spatial").GetSingle(),
            SigmaRange = c.GetProperty("sigma_range").GetSingle(),
        };
        Assert.True(options.EffectiveRadius > BilateralFilterOptions.MaxExactRadius); // Auto picks the grid here
        using Image<Rgba32> grid = image.Clone(x => x.BilateralFilter(options));
        using Image<Rgba32> exact = image.Clone(x => x.BilateralFilter(new BilateralFilterOptions
        {
            Radius = options.Radius,
            SigmaSpatial = options.SigmaSpatial,
            SigmaRange = options.SigmaRange,
            Method = BilateralFilterMethod.Exact,
        }));
        Assert.True(EffectsTests.MaxDifference(expected, exact, includeAlpha: false) <= 1);

        (double mean, int max) = MeanAndMax(expected, grid);
        this.output.WriteLine(FormattableString.Invariant($"grid vs exact: mean {mean:F3}, max {max}"));
        Assert.True(mean <= 2.0, FormattableString.Invariant($"mean absolute error {mean:F3}"));
        Assert.True(max <= 40, $"max error {max}");

        // And it really smooths: the noise level drops by more than half.
        Assert.True(ImageQuality.EstimateNoise(grid) < ImageQuality.EstimateNoise(image) / 2);
    }

    [Theory]
    [InlineData("guided_r2.png")]
    [InlineData("guided_r4.png")]
    [InlineData("guided_joint_r3.png")]
    public void GuidedFilterMatchesReference(string expectedName)
    {
        JsonElement c = Case(expectedName);
        using Image<Rgba32> image = Load(c.GetProperty("source").GetString()!);
        using Image<Rgba32> expected = Load(expectedName);
        int radius = c.GetProperty("radius").GetInt32();
        float epsilon = c.GetProperty("epsilon").GetSingle();
        if (c.TryGetProperty("guide", out JsonElement guideName))
        {
            using Image<Rgba32> guide = Load(guideName.GetString()!);
            image.Mutate(x => x.GuidedFilter(guide, radius, epsilon));
        }
        else
        {
            image.Mutate(x => x.GuidedFilter(radius, epsilon));
        }

        Assert.True(EffectsTests.MaxDifference(expected, image) <= 1, $"max difference {EffectsTests.MaxDifference(expected, image)}");
    }

    [Fact]
    public void FiltersKeepAlphaAndAreIndependentOfParallelism()
    {
        using Image<Rgba32> source = Load("noisy_large.png");
        source.Mutate(x => x.Opacity(0.5f));
        foreach (Action<IImageProcessingContext> op in new Action<IImageProcessingContext>[]
        {
            x => x.BilateralFilter(2f, 0.1f),
            x => x.BilateralFilter(new BilateralFilterOptions { SigmaSpatial = 4, Method = BilateralFilterMethod.Grid }),
            x => x.GuidedFilter(),
        })
        {
            using Image<Rgba32> parallel = source.Clone(op);
            using Image<Rgba32> serial = source.Clone(new ImageExecutionOptions { Configuration = new Configuration { MaxDegreeOfParallelism = 1 } }, op);
            Assert.Equal(EffectsTests.Checksum(serial), EffectsTests.Checksum(parallel));
            Assert.Equal(source[5, 5].A, parallel[5, 5].A);
        }
    }

    [Fact]
    public void WideFormatsAreFilteredAtFullPrecision()
    {
        using Image<Rgba32> source = Load("noisy.png");
        using Image<Rgba64> wide = source.CloneAs<Rgba64>();
        using Image<Rgba32> narrow = source.Clone(x => x.GuidedFilter(2, 0.01f));
        wide.Mutate(x => x.GuidedFilter(2, 0.01f));
        using Image<Rgba32> wideAsNarrow = wide.CloneAs<Rgba32>();
        Assert.True(EffectsTests.MaxDifference(narrow, wideAsNarrow) <= 1);
        Assert.Contains(Enumerable.Range(0, 40), x => wide[x, 10].R % 257 != 0); // values between 8-bit codes exist
    }

    [Fact]
    public void ValidatesParameters()
    {
        using var image = new Image<Rgba32>(8, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BilateralFilterOptions { SigmaSpatial = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BilateralFilterOptions { SigmaRange = float.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new BilateralFilterOptions { Radius = 65 });
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(x => x.BilateralFilter(new BilateralFilterOptions { Method = (BilateralFilterMethod)5 })));
        Assert.Throws<ArgumentNullException>(() => image.Mutate(x => x.BilateralFilter(null!)));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(x => x.GuidedFilter(0, 0.01f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(x => x.GuidedFilter(2, 0f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(x => x.GuidedFilter(2, float.PositiveInfinity)));
        Assert.Throws<ArgumentException>(() => image.Mutate(x => x.GuidedFilter(new Image<Rgba32>(9, 8), 2, 0.01f)));
        Assert.Throws<ArgumentNullException>(() => image.Mutate(x => x.GuidedFilter(null!, 2, 0.01f)));
        Assert.Equal(6, new BilateralFilterOptions { SigmaSpatial = 3 }.EffectiveRadius);
    }

    [Fact]
    public void HonoursCancellation()
    {
        using Image<Rgba32> image = Load("noisy_large.png");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => image.Clone(
            new ImageExecutionOptions { CancellationToken = cancelled.Token }, x => x.BilateralFilter()));
    }

    private static (double Mean, int Max) MeanAndMax(Image<Rgba32> a, Image<Rgba32> b)
    {
        long sum = 0;
        int max = 0;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                Rgba32 p = a[x, y], q = b[x, y];
                foreach (int d in new[] { Math.Abs(p.R - q.R), Math.Abs(p.G - q.G), Math.Abs(p.B - q.B) })
                {
                    sum += d;
                    max = Math.Max(max, d);
                }
            }
        }

        return (sum / (3.0 * a.Width * a.Height), max);
    }
}
