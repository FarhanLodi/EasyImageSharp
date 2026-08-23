using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Processing.Dithering;
using EasyImageSharp.Processing.Quantization;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// The dither kernels: every entry of <see cref="KnownDitherings"/> must run, be deterministic, keep the mean
/// colour of a smooth gradient (error diffusion conserves the error it spreads) and, for the ordered matrices,
/// repeat with the period of the matrix.
/// </summary>
public class DitherTests
{
    private static readonly string[] DitherNames = typeof(KnownDitherings)
        .GetProperties()
        .Where(p => typeof(IDither).IsAssignableFrom(p.PropertyType))
        .Select(p => p.Name)
        .ToArray();

    public static TheoryData<string> AllDitherNames()
    {
        var data = new TheoryData<string>();
        foreach (string name in DitherNames)
        {
            data.Add(name);
        }

        return data;
    }

    public static TheoryData<string> ErrorDitherNames()
    {
        var data = new TheoryData<string>();
        foreach (string name in DitherNames)
        {
            if (Lookup(name) is ErrorDither)
            {
                data.Add(name);
            }
        }

        return data;
    }

    public static TheoryData<string> OrderedDitherNames()
    {
        var data = new TheoryData<string>();
        foreach (string name in DitherNames)
        {
            if (Lookup(name) is OrderedDither)
            {
                data.Add(name);
            }
        }

        return data;
    }

    // ----- The catalogue -----

    [Fact]
    public void TheCatalogueHoldsTheExpectedKernels()
    {
        Assert.Equal(
            new[]
            {
                "FloydSteinberg", "Atkinson", "Burks", "JarvisJudiceNinke", "Sierra2", "Sierra3", "SierraLite",
                "Stucki", "StevensonArce", "Bayer2x2", "Bayer4x4", "Bayer8x8", "Bayer16x16", "Ordered3x3",
            },
            DitherNames);
        Assert.Equal(9, DitherNames.Count(n => Lookup(n) is ErrorDither));
        Assert.Equal(5, DitherNames.Count(n => Lookup(n) is OrderedDither));
    }

    [Theory]
    [MemberData(nameof(AllDitherNames))]
    public void EveryDitherRunsAndProducesOnlyPaletteColours(string name)
    {
        using Image<Rgba32> image = Gradient(32, 24);
        Color[] palette = { Color.Black, Color.White, Color.Red, Color.Blue };

        using Image<Rgba32> result = image.Clone(ctx => ctx.Dither(Lookup(name), 1f, palette));

        HashSet<Rgba32> allowed = palette.Select(c => c.ToRgba32()).ToHashSet();
        for (int y = 0; y < result.Height; y++)
        {
            for (int x = 0; x < result.Width; x++)
            {
                Assert.Contains(result[x, y], allowed);
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllDitherNames))]
    public void EveryDitherIsDeterministic(string name)
    {
        using Image<Rgba32> image = Gradient(48, 32);

        using Image<Rgba32> first = image.Clone(ctx => ctx.Dither(Lookup(name)));
        using Image<Rgba32> second = image.Clone(ctx => ctx.Dither(Lookup(name)));

        AssertPixelsEqual(first, second);
    }

    [Theory]
    [MemberData(nameof(AllDitherNames))]
    public void EveryDitherWorksThroughAQuantizer(string name)
    {
        using Image<Rgba32> image = Gradient(32, 24);
        var quantizer = new WuQuantizer(new QuantizerOptions { MaxColors = 8, Dither = Lookup(name) });

        using Image<Rgba32> result = image.Clone(ctx => ctx.Quantize(quantizer));

        Assert.Equal(image.Width, result.Width);
        Assert.InRange(DistinctColorCount(result), 1, 8);
    }

    [Theory]
    [MemberData(nameof(AllDitherNames))]
    public void EveryDitherHandlesSinglePixelAndSingleRowImages(string name)
    {
        Color[] palette = { Color.Black, Color.White };

        using var one = new Image<Rgba32>(1, 1, new Rgba32(130, 130, 130));
        using var row = new Image<Rgba32>(17, 1, new Rgba32(130, 130, 130));
        using var column = new Image<Rgba32>(1, 17, new Rgba32(130, 130, 130));

        one.Mutate(ctx => ctx.Dither(Lookup(name), 1f, palette));
        row.Mutate(ctx => ctx.Dither(Lookup(name), 1f, palette));
        column.Mutate(ctx => ctx.Dither(Lookup(name), 1f, palette));

        Assert.Equal(1, one.Width);
        Assert.Equal(17, row.Width);
        Assert.Equal(17, column.Height);
    }

    // ----- Error diffusion conserves the mean -----

    [Theory]
    [MemberData(nameof(ErrorDitherNames))]
    public void ErrorDiffusionKeepsTheMeanColourOfAGradient(string name)
    {
        // A smooth gray ramp dithered to black and white: the proportion of white pixels tracks the original
        // brightness, so the mean drifts by well under one 8-bit level per channel.
        using Image<Rgba32> image = GrayRamp(160, 160);
        Color[] palette = { Color.Black, Color.White };

        using Image<Rgba32> result = image.Clone(ctx => ctx.Dither(Lookup(name), 1f, palette));

        (double r, double g, double b) = MeanDrift(image, result);
        Assert.True(Math.Abs(r) < 1.0, $"{name}: red drifted by {r:F3}.");
        Assert.True(Math.Abs(g) < 1.0, $"{name}: green drifted by {g:F3}.");
        Assert.True(Math.Abs(b) < 1.0, $"{name}: blue drifted by {b:F3}.");
    }

    [Theory]
    [MemberData(nameof(ErrorDitherNames))]
    public void ErrorDiffusionKeepsTheMeanOfAColourGradient(string name)
    {
        using Image<Rgba32> image = Gradient(128, 128);
        var quantizer = new WuQuantizer(new QuantizerOptions { MaxColors = 8, Dither = Lookup(name) });

        using Image<Rgba32> result = image.Clone(ctx => ctx.Quantize(quantizer));

        (double r, double g, double b) = MeanDrift(image, result);
        Assert.True(Math.Abs(r) < 4.0, $"{name}: red drifted by {r:F3}.");
        Assert.True(Math.Abs(g) < 4.0, $"{name}: green drifted by {g:F3}.");
        Assert.True(Math.Abs(b) < 4.0, $"{name}: blue drifted by {b:F3}.");
    }

    [Theory]
    [MemberData(nameof(ErrorDitherNames))]
    public void ErrorDiffusionBeatsPlainThresholdingOnARamp(string name)
    {
        using Image<Rgba32> image = GrayRamp(128, 128);
        Color[] palette = { Color.Black, Color.White };

        using Image<Rgba32> dithered = image.Clone(ctx => ctx.Dither(Lookup(name), 1f, palette));
        using Image<Rgba32> undithered = image.Clone(ctx => ctx.Quantize(
            new PaletteQuantizer(palette, new QuantizerOptions { Dither = null })));

        Assert.True(
            BlockMeanError(image, dithered) < BlockMeanError(image, undithered),
            $"{name} should reproduce local brightness better than hard thresholding.");
    }

    [Fact]
    public void ADitherScaleOfZeroLeavesTheNearestColourUnchanged()
    {
        using Image<Rgba32> image = GrayRamp(32, 32);
        Color[] palette = { Color.Black, Color.White };

        using Image<Rgba32> unscaled = image.Clone(ctx => ctx.Dither(KnownDitherings.FloydSteinberg, 0f, palette));
        using Image<Rgba32> undithered = image.Clone(ctx => ctx.Quantize(
            new PaletteQuantizer(palette, new QuantizerOptions { Dither = null })));

        AssertPixelsEqual(unscaled, undithered);
    }

    [Fact]
    public void SerpentineScanningChangesTheResultButStaysDeterministic()
    {
        var forward = (ErrorDither)KnownDitherings.FloydSteinberg;
        ErrorDither serpentine = forward.WithSerpentine(true);
        Assert.True(serpentine.Serpentine);
        Assert.False(forward.Serpentine);

        using Image<Rgba32> image = GrayRamp(64, 64);
        Color[] palette = { Color.Black, Color.White };

        using Image<Rgba32> a = image.Clone(ctx => ctx.Dither(serpentine, 1f, palette));
        using Image<Rgba32> b = image.Clone(ctx => ctx.Dither(serpentine, 1f, palette));
        using Image<Rgba32> straight = image.Clone(ctx => ctx.Dither(forward, 1f, palette));

        AssertPixelsEqual(a, b);
        Assert.NotEqual(0, CountDifferences(a, straight));
        (double r, double _, double _) = MeanDrift(image, a);
        Assert.True(Math.Abs(r) < 1.0, $"Serpentine drifted by {r:F3}.");
    }

    // ----- Ordered dithers are position periodic -----

    [Theory]
    [InlineData("Bayer2x2", 2)]
    [InlineData("Bayer4x4", 4)]
    [InlineData("Bayer8x8", 8)]
    [InlineData("Bayer16x16", 16)]
    [InlineData("Ordered3x3", 3)]
    public void OrderedDithersRepeatWithTheMatrixPeriod(string name, int period)
    {
        // On a flat colour every variation comes from the threshold matrix alone, so the output tiles with it.
        int size = period * 4;
        using var image = new Image<Rgba32>(size, size, new Rgba32(120, 120, 120));
        Color[] palette = { Color.Black, Color.White };

        image.Mutate(ctx => ctx.Dither(Lookup(name), 1f, palette));

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Assert.Equal(image[x % period, y % period], image[x, y]);
            }
        }
    }

    [Theory]
    [MemberData(nameof(OrderedDitherNames))]
    public void OrderedDithersProduceBothColoursOnAMidGray(string name)
    {
        using var image = new Image<Rgba32>(32, 32, new Rgba32(128, 128, 128));
        Color[] palette = { Color.Black, Color.White };

        image.Mutate(ctx => ctx.Dither(Lookup(name), 1f, palette));

        Assert.Equal(2, DistinctColorCount(image));
    }

    [Fact]
    public void TheBayerMatrixIsTheClassicRecursivePattern()
    {
        // The 2x2 Bayer matrix is [[0, 2], [3, 1]], so a flat mid gray lights the pixels in that order.
        using var image = new Image<Rgba32>(2, 2, new Rgba32(128, 128, 128));
        Color[] palette = { Color.Black, Color.White };

        image.Mutate(ctx => ctx.Dither(KnownDitherings.Bayer2x2, 1f, palette));

        Assert.Equal(new Rgba32(0, 0, 0), image[0, 0]);
        Assert.Equal(new Rgba32(255, 255, 255), image[1, 0]);
        Assert.Equal(new Rgba32(255, 255, 255), image[0, 1]);
        Assert.Equal(new Rgba32(0, 0, 0), image[1, 1]);
    }

    [Fact]
    public void OrderedDithersAreAnchoredToTheFrameOrigin()
    {
        Color[] palette = { Color.Black, Color.White };
        using var full = new Image<Rgba32>(8, 8, new Rgba32(120, 120, 120));
        full.Mutate(ctx => ctx.Dither(KnownDitherings.Bayer4x4, 1f, palette));

        using var half = new Image<Rgba32>(4, 4, new Rgba32(120, 120, 120));
        half.Mutate(ctx => ctx.Dither(KnownDitherings.Bayer4x4, 1f, palette));

        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                Assert.Equal(full[x, y], half[x, y]);
            }
        }
    }

    [Fact]
    public void CreateBayerRejectsSizesThatAreNotPowersOfTwo()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OrderedDither.CreateBayer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => OrderedDither.CreateBayer(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => OrderedDither.CreateBayer(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => OrderedDither.CreateBayer(6));
        Assert.NotNull(OrderedDither.CreateBayer(2));
        Assert.NotNull(OrderedDither.CreateBayer(32));
    }

    [Fact]
    public void OrderedDitherValidatesItsThresholdMatrix()
    {
        Assert.Throws<ArgumentNullException>(() => new OrderedDither(null!));
        Assert.Throws<ArgumentException>(() => new OrderedDither(new int[0, 0]));
        Assert.Throws<ArgumentException>(() => new OrderedDither(new[,] { { 0, 9 } }));
        Assert.Throws<ArgumentException>(() => new OrderedDither(new[,] { { 0, -1 } }));
        Assert.NotNull(new OrderedDither(new[,] { { 0, 1 } }));
    }

    // ----- ErrorDither construction -----

    [Fact]
    public void ErrorDitherValidatesItsKernel()
    {
        Assert.Throws<ArgumentNullException>(() => new ErrorDither(null!, 0, 1));
        Assert.Throws<ArgumentException>(() => new ErrorDither(new int[0, 0], 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ErrorDither(new[,] { { 0, 1 } }, 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ErrorDither(new[,] { { 0, 1 } }, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ErrorDither(new[,] { { 0, 1 } }, 0, 0));
        Assert.Throws<ArgumentException>(() => new ErrorDither(new[,] { { 0, -1 } }, 0, 1));

        // Weights left of (or on) the origin in row 0 would target pixels that are already final.
        Assert.Throws<ArgumentException>(() => new ErrorDither(new[,] { { 1, 0 } }, 1, 1));
        Assert.Throws<ArgumentException>(() => new ErrorDither(new[,] { { 0, 1 } }, 1, 1));
    }

    [Fact]
    public void ACustomKernelDiffusesTheWholeErrorToTheRight()
    {
        // Everything goes to the next pixel, so a mid gray alternates strictly between the two palette entries.
        var dither = new ErrorDither(new[,] { { 0, 1 } }, originColumn: 0, divisor: 1);
        using var image = new Image<Rgba32>(6, 1, new Rgba32(128, 128, 128));
        Color[] palette = { Color.Black, Color.White };

        image.Mutate(ctx => ctx.Dither(dither, 1f, palette));

        for (int x = 0; x < 6; x++)
        {
            Assert.Equal(x % 2 == 0 ? new Rgba32(255, 255, 255) : new Rgba32(0, 0, 0), image[x, 0]);
        }
    }

    // ----- Transparency -----

    [Fact]
    public void TransparentPixelsStayTransparentThroughDithering()
    {
        using var image = new Image<Rgba32>(8, 8);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                image[x, y] = x < 4 ? new Rgba32(200, 30, 30) : default;
            }
        }

        var quantizer = new WuQuantizer(new QuantizerOptions { MaxColors = 4 });
        using Image<Rgba32> result = image.Clone(ctx => ctx.Quantize(quantizer));

        for (int y = 0; y < 8; y++)
        {
            for (int x = 4; x < 8; x++)
            {
                Assert.Equal(0, result[x, y].A);
            }
        }
    }

    // ----- BinaryDither -----

    [Fact]
    public void BinaryDitherResolvesToTheTwoRequestedColours()
    {
        using Image<Rgba32> image = GrayRamp(32, 32);

        image.Mutate(ctx => ctx.BinaryDither(KnownDitherings.FloydSteinberg, Color.Red, Color.Blue));

        var allowed = new HashSet<Rgba32> { new(255, 0, 0), new(0, 0, 255) };
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Assert.Contains(image[x, y], allowed);
            }
        }
    }

    [Fact]
    public void BinaryDitherDefaultsToWhiteAndBlack()
    {
        using Image<Rgba32> image = GrayRamp(24, 24);

        using Image<Rgba32> viaDefault = image.Clone(ctx => ctx.BinaryDither(KnownDitherings.Bayer4x4));
        using Image<Rgba32> explicitColors = image.Clone(
            ctx => ctx.BinaryDither(KnownDitherings.Bayer4x4, Color.White, Color.Black));

        AssertPixelsEqual(viaDefault, explicitColors);
    }

    [Fact]
    public void BinaryDitherSplitsByLuminance()
    {
        using var image = new Image<Rgba32>(2, 1);
        image[0, 0] = new Rgba32(0, 0, 0);
        image[1, 0] = new Rgba32(255, 255, 255);

        image.Mutate(ctx => ctx.BinaryDither(KnownDitherings.FloydSteinberg, Color.White, Color.Black));

        Assert.Equal(new Rgba32(0, 0, 0), image[0, 0]);
        Assert.Equal(new Rgba32(255, 255, 255), image[1, 0]);
    }

    // ----- Argument validation on the operations -----

    [Fact]
    public void TheDitherOperationsValidateTheirArguments()
    {
        using var image = new Image<Rgba32>(4, 4);
        Color[] palette = { Color.Black, Color.White };

        Assert.Throws<ArgumentNullException>(() => image.Mutate(ctx => ctx.Dither(null!, 1f, palette)));
        Assert.Throws<ArgumentNullException>(() => image.Mutate(ctx => ctx.BinaryDither(null!, Color.White, Color.Black)));
        Assert.Throws<ArgumentException>(() => image.Mutate(ctx => ctx.Dither(KnownDitherings.Bayer2x2, 1f, Array.Empty<Color>())));
        Assert.Throws<ArgumentException>(() => image.Mutate(ctx => ctx.Dither(KnownDitherings.Bayer2x2, 1f, new Color[257])));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(ctx => ctx.Dither(KnownDitherings.Bayer2x2, -0.5f, palette)));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(ctx => ctx.Dither(KnownDitherings.Bayer2x2, 1.5f, palette)));
    }

    [Fact]
    public void TheDefaultDitherOverloadsUseTheWebSafePalette()
    {
        using Image<Rgba32> image = Gradient(24, 24);

        using Image<Rgba32> viaDefault = image.Clone(ctx => ctx.Dither(KnownDitherings.Bayer4x4));
        using Image<Rgba32> viaScale = image.Clone(ctx => ctx.Dither(KnownDitherings.Bayer4x4, 1f));
        using Image<Rgba32> viaPalette = image.Clone(
            ctx => ctx.Dither(KnownDitherings.Bayer4x4, 1f, WebSafePaletteQuantizer.Palette));

        AssertPixelsEqual(viaDefault, viaScale);
        AssertPixelsEqual(viaDefault, viaPalette);
    }

    [Fact]
    public void DitheringAppliesToEveryFrame()
    {
        using var image = new Image<Rgba32>(8, 8, new Rgba32(120, 120, 120));
        ImageFrame<Rgba32> second = image.Frames.CreateFrame(8, 8);
        for (int y = 0; y < 8; y++)
        {
            second.GetRowSpan(y).Fill(new Rgba32(200, 200, 200));
        }

        image.Mutate(ctx => ctx.Dither(KnownDitherings.Bayer4x4, 1f, new[] { Color.Black, Color.White }));

        var allowed = new HashSet<Rgba32> { new(0, 0, 0), new(255, 255, 255) };
        Assert.Contains(image.Frames[0][0, 0], allowed);
        Assert.Contains(image.Frames[1][0, 0], allowed);
    }

    // ----- Helpers -----

    private static IDither Lookup(string name)
        => (IDither)typeof(KnownDitherings).GetProperty(name)!.GetValue(null)!;

    private static Image<Rgba32> Gradient(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32(
                    (byte)((x * 255) / Math.Max(1, width - 1)),
                    (byte)((y * 255) / Math.Max(1, height - 1)),
                    (byte)(((x + y) * 255) / Math.Max(1, width + height - 2)));
            }
        }

        return image;
    }

    /// <summary>A horizontal gray ramp; every row is identical.</summary>
    private static Image<Rgba32> GrayRamp(int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte v = (byte)((x * 255) / Math.Max(1, width - 1));
                image[x, y] = new Rgba32(v, v, v);
            }
        }

        return image;
    }

    private static (double Red, double Green, double Blue) MeanDrift(Image<Rgba32> source, Image<Rgba32> result)
    {
        long r = 0, g = 0, b = 0;
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Rgba32 a = source[x, y];
                Rgba32 c = result[x, y];
                r += c.R - a.R;
                g += c.G - a.G;
                b += c.B - a.B;
            }
        }

        double pixels = source.Width * (double)source.Height;
        return (r / pixels, g / pixels, b / pixels);
    }

    /// <summary>Mean absolute difference between 8x8 block averages: how well local brightness is preserved.</summary>
    private static double BlockMeanError(Image<Rgba32> source, Image<Rgba32> result)
    {
        const int Block = 8;
        double total = 0;
        int blocks = 0;
        for (int by = 0; by + Block <= source.Height; by += Block)
        {
            for (int bx = 0; bx + Block <= source.Width; bx += Block)
            {
                long a = 0, c = 0;
                for (int y = by; y < by + Block; y++)
                {
                    for (int x = bx; x < bx + Block; x++)
                    {
                        a += source[x, y].R;
                        c += result[x, y].R;
                    }
                }

                total += Math.Abs((a - c) / (double)(Block * Block));
                blocks++;
            }
        }

        return total / Math.Max(1, blocks);
    }

    private static int CountDifferences(Image<Rgba32> a, Image<Rgba32> b)
    {
        int count = 0;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                if (a[x, y] != b[x, y])
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int DistinctColorCount(Image<Rgba32> image)
    {
        var seen = new HashSet<Rgba32>();
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                seen.Add(image[x, y]);
            }
        }

        return seen.Count;
    }

    private static void AssertPixelsEqual(Image<Rgba32> expected, Image<Rgba32> actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                if (expected[x, y] != actual[x, y])
                {
                    Assert.Fail($"Pixel ({x}, {y}): expected {expected[x, y]}, got {actual[x, y]}.");
                }
            }
        }
    }
}
