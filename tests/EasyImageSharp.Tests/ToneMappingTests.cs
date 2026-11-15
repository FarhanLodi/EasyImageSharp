using System.Runtime.InteropServices;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Tone mapping: every expected value is evaluated here in double precision straight from the published curve
/// definitions (Reinhard 2002, Narkowicz 2015, Hable 2010) and the IEC 61966-2-1 sRGB transfer function.
/// </summary>
public class ToneMappingTests
{
    private const double Tolerance = 2e-6;

    [Fact]
    public void Clip_AppliesExposureThenClipsAtOne()
    {
        RgbaVector result = Map(new RgbaVector(0.25f, 0.5f, 2f, 0.3f), new ToneMappingOptions { ToneMapper = ToneMapper.Clip, Exposure = 1f });
        AssertPixel(result, Srgb(0.5), Srgb(1), Srgb(1), 0.3);
    }

    [Fact]
    public void Reinhard_MapsLuminanceAndKeepsTheColourRatio()
    {
        AssertPixel(Map(new RgbaVector(1f, 1f, 1f), ToneMapper.Reinhard), Srgb(0.5), Srgb(0.5), Srgb(0.5), 1);

        // A saturated colour keeps its ratios; red alone is scaled by Ld / L and then clipped at 1.
        double l = 0.2126 * 0.5 + 0.7152 * 1.0 + 0.0722 * 0.25;
        double factor = (l / (1 + l)) / l;
        AssertPixel(Map(new RgbaVector(0.5f, 1f, 0.25f), ToneMapper.Reinhard),
            Srgb(0.5 * factor), Srgb(1.0 * factor), Srgb(0.25 * factor), 1);
    }

    [Fact]
    public void ReinhardExtended_MapsTheWhitePointToWhite()
    {
        var options = new ToneMappingOptions { ToneMapper = ToneMapper.ReinhardExtended, WhitePoint = 4f };
        AssertPixel(Map(new RgbaVector(4f, 4f, 4f), options), 1, 1, 1, 1);
        double ld = 1 * (1 + (1 / 16.0)) / 2;
        AssertPixel(Map(new RgbaVector(1f, 1f, 1f), options), Srgb(ld), Srgb(ld), Srgb(ld), 1);
    }

    [Fact]
    public void ReinhardExtended_DefaultsToTheBrightestPixelOfEachFrame()
    {
        using var image = new Image<RgbaVector>(3, 1);
        image[0, 0] = new RgbaVector(0.5f, 0.5f, 0.5f);
        image[1, 0] = new RgbaVector(2f, 2f, 2f);
        image[2, 0] = new RgbaVector(8f, 8f, 8f);
        image.Mutate(c => c.ToneMap(new ToneMappingOptions { ToneMapper = ToneMapper.ReinhardExtended, Exposure = -1f }));

        // After one stop down the brightest luminance is 4, which becomes the white point.
        AssertPixel(image[2, 0], 1, 1, 1, 1);
        double l = 1.0;
        double ld = l * (1 + (l / 16)) / (1 + l);
        AssertPixel(image[1, 0], Srgb(ld), Srgb(ld), Srgb(ld), 1);
    }

    [Fact]
    public void AcesFilmic_FollowsTheNarkowiczFit()
    {
        foreach (float x in new[] { 0f, 0.01f, 0.18f, 1f, 4f, 16f })
        {
            double expected = Srgb(Math.Clamp(x * (2.51 * x + 0.03) / (x * (2.43 * x + 0.59) + 0.14), 0, 1));
            AssertPixel(Map(new RgbaVector(x, x, x), ToneMapper.AcesFilmic), expected, expected, expected, 1);
        }
    }

    [Fact]
    public void Hable_IsNormalisedToItsWhitePoint()
    {
        static double F(double x) => ((x * (0.15 * x + 0.05) + 0.004) / (x * (0.15 * x + 0.5) + 0.06)) - (0.02 / 0.3);
        AssertPixel(Map(new RgbaVector(11.2f, 11.2f, 11.2f), ToneMapper.Hable), 1, 1, 1, 1);
        double one = Srgb(F(1) / F(11.2));
        AssertPixel(Map(new RgbaVector(1f, 1f, 1f), ToneMapper.Hable), one, one, one, 1);

        var custom = new ToneMappingOptions { ToneMapper = ToneMapper.Hable, WhitePoint = 2f, Exposure = 1f };
        double two = Srgb(F(2 * 0.5) / F(2));
        AssertPixel(Map(new RgbaVector(0.5f, 0.5f, 0.5f), custom), two, two, two, 1);
    }

    [Theory]
    [InlineData(ToneMapper.Clip)]
    [InlineData(ToneMapper.Reinhard)]
    [InlineData(ToneMapper.ReinhardExtended)]
    [InlineData(ToneMapper.AcesFilmic)]
    [InlineData(ToneMapper.Hable)]
    public void NonFiniteAndNegativeValues_MapIntoRange(ToneMapper mapper)
    {
        var options = new ToneMappingOptions { ToneMapper = mapper, WhitePoint = mapper == ToneMapper.ReinhardExtended ? 4f : null };
        RgbaVector result = Map(new RgbaVector(float.NaN, -3f, float.PositiveInfinity, 1f), options);
        Assert.Equal(0f, result.R);
        Assert.Equal(0f, result.G);
        Assert.InRange(result.B, mapper is ToneMapper.Reinhard ? 0f : 0.99f, 1f);

        RgbaVector bright = Map(new RgbaVector(1e30f, 1e30f, 1e30f), new ToneMappingOptions { ToneMapper = mapper, Exposure = 120f });
        Assert.True(float.IsFinite(bright.R) && bright.R is >= 0f and <= 1f, bright.ToString());
    }

    [Fact]
    public void IntegerPixels_AreLinearisedFirst()
    {
        using var image = new Image<Rgba32>(256, 1);
        for (int x = 0; x < 256; x++)
        {
            image[x, 0] = new Rgba32((byte)x, (byte)(255 - x), (byte)(x / 2), (byte)x);
        }

        // Clip at exposure 0 decodes and re-encodes sRGB: an identity on 8-bit data, alpha untouched.
        using Image<Rgba32> clipped = image.Clone(c => c.ToneMap(ToneMapper.Clip));
        Assert.True(image.Frames.RootFrame.PixelSpan.SequenceEqual(clipped.Frames.RootFrame.PixelSpan));

        using Image<Rgba32> reinhard = image.Clone(c => c.ToneMap(ToneMapper.Reinhard));
        for (int x = 0; x < 256; x++)
        {
            Rgba32 p = image[x, 0];
            double r = Linear(p.R / 255.0), g = Linear(p.G / 255.0), b = Linear(p.B / 255.0);
            double l = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
            double f = l > 0 ? (l / (1 + l)) / l : 0;
            Rgba32 q = reinhard[x, 0];
            Assert.True(Math.Abs(q.R - (Srgb(r * f) * 255)) <= 0.51 && Math.Abs(q.G - (Srgb(g * f) * 255)) <= 0.51
                && Math.Abs(q.B - (Srgb(b * f) * 255)) <= 0.51, $"x={x}: {q}");
            Assert.Equal(p.A, q.A);
        }
    }

    [Fact]
    public void Result_IsIndependentOfParallelism()
    {
        using Image<RgbaVector> source = HdrTests.HdrImage(257, 131, seed: 9);
        var single = new ImageExecutionOptions { Configuration = new Configuration { MaxDegreeOfParallelism = 1 } };
        var multi = new ImageExecutionOptions { Configuration = new Configuration { MaxDegreeOfParallelism = 8, MinimumPixelsPerTask = 64 } };
        foreach (ToneMapper mapper in Enum.GetValues<ToneMapper>())
        {
            using Image<RgbaVector> a = source.Clone(single, c => c.ToneMap(mapper, 1.5f));
            using Image<RgbaVector> b = source.Clone(multi, c => c.ToneMap(mapper, 1.5f));
            Assert.True(MemoryMarshal.AsBytes(a.Frames.RootFrame.PixelSpan).SequenceEqual(MemoryMarshal.AsBytes(b.Frames.RootFrame.PixelSpan)), mapper.ToString());
        }
    }

    [Fact]
    public void EveryFrame_IsMapped()
    {
        using var image = new Image<RgbaVector>(2, 2, new RgbaVector(1f, 1f, 1f));
        image.Frames.AddFrame(image.Frames.RootFrame);
        image.Mutate(c => c.ToneMap(ToneMapper.Reinhard));
        foreach (ImageFrame<RgbaVector> frame in image.Frames)
        {
            AssertPixel(frame[0, 0], Srgb(0.5), Srgb(0.5), Srgb(0.5), 1);
        }
    }

    [Fact]
    public void DecodedOpenExr_ToneMapsToDisplayRange()
    {
        byte[] bytes = HdrExrFixtures.Bytes("exr", HdrExrFixtures.Entry("exr", "oei_all_half_values"));
        using Image<RgbaVector> image = Image.Load<RgbaVector>(bytes);
        image.Mutate(c => c.ToneMap());
        foreach (RgbaVector p in image.Frames.RootFrame.PixelSpan)
        {
            Assert.True(p.R is >= 0f and <= 1f && p.G is >= 0f and <= 1f && p.B is >= 0f and <= 1f, p.ToString());
        }
    }

    [Fact]
    public void InvalidOptions_Throw()
    {
        using var image = new Image<RgbaVector>(1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(c => c.ToneMap(new ToneMappingOptions { Exposure = float.NaN })));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(c => c.ToneMap(new ToneMappingOptions { WhitePoint = 0f })));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(c => c.ToneMap(new ToneMappingOptions { WhitePoint = float.PositiveInfinity })));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(c => c.ToneMap(new ToneMappingOptions { ToneMapper = (ToneMapper)99 })));
        Assert.Throws<ArgumentNullException>(() => image.Mutate(c => c.ToneMap((ToneMappingOptions)null!)));
    }

    private static RgbaVector Map(RgbaVector pixel, ToneMapper mapper) => Map(pixel, new ToneMappingOptions { ToneMapper = mapper });

    private static RgbaVector Map(RgbaVector pixel, ToneMappingOptions options)
    {
        using var image = new Image<RgbaVector>(1, 1, pixel);
        image.Mutate(c => c.ToneMap(options));
        return image[0, 0];
    }

    private static double Srgb(double linear)
    {
        double v = Math.Clamp(linear, 0, 1);
        return v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055;
    }

    private static double Linear(double encoded)
        => encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);

    private static void AssertPixel(RgbaVector actual, double r, double g, double b, double a)
    {
        Assert.True(Math.Abs(actual.R - r) <= Tolerance && Math.Abs(actual.G - g) <= Tolerance && Math.Abs(actual.B - b) <= Tolerance
            && Math.Abs(actual.A - a) <= Tolerance, $"{actual} vs ({r}, {g}, {b}, {a})");
    }
}
