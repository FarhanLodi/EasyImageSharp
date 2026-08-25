using System.Numerics;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>Pixel blending (W3C blend modes + Porter-Duff) and DrawImage compositing.</summary>
public class CompositingTests
{
    private static readonly Rgba32 Backdrop = new(200, 100, 50, 255);
    private static readonly Rgba32 Source = new(40, 160, 220, 255);

    private static void AssertClose(Rgba32 expected, Rgba32 actual, int tolerance = 1)
    {
        Assert.InRange(actual.R, expected.R - tolerance, expected.R + tolerance);
        Assert.InRange(actual.G, expected.G - tolerance, expected.G + tolerance);
        Assert.InRange(actual.B, expected.B - tolerance, expected.B + tolerance);
        Assert.InRange(actual.A, expected.A - tolerance, expected.A + tolerance);
    }

    // ----- Independent references (Pillow fixtures) -----

    [Theory]
    [InlineData("chops_multiply.png", PixelColorBlendingMode.Multiply)]
    [InlineData("chops_screen.png", PixelColorBlendingMode.Screen)]
    [InlineData("chops_add.png", PixelColorBlendingMode.Add)]
    [InlineData("chops_subtract.png", PixelColorBlendingMode.Subtract)]
    [InlineData("chops_difference.png", PixelColorBlendingMode.Difference)]
    [InlineData("chops_darker.png", PixelColorBlendingMode.Darken)]
    [InlineData("chops_lighter.png", PixelColorBlendingMode.Lighten)]
    public void BlendModes_MatchPillowImageChopsWithinOne(string fixture, PixelColorBlendingMode mode)
    {
        using Image<Rgba32> backdrop = EffectsTests.LoadFixture("src_rgb.png");
        using Image<Rgba32> overlay = EffectsTests.LoadFixture("chops_overlay_rgb.png");
        using Image<Rgba32> expected = EffectsTests.LoadFixture(fixture);
        using Image<Rgba32> actual = backdrop.Clone(c => c.DrawImage(overlay, mode, 1f));
        int diff = EffectsTests.MaxDifference(expected, actual);
        Assert.True(diff <= 1, $"{mode}: max diff {diff}");
    }

    [Fact]
    public void SourceOver_MatchesPillowAlphaCompositeWithinOne()
    {
        using Image<Rgba32> backdrop = EffectsTests.LoadFixture("src_rgba.png");
        using Image<Rgba32> overlay = EffectsTests.LoadFixture("overlay_rgba.png");
        using Image<Rgba32> expected = EffectsTests.LoadFixture("alpha_composite_expected.png");
        using Image<Rgba32> actual = backdrop.Clone(c => c.DrawImage(overlay, new Point(8, 6), PixelColorBlendingMode.Normal, PixelAlphaCompositionMode.SrcOver, 1f));
        int diff = EffectsTests.MaxDifference(expected, actual);
        Assert.True(diff <= 1, $"max diff {diff}");
    }

    // ----- PixelBlender: blend functions -----

    [Fact]
    public void Normal_OpaqueSourceReplacesBackdrop_AndOpacityInterpolates()
    {
        Assert.Equal(Source, PixelBlender.Blend(Backdrop, Source, 1f));
        Assert.Equal(Backdrop, PixelBlender.Blend(Backdrop, Source, 0f));
        Rgba32 half = PixelBlender.Blend(Backdrop, Source, 0.5f);
        AssertClose(new Rgba32(120, 130, 135, 255), half);
    }

    [Fact]
    public void SeparableBlendModes_MatchSpecFormulas()
    {
        Vector4 cb = new Vector4(200, 100, 50, 255) / 255f;
        Vector4 cs = new Vector4(40, 160, 220, 255) / 255f;

        Rgba32 Expect(Func<float, float, float> f) => new(
            (byte)MathF.Round(Math.Clamp(f(cb.X, cs.X), 0, 1) * 255f),
            (byte)MathF.Round(Math.Clamp(f(cb.Y, cs.Y), 0, 1) * 255f),
            (byte)MathF.Round(Math.Clamp(f(cb.Z, cs.Z), 0, 1) * 255f),
            255);

        static float Screen(float b, float s) => b + s - (b * s);
        static float HardLight(float b, float s) => s <= 0.5f ? b * 2f * s : Screen(b, (2f * s) - 1f);

        AssertClose(Expect((b, s) => b * s), PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.Multiply));
        AssertClose(Expect(Screen), PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.Screen));
        AssertClose(Expect((b, s) => b + s), PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.Add));
        AssertClose(Expect((b, s) => b - s), PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.Subtract));
        AssertClose(Expect(MathF.Min), PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.Darken));
        AssertClose(Expect(MathF.Max), PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.Lighten));
        AssertClose(Expect((b, s) => HardLight(s, b)), PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.Overlay));
        AssertClose(Expect(HardLight), PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.HardLight));
        AssertClose(Expect((b, s) => MathF.Abs(b - s)), PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.Difference));
        AssertClose(Expect((b, s) => b + s - (2 * b * s)), PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.Exclusion));
        AssertClose(
            Expect((b, s) => b <= 0 ? 0 : s >= 1 ? 1 : MathF.Min(1, b / (1 - s))),
            PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.ColorDodge));
        AssertClose(
            Expect((b, s) => b >= 1 ? 1 : s <= 0 ? 0 : 1 - MathF.Min(1, (1 - b) / s)),
            PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.ColorBurn));
        AssertClose(
            Expect((b, s) =>
            {
                if (s <= 0.5f)
                {
                    return b - ((1 - (2 * s)) * b * (1 - b));
                }

                float d = b <= 0.25f ? ((((16 * b) - 12) * b) + 4) * b : MathF.Sqrt(b);
                return b + (((2 * s) - 1) * (d - b));
            }),
            PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.SoftLight));
    }

    [Fact]
    public void BlendModes_NeutralElements()
    {
        var white = new Rgba32(255, 255, 255, 255);
        var black = new Rgba32(0, 0, 0, 255);
        Assert.Equal(Backdrop, PixelBlender.Blend(Backdrop, white, 1f, PixelColorBlendingMode.Multiply));
        Assert.Equal(Backdrop, PixelBlender.Blend(Backdrop, black, 1f, PixelColorBlendingMode.Screen));
        Assert.Equal(Backdrop, PixelBlender.Blend(Backdrop, black, 1f, PixelColorBlendingMode.Add));
        Assert.Equal(Backdrop, PixelBlender.Blend(Backdrop, black, 1f, PixelColorBlendingMode.Subtract));
        Assert.Equal(Backdrop, PixelBlender.Blend(Backdrop, black, 1f, PixelColorBlendingMode.Difference));
        Assert.Equal(Backdrop, PixelBlender.Blend(Backdrop, black, 1f, PixelColorBlendingMode.Exclusion));
        Assert.Equal(Backdrop, PixelBlender.Blend(Backdrop, white, 1f, PixelColorBlendingMode.Darken));
        Assert.Equal(Backdrop, PixelBlender.Blend(Backdrop, black, 1f, PixelColorBlendingMode.Lighten));
        Assert.Equal(Backdrop, PixelBlender.Blend(Backdrop, black, 1f, PixelColorBlendingMode.ColorDodge));
        Assert.Equal(Backdrop, PixelBlender.Blend(Backdrop, white, 1f, PixelColorBlendingMode.ColorBurn));
        AssertClose(Backdrop, PixelBlender.Blend(Backdrop, new Rgba32(128, 128, 128, 255), 1f, PixelColorBlendingMode.HardLight), 2);
        AssertClose(Backdrop, PixelBlender.Blend(Backdrop, new Rgba32(128, 128, 128, 255), 1f, PixelColorBlendingMode.SoftLight), 2);
    }

    [Fact]
    public void NonSeparableBlendModes_MatchSpecDefinitions()
    {
        static float Lum(Rgba32 c) => (0.3f * c.R + 0.59f * c.G + 0.11f * c.B) / 255f;
        static float Sat(Rgba32 c) => (Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B))) / 255f;

        // Luminosity: backdrop hue/saturation with the source's luminance.
        Rgba32 lum = PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.Luminosity);
        Assert.InRange(Lum(lum), Lum(Source) - 0.01f, Lum(Source) + 0.01f);
        Assert.True(lum.R > lum.G && lum.G > lum.B, $"backdrop hue (orange) must be kept: {lum}");

        // Color: source hue/saturation with the backdrop's luminance.
        Rgba32 color = PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.Color);
        Assert.InRange(Lum(color), Lum(Backdrop) - 0.01f, Lum(Backdrop) + 0.01f);
        Assert.True(color.B > color.G && color.G > color.R, $"source hue (blue) must be kept: {color}");

        // Hue: source hue, backdrop saturation and luminance.
        Rgba32 hue = PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.Hue);
        Assert.InRange(Lum(hue), Lum(Backdrop) - 0.01f, Lum(Backdrop) + 0.01f);
        Assert.InRange(Sat(hue), Sat(Backdrop) - 0.02f, Sat(Backdrop) + 0.02f);
        Assert.True(hue.B > hue.R, $"source hue (blue) must be kept: {hue}");

        // Saturation: backdrop hue and luminance with the source's saturation.
        Rgba32 sat = PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.Saturation);
        Assert.InRange(Lum(sat), Lum(Backdrop) - 0.01f, Lum(Backdrop) + 0.01f);
        Assert.InRange(Sat(sat), Sat(Source) - 0.02f, Sat(Source) + 0.02f);
        Assert.True(sat.R > sat.B, $"backdrop hue (orange) must be kept: {sat}");

        // Grey source has no saturation: Saturation mode yields a grey of the backdrop luminance.
        Rgba32 grey = PixelBlender.Blend(Backdrop, new Rgba32(90, 90, 90, 255), 1f, PixelColorBlendingMode.Saturation);
        Assert.Equal(grey.R, grey.G);
        Assert.Equal(grey.G, grey.B);
    }

    // ----- PixelBlender: Porter-Duff -----

    [Fact]
    public void PorterDuff_Operators_OnHalfTransparentPixels()
    {
        // Backdrop: red at 50 %, source: blue at 50 %.
        var b = new Rgba32(255, 0, 0, 128);
        var s = new Rgba32(0, 0, 255, 128);
        const float ab = 128f / 255f;
        const float asrc = 128f / 255f;

        Rgba32 Blend(PixelAlphaCompositionMode mode) => PixelBlender.Blend(b, s, 1f, PixelColorBlendingMode.Normal, mode);
        static byte A(float v) => (byte)MathF.Round(v * 255f);

        // Clear -> transparent black.
        Assert.Equal(new Rgba32(0, 0, 0, 0), Blend(PixelAlphaCompositionMode.Clear));
        // Src -> source; Dest -> backdrop.
        Assert.Equal(s, Blend(PixelAlphaCompositionMode.Src));
        Assert.Equal(b, Blend(PixelAlphaCompositionMode.Dest));
        // SrcOver: ao = as + ab(1-as) = 0.7529; colour = (blue*as + red*ab*(1-as)) / ao.
        float aoOver = asrc + (ab * (1 - asrc));
        Rgba32 over = Blend(PixelAlphaCompositionMode.SrcOver);
        Assert.InRange(over.A, A(aoOver) - 1, A(aoOver) + 1);
        AssertClose(new Rgba32(A(ab * (1 - asrc) / aoOver), 0, A(asrc / aoOver), A(aoOver)), over);
        // DestOver is SrcOver with the roles swapped.
        Rgba32 destOver = Blend(PixelAlphaCompositionMode.DestOver);
        AssertClose(new Rgba32(A(ab / aoOver), 0, A(asrc * (1 - ab) / aoOver), A(aoOver)), destOver);
        // SrcIn: source colour, alpha as*ab.
        AssertClose(new Rgba32(0, 0, 255, A(asrc * ab)), Blend(PixelAlphaCompositionMode.SrcIn));
        // DestIn: backdrop colour, alpha ab*as.
        AssertClose(new Rgba32(255, 0, 0, A(ab * asrc)), Blend(PixelAlphaCompositionMode.DestIn));
        // SrcOut: source colour, alpha as(1-ab).
        AssertClose(new Rgba32(0, 0, 255, A(asrc * (1 - ab))), Blend(PixelAlphaCompositionMode.SrcOut));
        // DestOut: backdrop colour, alpha ab(1-as).
        AssertClose(new Rgba32(255, 0, 0, A(ab * (1 - asrc))), Blend(PixelAlphaCompositionMode.DestOut));
        // SrcAtop: alpha = ab; colour = blue*as + red*(1-as).
        Rgba32 atop = Blend(PixelAlphaCompositionMode.SrcAtop);
        AssertClose(new Rgba32(A(1 - asrc), 0, A(asrc), A(ab)), atop);
        // DestAtop: alpha = as; colour = red*ab + blue*(1-ab).
        AssertClose(new Rgba32(A(ab), 0, A(1 - ab), A(asrc)), Blend(PixelAlphaCompositionMode.DestAtop));
        // Xor: alpha = as(1-ab) + ab(1-as); equal weights -> purple.
        Rgba32 xor = Blend(PixelAlphaCompositionMode.Xor);
        byte xorAlpha = A((asrc * (1 - ab)) + (ab * (1 - asrc)));
        Assert.InRange(xor.A, xorAlpha - 1, xorAlpha + 1);
        Assert.InRange(Math.Abs(xor.R - xor.B), 0, 1);
    }

    [Fact]
    public void SourceOver_TransparentInputsAndDegenerateCases()
    {
        Assert.Equal(Backdrop, PixelBlender.Blend(Backdrop, new Rgba32(1, 2, 3, 0), 1f));
        Assert.Equal(new Rgba32(1, 2, 3, 255), PixelBlender.Blend(new Rgba32(9, 9, 9, 0), new Rgba32(1, 2, 3, 255), 1f));
        Assert.Equal(new Rgba32(0, 0, 0, 0), PixelBlender.Blend(new Rgba32(9, 9, 9, 0), new Rgba32(1, 2, 3, 0), 1f));
        // Fully transparent backdrop: the blend function must not tint the source (weighted by ab = 0).
        Assert.Equal(new Rgba32(1, 2, 3, 255), PixelBlender.Blend(new Rgba32(9, 9, 9, 0), new Rgba32(1, 2, 3, 255), 1f, PixelColorBlendingMode.Multiply));
        // Blend a whole row, in place.
        var row = new Rgba32[] { Backdrop, Backdrop, Backdrop };
        var src = new Rgba32[] { Source, new Rgba32(0, 0, 0, 0), new Rgba32(255, 255, 255, 128) };
        PixelBlender.Blend(row, row, src, 1f);
        Assert.Equal(Source, row[0]);
        Assert.Equal(Backdrop, row[1]);
        AssertClose(new Rgba32(228, 178, 153, 255), row[2]);
        Assert.Throws<ArgumentException>(() => PixelBlender.Blend(row, row, new Rgba32[2], 1f));
    }

    [Fact]
    public void BlendPercentage_ScalesEffect_AndModesComposeWithOpacity()
    {
        // Multiply at 50 % over an opaque backdrop: half way between the backdrop and the multiplied colour.
        Rgba32 full = PixelBlender.Blend(Backdrop, Source, 1f, PixelColorBlendingMode.Multiply);
        Rgba32 half = PixelBlender.Blend(Backdrop, Source, 0.5f, PixelColorBlendingMode.Multiply);
        AssertClose(new Rgba32((byte)((Backdrop.R + full.R + 1) / 2), (byte)((Backdrop.G + full.G + 1) / 2), (byte)((Backdrop.B + full.B + 1) / 2), 255), half, 1);
    }

    // ----- DrawImage -----

    /// <summary>
    /// Over an opaque backdrop the new blending overload and the original straight-alpha overload agree.
    /// They deliberately diverge where the backdrop itself is translucent: the original lerps the colours,
    /// while the new one performs true Porter-Duff source-over on premultiplied colour (checked separately
    /// in <see cref="DrawImage_OverTranslucentBackdrop_FollowsPorterDuff"/>).
    /// </summary>
    [Fact]
    public void DrawImage_NewOverload_MatchesLegacyOverload_OverOpaqueBackdrop()
    {
        using Image<Rgba32> backdrop = EffectsTests.Synthetic();
        for (int y = 0; y < backdrop.Height; y++)
        {
            for (int x = 0; x < backdrop.Width; x++)
            {
                Rgba32 p = backdrop[x, y];
                backdrop[x, y] = new Rgba32(p.R, p.G, p.B, 255);
            }
        }

        using Image<Rgba32> overlay = EffectsTests.LoadFixture("overlay_rgba.png");
        using Image<Rgba32> legacy = backdrop.Clone(c => c.DrawImage(overlay, new Point(5, 7), 0.75f));
        using Image<Rgba32> modern = backdrop.Clone(c => c.DrawImage(overlay, new Point(5, 7), PixelColorBlendingMode.Normal, PixelAlphaCompositionMode.SrcOver, 0.75f));
        int diff = EffectsTests.MaxDifference(legacy, modern);
        Assert.True(diff <= 1, $"max diff {diff}");
    }

    [Fact]
    public void DrawImage_OverTranslucentBackdrop_FollowsPorterDuff()
    {
        using var backdrop = new Image<Rgba32>(1, 1, new Rgba32(200, 100, 50, 128));
        using var overlay = new Image<Rgba32>(1, 1, new Rgba32(10, 220, 30, 64));
        using Image<Rgba32> result = backdrop.Clone(
            c => c.DrawImage(overlay, new Point(0, 0), PixelColorBlendingMode.Normal, PixelAlphaCompositionMode.SrcOver, 1f));

        // sa = 64/255, da = 128/255; outA = sa + da(1-sa); outC = (sc*sa + dc*da(1-sa)) / outA.
        const float Sa = 64f / 255f;
        const float Da = 128f / 255f;
        float outA = Sa + (Da * (1f - Sa));
        static byte Ch(float sc, float dc, float sa, float da, float outA)
            => (byte)Math.Clamp((float)Math.Round((((sc / 255f * sa) + (dc / 255f * da * (1f - sa))) / outA) * 255f), 0f, 255f);

        Rgba32 actual = result[0, 0];
        AssertClose(
            new Rgba32(Ch(10, 200, Sa, Da, outA), Ch(220, 100, Sa, Da, outA), Ch(30, 50, Sa, Da, outA), (byte)Math.Round(outA * 255f)),
            actual,
            1);
    }

    [Fact]
    public void DrawImage_SourceRectangle_CopiesOnlyThatPart_AndClipsAtEdges()
    {
        using var backdrop = new Image<Rgba32>(20, 20, new Rgba32(0, 0, 0, 255));
        using var overlay = new Image<Rgba32>(10, 10);
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                overlay[x, y] = new Rgba32((byte)(x * 20), (byte)(y * 20), 0, 255);
            }
        }

        var sourceRect = new Rectangle(2, 3, 4, 5);
        using Image<Rgba32> result = backdrop.Clone(c => c.DrawImage(overlay, new Point(15, 16), sourceRect, 1f));
        // Source pixels (2..5, 3..7) land at (15..18, 16..19); the last row of the source rectangle (y = 7 -> dest y = 20) is clipped.
        Assert.Equal(overlay[2, 3], result[15, 16]);
        Assert.Equal(overlay[5, 6], result[18, 19]);
        Assert.Equal(new Rgba32(0, 0, 0, 255), result[19, 16]);
        Assert.Equal(new Rgba32(0, 0, 0, 255), result[14, 16]);
        Assert.Equal(new Rgba32(0, 0, 0, 255), result[15, 15]);

        // Negative location clips the top-left of the source; a source rectangle beyond the source is clamped.
        using Image<Rgba32> negative = backdrop.Clone(c => c.DrawImage(overlay, new Point(-3, -4), new Rectangle(0, 0, 50, 50), 1f));
        Assert.Equal(overlay[3, 4], negative[0, 0]);
        Assert.Equal(overlay[9, 9], negative[6, 5]);
        Assert.Equal(new Rgba32(0, 0, 0, 255), negative[7, 5]);

        // Completely outside: no-op.
        using Image<Rgba32> outside = backdrop.Clone(c => c.DrawImage(overlay, new Point(100, 100), PixelColorBlendingMode.Normal, 1f));
        Assert.Equal(EffectsTests.Checksum(backdrop), EffectsTests.Checksum(outside));
    }

    [Fact]
    public void DrawImage_GraphicsOptions_AndAllPixelFormatsForSourceAndDestination()
    {
        using Image<Rgba32> backdrop = EffectsTests.Synthetic();
        using Image<Rgba32> overlay = EffectsTests.LoadFixture("overlay_rgba.png");
        var options = new GraphicsOptions { ColorBlendingMode = PixelColorBlendingMode.Screen, BlendPercentage = 0.6f };
        using Image<Rgba32> viaOptions = backdrop.Clone(c => c.DrawImage(overlay, new Point(3, 3), options));
        using Image<Rgba32> explicitArgs = backdrop.Clone(c => c.DrawImage(overlay, new Point(3, 3), PixelColorBlendingMode.Screen, PixelAlphaCompositionMode.SrcOver, 0.6f));
        Assert.Equal(EffectsTests.Checksum(viaOptions), EffectsTests.Checksum(explicitArgs));

        // Source in any pixel format gives the same colours.
        using Image<Bgra32> bgraOverlay = overlay.CloneAs<Bgra32>();
        using Image<Rgba32> viaBgra = backdrop.Clone(c => c.DrawImage(bgraOverlay, new Point(3, 3), options));
        Assert.Equal(EffectsTests.Checksum(viaOptions), EffectsTests.Checksum(viaBgra));
        using Image<L8> l8Overlay = overlay.CloneAs<L8>();
        using Image<Rgba32> viaL8 = backdrop.Clone(c => c.DrawImage(l8Overlay, new Point(3, 3), options));
        using Image<Rgba32> viaL8AsRgba = backdrop.Clone(c => c.DrawImage(l8Overlay.CloneAs<Rgba32>(), new Point(3, 3), options));
        Assert.Equal(EffectsTests.Checksum(viaL8AsRgba), EffectsTests.Checksum(viaL8));

        // Destination in every format.
        using Image<Rgb24> rgbDest = backdrop.CloneAs<Rgb24>().Clone(c => c.DrawImage(overlay, new Point(3, 3), options));
        using Image<Bgr24> bgrDest = backdrop.CloneAs<Bgr24>().Clone(c => c.DrawImage(overlay, new Point(3, 3), options));
        using Image<Rgba32> opaqueRef = backdrop.CloneAs<Rgb24>().CloneAs<Rgba32>().Clone(c => c.DrawImage(overlay, new Point(3, 3), options));
        for (int y = 0; y < backdrop.Height; y += 4)
        {
            for (int x = 0; x < backdrop.Width; x += 5)
            {
                Rgba32 expected = opaqueRef[x, y];
                Assert.Equal(new Rgb24(expected.R, expected.G, expected.B), rgbDest[x, y]);
                Assert.Equal(expected, bgrDest[x, y].ToRgba32());
            }
        }
    }

    [Fact]
    public void DrawImage_AppliesToEveryFrame_AndValidatesArguments()
    {
        using Image<Rgba32> frames = EffectsTests.TwoFrames();
        using Image<Rgba32> overlay = EffectsTests.LoadFixture("overlay_rgba.png");
        using Image<Rgba32> result = frames.Clone(c => c.DrawImage(overlay, new Point(2, 2), PixelColorBlendingMode.Multiply, 0.8f));
        for (int f = 0; f < 2; f++)
        {
            using var single = new Image<Rgba32>(new List<ImageFrame<Rgba32>> { frames.Frames[f].Clone() });
            single.Mutate(c => c.DrawImage(overlay, new Point(2, 2), PixelColorBlendingMode.Multiply, 0.8f));
            using var frame = new Image<Rgba32>(new List<ImageFrame<Rgba32>> { result.Frames[f].Clone() });
            Assert.Equal(EffectsTests.Checksum(single), EffectsTests.Checksum(frame));
        }

        Assert.Throws<ArgumentNullException>(() => frames.Mutate(c => c.DrawImage(null!, new Point(0, 0), PixelColorBlendingMode.Normal, 1f)));
        Assert.Throws<ArgumentNullException>(() => frames.Mutate(c => c.DrawImage(overlay, new Point(0, 0), (GraphicsOptions)null!)));
    }

    [Fact]
    public void GraphicsOptions_DefaultsAndClone()
    {
        var options = new GraphicsOptions();
        Assert.True(options.Antialias);
        Assert.Equal(16, options.AntialiasSubpixelDepth);
        Assert.Equal(1f, options.BlendPercentage);
        Assert.Equal(PixelColorBlendingMode.Normal, options.ColorBlendingMode);
        Assert.Equal(PixelAlphaCompositionMode.SrcOver, options.AlphaCompositionMode);
        options.BlendPercentage = 3f;
        Assert.Equal(1f, options.BlendPercentage);
        options.BlendPercentage = -1f;
        Assert.Equal(0f, options.BlendPercentage);
        options.BlendPercentage = 0.25f;
        options.ColorBlendingMode = PixelColorBlendingMode.Hue;
        GraphicsOptions clone = options.DeepClone();
        Assert.Equal(0.25f, clone.BlendPercentage);
        Assert.Equal(PixelColorBlendingMode.Hue, clone.ColorBlendingMode);
        clone.ColorBlendingMode = PixelColorBlendingMode.Normal;
        Assert.Equal(PixelColorBlendingMode.Hue, options.ColorBlendingMode);
    }
}
