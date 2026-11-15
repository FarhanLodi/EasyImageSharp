using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Randomised differential tests of the document-processing vector kernels (local statistics, local thresholds and
/// grey-level morphology) against their scalar reference paths, which are selected explicitly rather than through
/// <see cref="SimdConfig.ForceScalarFallback"/> so concurrently running tests cannot interfere. The masks and planes
/// must be identical byte for byte; the window statistics identical bit for bit.
/// </summary>
public class DocumentSimdDifferentialTests
{
    // ----- Local statistics and thresholds -----

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 15)]
    [InlineData(4, 25)]
    [InlineData(5, 51)]
    public void RowWindowStatsAreBitIdentical(int seed, int windowSize)
    {
        var random = new Random(seed);
        foreach ((int width, int height) in Sizes(random, windowSize))
        {
            byte[] plane = RandomPage(random, width, height);
            var integral = new IntegralImage(plane, width, height, withSquares: true);
            int half = windowSize / 2;
            var meanScalar = new double[width];
            var varianceScalar = new double[width];
            var areaScalar = new int[width];
            var meanVector = new double[width];
            var varianceVector = new double[width];
            var areaVector = new int[width];
            for (int y = 0; y < height; y++)
            {
                integral.RowWindowStats(y, half, meanScalar, varianceScalar, areaScalar, vector: false);
                integral.RowWindowStats(y, half, meanVector, varianceVector, areaVector, vector: true);
                for (int x = 0; x < width; x++)
                {
                    integral.WindowStats(x, y, half, out double mean, out double variance, out int area);
                    Assert.Equal(BitConverter.DoubleToInt64Bits(mean), BitConverter.DoubleToInt64Bits(meanScalar[x]));
                    Assert.Equal(BitConverter.DoubleToInt64Bits(variance), BitConverter.DoubleToInt64Bits(varianceScalar[x]));
                    Assert.Equal(area, areaScalar[x]);
                    if (BitConverter.DoubleToInt64Bits(mean) != BitConverter.DoubleToInt64Bits(meanVector[x])
                        || BitConverter.DoubleToInt64Bits(variance) != BitConverter.DoubleToInt64Bits(varianceVector[x])
                        || area != areaVector[x])
                    {
                        Assert.Fail($"{width}x{height} window {windowSize}: ({x}, {y}) scalar {mean}/{variance}/{area}, vector {meanVector[x]}/{varianceVector[x]}/{areaVector[x]}.");
                    }
                }
            }
        }
    }

    [Fact]
    public void IntegralImageSumsMatchBruteForce()
    {
        // The vertical accumulation runs through the vector kernel whenever vectors are enabled; check it exactly.
        var random = new Random(9);
        for (int iteration = 0; iteration < 40; iteration++)
        {
            int width = random.Next(1, 90);
            int height = random.Next(1, 40);
            byte[] plane = RandomPage(random, width, height);
            var integral = new IntegralImage(plane, width, height, withSquares: true);
            for (int probe = 0; probe < 50; probe++)
            {
                int x1 = random.Next(width);
                int x2 = random.Next(x1, width);
                int y1 = random.Next(height);
                int y2 = random.Next(y1, height);
                long sum = 0;
                long squares = 0;
                for (int y = y1; y <= y2; y++)
                {
                    for (int x = x1; x <= x2; x++)
                    {
                        int v = plane[(y * width) + x];
                        sum += v;
                        squares += v * v;
                    }
                }

                Assert.Equal(sum, integral.Sum(x1, y1, x2, y2));
                Assert.Equal(squares, integral.SumOfSquares(x1, y1, x2, y2));
            }
        }
    }

    [Theory]
    [InlineData("Niblack", -0.2f)]
    [InlineData("Niblack", 0.7f)]
    [InlineData("WolfJolion", 0.5f)]
    [InlineData("WolfJolion", 0.13f)]
    [InlineData("Phansalkar", 0.25f)]
    [InlineData("Nick", -0.1f)]
    [InlineData("Nick", -0.37f)]
    public void LocalThresholdMasksAreIdentical(string method, float k)
    {
        LocalThresholdOps.Kind kind = Enum.Parse<LocalThresholdOps.Kind>(method);
        var random = new Random(((int)kind * 1000) + (int)(k * 100));
        foreach (int windowSize in new[] { 1, 3, 15, 25, 51 })
        {
            foreach ((int width, int height) in Sizes(random, windowSize))
            {
                byte[] plane = RandomPage(random, width, height);
                byte[] reference = ReferenceInkMask(plane, width, height, kind, windowSize, k, 2f, 10f);
                byte[] scalar = LocalThresholdOps.ComputeInkMask(plane, width, height, kind, windowSize, k, 2f, 10f, vector: false);
                byte[] vector = LocalThresholdOps.ComputeInkMask(plane, width, height, kind, windowSize, k, 2f, 10f, vector: true);
                Assert.True(reference.AsSpan().SequenceEqual(scalar), $"{kind} {width}x{height} window {windowSize}: the scalar path changed.");
                for (int i = 0; i < reference.Length; i++)
                {
                    if (reference[i] != vector[i])
                    {
                        Assert.Fail($"{kind} {width}x{height} window {windowSize}: pixel {i} reference {reference[i]}, vector {vector[i]}.");
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(15, 0.2f)]
    [InlineData(25, 0.5f)]
    [InlineData(51, 0.34f)]
    public void SauvolaThresholdsMatchTheScalarFormula(int windowSize, float k)
    {
        // The per-pixel formula of FilterOps.SauvolaThreshold (window sums from the summed-area table, a long area),
        // against the row statistics plus the vector Sauvola kernel.
        const double R = 128.0;
        var random = new Random(windowSize);
        int half = windowSize / 2;
        foreach ((int width, int height) in Sizes(random, windowSize))
        {
            byte[] plane = RandomPage(random, width, height);
            var integral = new IntegralImage(plane, width, height, withSquares: true);
            var means = new double[width];
            var variances = new double[width];
            var areas = new int[width];
            var thresholds = new double[width];
            for (int y = 0; y < height; y++)
            {
                integral.RowWindowStats(y, half, means, variances, areas, vector: true);
                int done = IntegralImageSimd.SauvolaThresholds(means, variances, k, R, thresholds);
                for (int x = 0; x < width; x++)
                {
                    int x1 = Math.Max(0, x - half);
                    int x2 = Math.Min(width - 1, x + half);
                    int y1 = Math.Max(0, y - half);
                    int y2 = Math.Min(height - 1, y + half);
                    long area = (long)(x2 - x1 + 1) * (y2 - y1 + 1);
                    double mean = (double)integral.Sum(x1, y1, x2, y2) / area;
                    double meanSq = (double)integral.SumOfSquares(x1, y1, x2, y2) / area;
                    double stdDev = Math.Sqrt(Math.Max(0, meanSq - (mean * mean)));
                    double expected = mean * (1 + (k * ((stdDev / R) - 1)));
                    double actual = x < done ? thresholds[x] : means[x] * (1 + (k * ((Math.Sqrt(variances[x]) / R) - 1)));
                    Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
                }
            }
        }
    }

    // ----- Morphology -----

    [Theory]
    [InlineData(11, true)]
    [InlineData(12, false)]
    public void RowAndColumnPassesAreIdentical(int seed, bool isMin)
    {
        var random = new Random(seed);
        for (int iteration = 0; iteration < 150; iteration++)
        {
            int width = random.Next(1, 140);
            int height = random.Next(1, 90);
            byte[] plane = random.Next(2) == 0 ? RandomPage(random, width, height) : RandomBinary(random, width, height);
            int before = RandomReach(random, Math.Max(width, height));
            int after = RandomReach(random, Math.Max(width, height));

            byte[] rowsScalar = MorphologyOps.FilterRows(plane, width, height, before, after, isMin, vector: false);
            byte[] rowsVector = MorphologyOps.FilterRows(plane, width, height, before, after, isMin, vector: true);
            AssertSame(rowsScalar, rowsVector, $"rows {width}x{height} [{before}, {after}] min={isMin}");

            byte[] columnsScalar = MorphologyOps.FilterColumns(plane, width, height, before, after, isMin, vector: false);
            byte[] columnsVector = MorphologyOps.FilterColumns(plane, width, height, before, after, isMin, vector: true);
            AssertSame(columnsScalar, columnsVector, $"columns {width}x{height} [{before}, {after}] min={isMin}");
        }
    }

    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    public void ErodeAndDilateAreIdenticalForEveryElementShape(int seed)
    {
        var random = new Random(seed);
        for (int iteration = 0; iteration < 120; iteration++)
        {
            int width = random.Next(1, 160);
            int height = random.Next(1, 100);
            byte[] plane = random.Next(2) == 0 ? RandomPage(random, width, height) : RandomBinary(random, width, height);
            StructuringElement element = RandomElement(random);
            foreach (bool isMin in new[] { true, false })
            {
                byte[] scalar = MorphologyOps.Filter(plane, width, height, element, isMin, vector: false);
                byte[] vector = MorphologyOps.Filter(plane, width, height, element, isMin, vector: true);
                AssertSame(scalar, vector, $"{element.Shape} {element.Width}x{element.Height} on {width}x{height}, min={isMin}");
            }
        }
    }

    [Fact]
    public void LargeImageWithTallWindowUsesEveryColumnStrip()
    {
        // Wider than many 64-byte strips, with a ragged last strip and a window taller than the direct-reduction limit.
        var random = new Random(31);
        const int Width = 64 * 5 + 37;
        const int Height = 211;
        byte[] plane = RandomPage(random, Width, Height);
        foreach (int radius in new[] { 4, 5, 12, 40, 150 })
        {
            StructuringElement element = StructuringElement.Rectangle(3, (2 * radius) + 1);
            foreach (bool isMin in new[] { true, false })
            {
                byte[] scalar = MorphologyOps.Filter(plane, Width, Height, element, isMin, vector: false);
                byte[] vector = MorphologyOps.Filter(plane, Width, Height, element, isMin, vector: true);
                AssertSame(scalar, vector, $"radius {radius} min={isMin}");
            }
        }
    }

    // ----- Helpers -----

    /// <summary>A copy of the per-pixel threshold loop the row-buffer implementation replaced, as the ground truth.</summary>
    private static byte[] ReferenceInkMask(byte[] luminance, int width, int height, LocalThresholdOps.Kind kind, int windowSize, float k, float p, float q)
    {
        int half = windowSize / 2;
        var integral = new IntegralImage(luminance, width, height, withSquares: true);
        var mask = new byte[luminance.Length];
        double globalMin = 0;
        double maxStd = 1;
        if (kind == LocalThresholdOps.Kind.WolfJolion)
        {
            globalMin = 255;
            foreach (byte v in luminance)
            {
                globalMin = Math.Min(globalMin, v);
            }

            double maxVar = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    integral.WindowStats(x, y, half, out _, out double variance, out _);
                    maxVar = Math.Max(maxVar, variance);
                }
            }

            maxStd = Math.Sqrt(maxVar);
            if (maxStd <= 0)
            {
                maxStd = 1;
            }
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                integral.WindowStats(x, y, half, out double mean, out double variance, out int area);
                double threshold = LocalThresholdOps.Threshold(kind, mean, variance, area, k, p, q, globalMin, maxStd);
                mask[(y * width) + x] = luminance[(y * width) + x] >= threshold ? (byte)0 : (byte)1;
            }
        }

        return mask;
    }

    /// <summary>Image sizes around the window size, where the interior/border split changes, plus a few random ones.</summary>
    private static IEnumerable<(int Width, int Height)> Sizes(Random random, int windowSize)
    {
        yield return (1, 1);
        yield return (windowSize, 3);
        yield return (windowSize + 1, 4);
        yield return (windowSize + 2, 2);
        yield return ((2 * windowSize) + 3, windowSize + 1);
        yield return (random.Next(1, 200), random.Next(1, 60));
        yield return (random.Next(100, 300), random.Next(1, 30));
    }

    /// <summary>A page-like plane: a slowly varying background, ink strokes, flat regions and noise.</summary>
    private static byte[] RandomPage(Random random, int width, int height)
    {
        var plane = new byte[width * height];
        int background = random.Next(120, 256);
        int gradient = random.Next(-60, 61);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int value;
                int pick = random.Next(20);
                if (pick == 0)
                {
                    value = random.Next(0, 80);
                }
                else if (pick == 1)
                {
                    value = 255;
                }
                else if ((x / 5) % 4 == 0 && (y / 7) % 3 == 0)
                {
                    value = random.Next(10, 60);
                }
                else
                {
                    value = background + (gradient * x / Math.Max(1, width)) + random.Next(-6, 7);
                }

                plane[(y * width) + x] = (byte)Math.Clamp(value, 0, 255);
            }
        }

        if (random.Next(5) == 0)
        {
            // A perfectly flat plane, where the variances are exactly zero.
            Array.Fill(plane, (byte)random.Next(256));
        }

        return plane;
    }

    private static byte[] RandomBinary(Random random, int width, int height)
    {
        var plane = new byte[width * height];
        for (int i = 0; i < plane.Length; i++)
        {
            plane[i] = random.Next(5) == 0 ? (byte)0 : (byte)255;
        }

        return plane;
    }

    /// <summary>Mostly small reaches, sometimes medium and sometimes beyond the image.</summary>
    private static int RandomReach(Random random, int size) => random.Next(4) switch
    {
        0 => 0,
        1 => random.Next(1, 5),
        2 => random.Next(5, 40),
        _ => random.Next(size, (2 * size) + 2),
    };

    private static StructuringElement RandomElement(Random random)
    {
        switch (random.Next(6))
        {
            case 0:
                return StructuringElement.Square(random.Next(0, 12));
            case 1:
                return StructuringElement.Rectangle(random.Next(1, 40), random.Next(1, 40));
            case 2:
                return StructuringElement.Disk(random.Next(1, 9));
            case 3:
                return StructuringElement.Ellipse(random.Next(1, 25), random.Next(1, 25));
            case 4:
                return StructuringElement.Cross(random.Next(1, 10));
            default:
            {
                int w = (2 * random.Next(0, 4)) + 1;
                int h = (2 * random.Next(0, 4)) + 1;
                var mask = new bool[h, w];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        mask[y, x] = random.Next(3) != 0;
                    }
                }

                mask[h / 2, w / 2] = true;
                return StructuringElement.FromMask(mask);
            }
        }
    }

    private static void AssertSame(byte[] expected, byte[] actual, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                Assert.Fail($"{what}: byte {i} differs, scalar {expected[i]} vs vector {actual[i]}.");
            }
        }
    }
}
