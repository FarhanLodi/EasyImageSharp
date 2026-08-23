using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

public class BackgroundColorTests
{
    /// <summary>Straight-alpha source-over of <paramref name="src"/> onto an opaque background.</summary>
    private static Rgba32 SrcOver(Rgba32 src, Color bg)
    {
        float a = src.A / 255f;
        return new Rgba32(
            (byte)Math.Round((src.R * a) + (bg.R * (1 - a))),
            (byte)Math.Round((src.G * a) + (bg.G * (1 - a))),
            (byte)Math.Round((src.B * a) + (bg.B * (1 - a))),
            255);
    }

    private static void AssertClose(Rgba32 expected, Rgba32 actual)
    {
        Assert.InRange<int>(actual.R, expected.R - 1, expected.R + 1);
        Assert.InRange<int>(actual.G, expected.G - 1, expected.G + 1);
        Assert.InRange<int>(actual.B, expected.B - 1, expected.B + 1);
        Assert.Equal(expected.A, actual.A);
    }

    [Fact]
    public void BackgroundColor_Rgba32_CompositesOverWhite()
    {
        using Image<Rgba32> original = TestImages.AlphaGradient(40, 40);
        using Image<Rgba32> flattened = original.Clone(ctx => ctx.BackgroundColor(Color.White));

        Assert.Equal(original.Width, flattened.Width);
        Assert.Equal(original.Height, flattened.Height);

        // Row 0 is fully opaque (unchanged), the last row fully transparent (becomes white),
        // and rows in between blend according to their alpha.
        foreach ((int x, int y) in new[] { (0, 0), (17, 0), (5, 10), (20, 20), (33, 29), (39, 39), (7, 39) })
        {
            Rgba32 src = original[x, y];
            AssertClose(SrcOver(src, Color.White), flattened[x, y]);
        }

        Assert.Equal(new Rgba32(255, 255, 255, 255), flattened[3, 39]);
        for (int y = 0; y < flattened.Height; y++)
        {
            for (int x = 0; x < flattened.Width; x++)
            {
                Assert.Equal(255, flattened[x, y].A);
            }
        }
    }

    [Fact]
    public void BackgroundColor_MatchesManualMathForKnownPixels()
    {
        using var image = new Image<Rgba32>(3, 1);
        image[0, 0] = new Rgba32(200, 100, 50, 128);
        image[1, 0] = new Rgba32(0, 0, 0, 0);
        image[2, 0] = new Rgba32(10, 20, 30, 255);
        image.Mutate(ctx => ctx.BackgroundColor(Color.FromRgb(0, 0, 255)));

        // 200 * 128/255 + 0 * 127/255 = 100.4 ; 100 * 0.502 = 50.2 ; 50 * 0.502 + 255 * 0.498 = 152.1
        AssertClose(new Rgba32(100, 50, 152, 255), image[0, 0]);
        Assert.Equal(new Rgba32(0, 0, 255, 255), image[1, 0]);
        Assert.Equal(new Rgba32(10, 20, 30, 255), image[2, 0]);
    }

    [Fact]
    public void BackgroundColor_Bgra32_AlsoComposites()
    {
        using var image = new Image<Bgra32>(2, 1);
        image[0, 0] = new Bgra32(255, 0, 0, 0);
        image[1, 0] = new Bgra32(255, 0, 0, 255);
        image.Mutate(ctx => ctx.BackgroundColor(Color.Lime));

        Assert.Equal(new Bgra32(0, 255, 0, 255), image[0, 0]);
        Assert.Equal(new Bgra32(255, 0, 0, 255), image[1, 0]);
    }

    [Fact]
    public void BackgroundColor_OpaqueFormats_AreUnchanged()
    {
        using Image<Rgb24> rgb = TestImages.Gradient(16, 16);
        using Image<Rgb24> rgbAfter = rgb.Clone(ctx => ctx.BackgroundColor(Color.Red));
        Assert.Equal(0, TestImages.AveragePixelDifference(rgb, rgbAfter));

        using Image<L8> gray = rgb.CloneAs<L8>();
        using Image<L8> grayAfter = gray.Clone(ctx => ctx.BackgroundColor(Color.Red));
        for (int y = 0; y < gray.Height; y++)
        {
            for (int x = 0; x < gray.Width; x++)
            {
                Assert.Equal(gray[x, y], grayAfter[x, y]);
            }
        }

        using Image<Bgr24> bgr = rgb.CloneAs<Bgr24>();
        using Image<Bgr24> bgrAfter = bgr.Clone(ctx => ctx.BackgroundColor(Color.Red));
        Assert.Equal(bgr[5, 7], bgrAfter[5, 7]);
    }

    [Fact]
    public void BackgroundColor_TransparentBackground_LeavesImageUnchanged()
    {
        using var image = new Image<Rgba32>(2, 1);
        image[0, 0] = new Rgba32(200, 100, 50, 128);
        image[1, 0] = new Rgba32(1, 2, 3, 0);
        image.Mutate(ctx => ctx.BackgroundColor(Color.Transparent));

        Assert.Equal(new Rgba32(200, 100, 50, 128), image[0, 0]);
        Assert.Equal(new Rgba32(1, 2, 3, 0), image[1, 0]);
    }

    [Fact]
    public void BackgroundColor_TranslucentBackground_UsesGeneralSourceOver()
    {
        using var image = new Image<Rgba32>(1, 1);
        image[0, 0] = new Rgba32(255, 0, 0, 128);
        image.Mutate(ctx => ctx.BackgroundColor(Color.Blue.WithAlpha(128)));

        // out.A = 0.502 + 0.502 * 0.498 = 0.752 -> 192; red weight 0.502/0.752, blue weight 0.25/0.752.
        Rgba32 result = image[0, 0];
        Assert.InRange<int>(result.A, 191, 193);
        Assert.InRange<int>(result.R, 169, 171);
        Assert.Equal(0, result.G);
        Assert.InRange<int>(result.B, 84, 86);
    }

    [Fact]
    public void BackgroundColor_AppliesToAllFrames()
    {
        using var image = new Image<Rgba32>(4, 4, new Rgba32(0, 0, 0, 0));
        image.Frames.AddFrame(image.Frames.RootFrame);
        image.Mutate(ctx => ctx.BackgroundColor(Color.Coral));

        Assert.Equal(new Rgba32(255, 127, 80, 255), image.Frames[0][2, 2]);
        Assert.Equal(new Rgba32(255, 127, 80, 255), image.Frames[1][2, 2]);
    }

    [Fact]
    public void Pad_WithColor_FillsWithThatColor()
    {
        using var image = new Image<Rgba32>(4, 4, new Rgba32(1, 2, 3));
        image.Mutate(ctx => ctx.Pad(8, 8, Color.Coral));

        Assert.Equal(8, image.Width);
        Assert.Equal(8, image.Height);
        Assert.Equal(new Rgba32(255, 127, 80, 255), image[0, 0]);
        Assert.Equal(new Rgba32(255, 127, 80, 255), image[7, 7]);
        Assert.Equal(new Rgba32(1, 2, 3), image[3, 3]);

        // The Color-typed extension is also callable explicitly and on opaque formats.
        using var rgb = new Image<Rgb24>(2, 2, new Rgb24(9, 9, 9));
        rgb.Mutate(ctx => ProcessingExtensions.Pad(ctx, 4, 4, Color.Navy));
        Assert.Equal(new Rgb24(0, 0, 128), rgb[0, 0]);
        Assert.Equal(new Rgb24(9, 9, 9), rgb[1, 1]);

        // The Rgba32 overload keeps working unchanged.
        using var viaRgba = new Image<Rgba32>(2, 2, new Rgba32(1, 2, 3));
        viaRgba.Mutate(ctx => ctx.Pad(4, 4, new Rgba32(255, 255, 255)));
        Assert.Equal(new Rgba32(255, 255, 255), viaRgba[0, 0]);
    }

    [Fact]
    public void ResizeOptions_PadColor_AcceptsColorAndRgba32()
    {
        var withColor = new ResizeOptions { Size = new Size(10, 10), Mode = ResizeMode.Pad, PadColor = Color.Red };
        Assert.Equal(Color.Red, withColor.PadColor);

        var withRgba = new ResizeOptions { Size = new Size(10, 10), Mode = ResizeMode.Pad, PadColor = new Rgba32(0, 255, 0) };
        Assert.Equal(Color.Lime, withRgba.PadColor);
        Rgba32 asPixel = withRgba.PadColor;
        Assert.Equal(new Rgba32(0, 255, 0), asPixel);

        Assert.Equal(Color.Transparent, new ResizeOptions().PadColor);

        using var image = new Image<Rgba32>(20, 10, new Rgba32(1, 2, 3));
        image.Mutate(ctx => ctx.Resize(withColor));
        Assert.Equal(10, image.Width);
        Assert.Equal(10, image.Height);
        Assert.Equal(new Rgba32(255, 0, 0, 255), image[5, 0]); // Letterbox strip.
        Assert.Equal(new Rgba32(1, 2, 3), image[5, 5]); // Content.
    }
}
