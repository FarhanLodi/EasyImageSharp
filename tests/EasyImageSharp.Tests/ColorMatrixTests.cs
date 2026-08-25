using System.Numerics;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

public class ColorMatrixTests
{
    private static Rgba32 Apply(ColorMatrix matrix, Rgba32 pixel)
    {
        using var image = new Image<Rgba32>(1, 1, pixel);
        image.Mutate(c => c.Filter(matrix));
        return image[0, 0];
    }

    private static void AssertClose(Rgba32 expected, Rgba32 actual, int tolerance = 1)
    {
        Assert.InRange(actual.R, expected.R - tolerance, expected.R + tolerance);
        Assert.InRange(actual.G, expected.G - tolerance, expected.G + tolerance);
        Assert.InRange(actual.B, expected.B - tolerance, expected.B + tolerance);
        Assert.InRange(actual.A, expected.A - tolerance, expected.A + tolerance);
    }

    // ----- Struct algebra -----

    [Fact]
    public void Identity_TransformsToSameColor_AndIsIdentity()
    {
        Assert.True(ColorMatrix.Identity.IsIdentity);
        var color = new Vector4(0.2f, 0.4f, 0.6f, 0.8f);
        Assert.Equal(color, ColorMatrix.Identity.Transform(color));
        Assert.False(KnownFilterMatrices.CreateSepiaFilter(1f).IsIdentity);
    }

    [Fact]
    public void Multiply_AppliesLeftThenRight_AndConcatMatchesOperator()
    {
        ColorMatrix brightness = KnownFilterMatrices.CreateBrightnessFilter(2f);
        ColorMatrix lightness = KnownFilterMatrices.CreateLightnessFilter(1.1f); // +0.1 offset
        var color = new Vector4(0.2f, 0.3f, 0.4f, 1f);

        // (color * brightness) * lightness == color * (brightness * lightness).
        Vector4 sequential = lightness.Transform(brightness.Transform(color));
        Vector4 combined = (brightness * lightness).Transform(color);
        Assert.Equal(sequential.X, combined.X, 5);
        Assert.Equal(sequential.Y, combined.Y, 5);
        Assert.Equal(sequential.Z, combined.Z, 5);
        Assert.Equal(sequential.W, combined.W, 5);
        Assert.Equal(brightness * lightness, brightness.Concat(lightness));
        Assert.Equal(brightness * lightness, ColorMatrix.Multiply(brightness, lightness));

        // Order matters when offsets are involved: (x * 2) + 0.1 != (x + 0.1) * 2.
        Assert.NotEqual(brightness * lightness, lightness * brightness);
        Assert.Equal(0.5f, (brightness * lightness).Transform(color).X, 5);
        Assert.Equal(0.6f, (lightness * brightness).Transform(color).X, 5);
    }

    [Fact]
    public void Equality_HashCode_AndArithmetic()
    {
        ColorMatrix a = KnownFilterMatrices.CreateSepiaFilter(0.5f);
        ColorMatrix b = KnownFilterMatrices.CreateSepiaFilter(0.5f);
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, KnownFilterMatrices.CreateSepiaFilter(0.4f));
        Assert.Equal(ColorMatrix.Identity, (a + b) - a - b + ColorMatrix.Identity);
        Assert.Equal(a + a, a * 2f);
        Assert.Contains("M11", a.ToString());
    }

    [Fact]
    public void Transform_MatchesRowVectorConvention()
    {
        // R' = R*M11 + G*M21 + B*M31 + A*M41 + M51.
        var m = new ColorMatrix(
            1, 2, 3, 4,
            5, 6, 7, 8,
            9, 10, 11, 12,
            13, 14, 15, 16,
            17, 18, 19, 20);
        Vector4 result = m.Transform(new Vector4(1, 0, 0, 0));
        Assert.Equal(new Vector4(1 + 17, 2 + 18, 3 + 19, 4 + 20), result);
        result = m.Transform(new Vector4(0, 0, 1, 0));
        Assert.Equal(new Vector4(9 + 17, 10 + 18, 11 + 19, 12 + 20), result);
    }

    // ----- Known filter behaviour on reference colours -----

    [Fact]
    public void Grayscale_Bt709_ProducesEqualChannelsWithLumaWeights()
    {
        Rgba32 red = Apply(KnownFilterMatrices.CreateGrayscaleBt709Filter(1f), new Rgba32(255, 0, 0, 200));
        Assert.Equal(red.R, red.G);
        Assert.Equal(red.G, red.B);
        Assert.InRange(red.R, 53, 55); // 0.2126 * 255 = 54.2
        Assert.Equal(200, red.A);

        Rgba32 green = Apply(KnownFilterMatrices.CreateGrayscaleBt601Filter(1f), new Rgba32(0, 255, 0, 255));
        Assert.InRange(green.R, 149, 150); // 0.587 * 255 = 149.7
        Assert.Equal(green.R, green.B);
    }

    [Fact]
    public void Grayscale_AmountZero_IsIdentity_AndHalfIsBetween()
    {
        var pixel = new Rgba32(200, 40, 90, 255);
        Assert.Equal(pixel, Apply(KnownFilterMatrices.CreateGrayscaleBt709Filter(0f), pixel));
        Assert.Equal(pixel, Apply(KnownFilterMatrices.CreateSepiaFilter(0f), pixel));
        Rgba32 full = Apply(KnownFilterMatrices.CreateGrayscaleBt709Filter(1f), pixel);
        Rgba32 half = Apply(KnownFilterMatrices.CreateGrayscaleBt709Filter(0.5f), pixel);
        Assert.InRange(half.R, Math.Min(pixel.R, full.R), Math.Max(pixel.R, full.R));
        Assert.InRange(half.G, Math.Min(pixel.G, full.G), Math.Max(pixel.G, full.G));
    }

    [Fact]
    public void Sepia_OfWhite_IsWarmAndClamped()
    {
        Rgba32 sepia = Apply(KnownFilterMatrices.CreateSepiaFilter(1f), new Rgba32(255, 255, 255, 255));
        // 0.393+0.769+0.189 = 1.351 -> clamps to 255; G = 1.203 -> 255; B = 0.937 * 255 = 239.
        Assert.Equal(255, sepia.R);
        Assert.Equal(255, sepia.G);
        Assert.InRange(sepia.B, 238, 240);
        Rgba32 grey = Apply(KnownFilterMatrices.CreateSepiaFilter(1f), new Rgba32(100, 100, 100, 255));
        Assert.True(grey.R > grey.G && grey.G > grey.B, grey.ToString());
    }

    [Fact]
    public void Invert_Opacity_Lightness_Brightness_Contrast_MatchDefinitions()
    {
        var pixel = new Rgba32(200, 100, 50, 255);
        Assert.Equal(new Rgba32(55, 155, 205, 255), Apply(KnownFilterMatrices.CreateInvertFilter(1f), pixel));
        Assert.Equal(new Rgba32(200, 100, 50, 128), Apply(KnownFilterMatrices.CreateOpacityFilter(0.5f), pixel));
        AssertClose(new Rgba32(226, 126, 76, 255), Apply(KnownFilterMatrices.CreateLightnessFilter(1.1f), pixel), 1);
        Assert.Equal(new Rgba32(255, 200, 100, 255), Apply(KnownFilterMatrices.CreateBrightnessFilter(2f), pixel));
        AssertClose(new Rgba32(164, 114, 89, 255), Apply(KnownFilterMatrices.CreateContrastFilter(0.5f), pixel), 1);
        Assert.Equal(new Rgba32(128, 128, 128, 255), Apply(KnownFilterMatrices.CreateContrastFilter(0f), pixel));
    }

    [Fact]
    public void Saturate_ZeroIsGrayscale_OneIsIdentity()
    {
        var pixel = new Rgba32(200, 100, 50, 255);
        Rgba32 zero = Apply(KnownFilterMatrices.CreateSaturateFilter(0f), pixel);
        Assert.Equal(zero.R, zero.G);
        Assert.Equal(zero.G, zero.B);
        Assert.Equal(pixel, Apply(KnownFilterMatrices.CreateSaturateFilter(1f), pixel));
        Rgba32 boosted = Apply(KnownFilterMatrices.CreateSaturateFilter(2f), pixel);
        Assert.True(boosted.R - boosted.B > pixel.R - pixel.B);
    }

    [Fact]
    public void Hue_ZeroAnd360AreIdentity_180InvertsHueOfPrimaries()
    {
        var pixel = new Rgba32(200, 100, 50, 255);
        AssertClose(pixel, Apply(KnownFilterMatrices.CreateHueFilter(0f), pixel));
        AssertClose(pixel, Apply(KnownFilterMatrices.CreateHueFilter(360f), pixel));
        Rgba32 red = Apply(KnownFilterMatrices.CreateHueFilter(120f), new Rgba32(255, 0, 0, 255));
        Assert.True(red.G > red.R && red.G > red.B, $"red rotated 120 degrees should be greenish: {red}");
        Rgba32 red240 = Apply(KnownFilterMatrices.CreateHueFilter(240f), new Rgba32(255, 0, 0, 255));
        Assert.True(red240.B > red240.R && red240.B > red240.G, $"red rotated 240 degrees should be bluish: {red240}");
    }

    [Fact]
    public void ColorBlindness_MatricesPreserveGrey_AndAchromatopsiaIsMonochrome()
    {
        var grey = new Rgba32(120, 120, 120, 255);
        foreach (ColorBlindnessMode mode in Enum.GetValues<ColorBlindnessMode>())
        {
            AssertClose(grey, Apply(KnownFilterMatrices.GetColorBlindnessFilter(mode), grey), 1);
        }

        Rgba32 mono = Apply(KnownFilterMatrices.AchromatopsiaFilter, new Rgba32(255, 0, 0, 255));
        Assert.Equal(mono.R, mono.G);
        Assert.Equal(mono.G, mono.B);
        Rgba32 deuter = Apply(KnownFilterMatrices.DeuteranopiaFilter, new Rgba32(0, 255, 0, 255));
        Assert.InRange(deuter.R, 95, 97);   // 0.375 * 255
        Assert.InRange(deuter.G, 76, 77);   // 0.3 * 255
        Assert.InRange(deuter.B, 76, 77);
    }

    [Fact]
    public void BlackWhite_PushesTowardsExtremes()
    {
        Assert.Equal(new Rgba32(0, 0, 0, 255), Apply(KnownFilterMatrices.BlackWhiteFilter, new Rgba32(40, 40, 40, 255)));
        Assert.Equal(new Rgba32(255, 255, 255, 255), Apply(KnownFilterMatrices.BlackWhiteFilter, new Rgba32(220, 220, 220, 255)));
        Rgba32 mid = Apply(KnownFilterMatrices.BlackWhiteFilter, new Rgba32(85, 85, 85, 255));
        Assert.Equal(mid.R, mid.G);
        Assert.InRange(mid.R, 127, 128); // 4.5 * (85/255) - 1 = 0.5
    }

    // ----- Argument validation -----

    [Fact]
    public void ParameterisedFilters_ValidateArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KnownFilterMatrices.CreateSepiaFilter(1.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => KnownFilterMatrices.CreateGrayscaleBt709Filter(-0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => KnownFilterMatrices.CreateOpacityFilter(2f));
        Assert.Throws<ArgumentOutOfRangeException>(() => KnownFilterMatrices.CreateBrightnessFilter(-1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => KnownFilterMatrices.CreateSaturateFilter(-1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => KnownFilterMatrices.CreateLightnessFilter(-1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => KnownFilterMatrices.CreateContrastFilter(-1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => KnownFilterMatrices.CreateInvertFilter(2f));
        Assert.Throws<ArgumentOutOfRangeException>(() => KnownFilterMatrices.GetColorBlindnessFilter((ColorBlindnessMode)99));
    }

    // ----- Context integration -----

    [Fact]
    public void ContextExtensions_ProduceSameResultAsFilterWithMatrix()
    {
        using Image<Rgba32> src = EffectsTests.Synthetic();
        Assert.Equal(
            EffectsTests.Checksum(src.Clone(c => c.Filter(KnownFilterMatrices.CreateSepiaFilter(0.8f)))),
            EffectsTests.Checksum(src.Clone(c => c.Sepia(0.8f))));
        Assert.Equal(
            EffectsTests.Checksum(src.Clone(c => c.Filter(KnownFilterMatrices.CreateHueFilter(45f)))),
            EffectsTests.Checksum(src.Clone(c => c.Hue(45f))));
        Assert.Equal(
            EffectsTests.Checksum(src.Clone(c => c.Filter(KnownFilterMatrices.CreateSaturateFilter(1.5f)))),
            EffectsTests.Checksum(src.Clone(c => c.Saturate(1.5f))));
        Assert.Equal(
            EffectsTests.Checksum(src.Clone(c => c.Filter(KnownFilterMatrices.CreateLightnessFilter(0.8f)))),
            EffectsTests.Checksum(src.Clone(c => c.Lightness(0.8f))));
        Assert.Equal(
            EffectsTests.Checksum(src.Clone(c => c.Filter(KnownFilterMatrices.CreateOpacityFilter(0.3f)))),
            EffectsTests.Checksum(src.Clone(c => c.Opacity(0.3f))));
        Assert.Equal(
            EffectsTests.Checksum(src.Clone(c => c.Filter(KnownFilterMatrices.BlackWhiteFilter))),
            EffectsTests.Checksum(src.Clone(c => c.BlackWhite())));
        Assert.Equal(
            EffectsTests.Checksum(src.Clone(c => c.Filter(KnownFilterMatrices.ProtanopiaFilter))),
            EffectsTests.Checksum(src.Clone(c => c.ColorBlindness(ColorBlindnessMode.Protanopia))));
        Assert.Equal(
            EffectsTests.Checksum(src.Clone(c => c.Filter(KnownFilterMatrices.CreateGrayscaleBt601Filter(1f)))),
            EffectsTests.Checksum(src.Clone(c => c.Grayscale(GrayscaleMode.Bt601))));
    }

    [Fact]
    public void GrayscaleMatrix_MatchesClassicGrayscaleWithinOne()
    {
        using Image<Rgba32> src = EffectsTests.Synthetic();
        using Image<Rgba32> classic = src.Clone(c => c.Grayscale());
        using Image<Rgba32> matrix = src.Clone(c => c.Grayscale(GrayscaleMode.Bt709));
        Assert.True(EffectsTests.MaxDifference(classic, matrix) <= 1);
    }

    [Fact]
    public void Filter_Rectangle_OnlyChangesRegion()
    {
        using Image<Rgba32> src = EffectsTests.Synthetic();
        var rect = new Rectangle(4, 4, 10, 10);
        using Image<Rgba32> full = src.Clone(c => c.Sepia());
        using Image<Rgba32> partial = src.Clone(c => c.Sepia(1f, rect));
        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                Assert.Equal(rect.Contains(x, y) ? full[x, y] : src[x, y], partial[x, y]);
            }
        }
    }

    [Fact]
    public void Filter_WorksOnEveryPixelFormat_AndOpacityIsDroppedByOpaqueFormats()
    {
        using Image<Rgba32> src = EffectsTests.Synthetic();
        using Image<Rgba32> rgbaRef = src.Clone(c => c.Kodachrome());
        using Image<Rgb24> rgb = src.CloneAs<Rgb24>().Clone(c => c.Kodachrome());
        using Image<Bgr24> bgr = src.CloneAs<Bgr24>().Clone(c => c.Kodachrome());
        using Image<Bgra32> bgra = src.CloneAs<Bgra32>().Clone(c => c.Kodachrome());
        using Image<L8> l8 = src.CloneAs<L8>().Clone(c => c.Kodachrome());
        for (int y = 0; y < src.Height; y += 3)
        {
            for (int x = 0; x < src.Width; x += 5)
            {
                Rgba32 expected = rgbaRef[x, y];
                Assert.Equal(new Rgb24(expected.R, expected.G, expected.B), rgb[x, y]);
                Assert.Equal(new Rgba32(expected.R, expected.G, expected.B, 255), bgr[x, y].ToRgba32());
                Assert.Equal(expected, bgra[x, y].ToRgba32());
            }
        }

        // Opacity on a format without alpha is a no-op.
        using Image<Rgb24> opaque = src.CloneAs<Rgb24>();
        using Image<Rgb24> faded = opaque.Clone(c => c.Opacity(0.2f));
        Assert.Equal(EffectsTests.Checksum(opaque), EffectsTests.Checksum(faded));
        Assert.Equal(src.Width, l8.Width);
    }

    [Fact]
    public void Filter_AppliesToEveryFrame()
    {
        using Image<Rgba32> src = EffectsTests.TwoFrames();
        using Image<Rgba32> result = src.Clone(c => c.Invert(new Rectangle(0, 0, 100, 100)).Sepia());
        for (int f = 0; f < 2; f++)
        {
            using var single = new Image<Rgba32>(new List<ImageFrame<Rgba32>> { src.Frames[f].Clone() });
            single.Mutate(c => c.Invert().Sepia());
            using var frame = new Image<Rgba32>(new List<ImageFrame<Rgba32>> { result.Frames[f].Clone() });
            Assert.Equal(EffectsTests.Checksum(single), EffectsTests.Checksum(frame));
        }
    }
}
