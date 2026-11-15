using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>Dominant colours against images whose colour populations are known by construction.</summary>
public class DominantColorTests
{
    /// <summary>
    /// 50% dark red, 30% blue and 20% near-white in horizontal bands, each with a deterministic +-3 jitter so the
    /// clusters are not single histogram bins.
    /// </summary>
    private static Image<Rgba32> Bands(int width = 200, int height = 100)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            (int r, int g, int b) = y < height / 2 ? (200, 30, 30) : y < height * 8 / 10 ? (20, 40, 180) : (240, 240, 235);
            for (int x = 0; x < width; x++)
            {
                int jitter = ((x * 7) + (y * 13)) % 7 - 3;
                image[x, y] = new Rgba32((byte)(r + jitter), (byte)(g + jitter), (byte)(b + jitter), 255);
            }
        }

        return image;
    }

    [Fact]
    public void FindsTheConstructedPopulations()
    {
        using Image<Rgba32> image = Bands();
        IReadOnlyList<DominantColor> colors = image.GetDominantColors(3);
        Assert.Equal(3, colors.Count);
        AssertColor(colors[0], 200, 30, 30, 0.5f);
        AssertColor(colors[1], 20, 40, 180, 0.3f);
        AssertColor(colors[2], 240, 240, 235, 0.2f);
        Assert.Equal(1f, colors.Sum(c => c.Fraction), 4);
    }

    [Fact]
    public void IgnoresNearTransparentPixels()
    {
        using Image<Rgba32> image = Bands();
        for (int y = 0; y < 50; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Rgba32 p = image[x, y];
                image[x, y] = new Rgba32(p.R, p.G, p.B, 10); // the red half becomes near-transparent
            }
        }

        IReadOnlyList<DominantColor> colors = image.GetDominantColors(2);
        Assert.Equal(2, colors.Count);
        AssertColor(colors[0], 20, 40, 180, 0.6f);
        AssertColor(colors[1], 240, 240, 235, 0.4f);

        IReadOnlyList<DominantColor> withRed = image.GetDominantColors(3, new DominantColorOptions { MinimumAlpha = 0 });
        Assert.Equal(0.5f, withRed[0].Fraction, 2);

        using var transparent = new Image<Rgba32>(16, 16);
        Assert.Empty(transparent.GetDominantColors(4));
    }

    [Fact]
    public void ReturnsFewerColoursThanRequestedForSimpleImages()
    {
        using var image = new Image<Rgb24>(40, 40, new Rgb24(10, 120, 200));
        IReadOnlyList<DominantColor> colors = image.GetDominantColors(8);
        DominantColor only = Assert.Single(colors);
        Assert.Equal(1f, only.Fraction);
        Assert.Equal(new Color(10, 120, 200), only.Color);
    }

    [Fact]
    public void IsDeterministicAndIndependentOfParallelism()
    {
        using Image<Rgba32> image = EffectsTests.Synthetic(256, 192);
        IReadOnlyList<DominantColor> first = image.GetDominantColors(6);
        using var serial = OperationExecution.Enter(new ImageExecutionOptions { Configuration = new Configuration { MaxDegreeOfParallelism = 1 } });
        IReadOnlyList<DominantColor> second = image.GetDominantColors(6);
        Assert.Equal(first.Select(c => (c.Color, c.Fraction)), second.Select(c => (c.Color, c.Fraction)));
        Assert.True(first.Zip(first.Skip(1)).All(p => p.First.Fraction >= p.Second.Fraction));
    }

    [Fact]
    public void SamplingKeepsTheSharesOfLargeImages()
    {
        using Image<Rgba32> image = Bands(1600, 800);
        IReadOnlyList<DominantColor> sampled = image.GetDominantColors(3);
        IReadOnlyList<DominantColor> full = image.GetDominantColors(3, new DominantColorOptions { MaxSampleDimension = 0 });
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(full[i].Fraction, sampled[i].Fraction, 2);
        }
    }

    [Fact]
    public void ValidatesArguments()
    {
        using var image = new Image<Rgba32>(4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => image.GetDominantColors(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.GetDominantColors(257));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DominantColorOptions { MaxSampleDimension = 8 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DominantColorOptions { MaxIterations = 0 });
        Assert.Throws<ArgumentNullException>(() => ProcessingExtensions.GetDominantColors<Rgba32>(null!, 3));
        Assert.Equal("DominantColor [ #0A78C8, 50.0% ]", new DominantColor(new Color(10, 120, 200), 0.5f).ToString());
    }

    private static void AssertColor(DominantColor actual, int r, int g, int b, float fraction)
    {
        Assert.InRange(actual.Color.R, r - 3, r + 3);
        Assert.InRange(actual.Color.G, g - 3, g + 3);
        Assert.InRange(actual.Color.B, b - 3, b + 3);
        Assert.Equal(fraction, actual.Fraction, 2);
    }
}
