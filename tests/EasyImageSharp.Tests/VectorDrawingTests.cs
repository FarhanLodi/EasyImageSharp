using System.Numerics;
using EasyImageSharp.Drawing;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Exact tests for brushes, vector paths, stroking, fill rules and clipping. Expected values are derived
/// analytically (pixel-aligned geometry, closed-form gradient offsets) rather than taken from the renderer.
/// </summary>
public class VectorDrawingTests
{
    private static readonly DrawingOptions NoAa = new() { Antialias = false };

    /// <summary>Draws white onto a transparent image and returns the alpha channel, which is the coverage.</summary>
    private static byte[,] Coverage(int width, int height, Action<IImageProcessingContext> draw)
    {
        using var image = new Image<Rgba32>(width, height, Rgba32.Transparent);
        image.Mutate(draw);
        var alpha = new byte[width, height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                alpha[x, y] = image[x, y].A;
            }
        }

        return alpha;
    }

    private static double Sum(byte[,] coverage)
    {
        double total = 0;
        foreach (byte v in coverage)
        {
            total += v / 255.0;
        }

        return total;
    }

    private static void AssertSamePixels<TPixel>(Image<TPixel> expected, Image<TPixel> actual)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Assert.True(expected[x, y].Equals(actual[x, y]), $"pixel ({x},{y}): expected {expected[x, y]} got {actual[x, y]}");
            }
        }
    }

    // ----- Solid brushes and paths reproduce the colour operations exactly -----

    [Fact]
    public void RectanglePath_FillsExactlyLikeFillRectangle()
    {
        var rect = new RectangleF(3.3f, 4.7f, 10.2f, 5.9f);
        using var expected = new Image<Rgb24>(20, 16, new Rgb24(10, 20, 30));
        using Image<Rgb24> actual = expected.Clone();
        expected.Mutate(ctx => ctx.FillRectangle(Color.Orange, rect));
        actual.Mutate(ctx => ctx.Fill(Color.Orange, VectorPath.FromRectangle(rect)));
        AssertSamePixels(expected, actual);
    }

    [Fact]
    public void SolidBrush_MatchesColourOverloads()
    {
        var color = new Color(200, 40, 90, 160);
        var options = new DrawingOptions { BlendPercentage = 0.8f };
        using var expected = new Image<Rgba32>(30, 30, new Rgba32(0, 100, 200, 120));
        using Image<Rgba32> actual = expected.Clone();
        expected.Mutate(ctx => ctx
            .FillRectangle(color, new RectangleF(2.5f, 3.25f, 11, 9), options)
            .FillEllipse(color, new RectangleF(10.2f, 8.7f, 15, 12), options)
            .FillPolygon(color, [new PointF(1, 28), new PointF(14.5f, 15), new PointF(29, 29)], options));
        var brush = new SolidBrush(color);
        actual.Mutate(ctx => ctx
            .FillRectangle(brush, new RectangleF(2.5f, 3.25f, 11, 9), options)
            .FillEllipse(brush, new RectangleF(10.2f, 8.7f, 15, 12), options)
            .FillPolygon(brush, [new PointF(1, 28), new PointF(14.5f, 15), new PointF(29, 29)], options));
        AssertSamePixels(expected, actual);
    }

    [Fact]
    public void UniformGradient_BlendsExactlyLikeTheSolidColour()
    {
        // A gradient whose stops are equal shades one colour everywhere, so the brush painter must produce the
        // single-colour painter's pixels in every pixel format path (Rgba32, Rgb24 and the generic one).
        var color = new Color(200, 100, 50, 180);
        var gradient = new LinearGradientBrush(new PointF(0, 0), new PointF(25, 7), color, color);
        var options = new DrawingOptions { BlendPercentage = 0.7f };
        VectorPath path = new PathBuilder().AddEllipse(new RectangleF(1.3f, 2.1f, 22.4f, 15.8f)).Build();

        using var rgbaExpected = new Image<Rgba32>(26, 20, new Rgba32(10, 220, 30, 90));
        using Image<Rgba32> rgbaActual = rgbaExpected.Clone();
        rgbaExpected.Mutate(ctx => ctx.Fill(new SolidBrush(color), path, options));
        rgbaActual.Mutate(ctx => ctx.Fill(gradient, path, options));
        AssertSamePixels(rgbaExpected, rgbaActual);

        using var rgbExpected = new Image<Rgb24>(26, 20, new Rgb24(10, 220, 30));
        using Image<Rgb24> rgbActual = rgbExpected.Clone();
        rgbExpected.Mutate(ctx => ctx.Fill(new SolidBrush(color), path, options));
        rgbActual.Mutate(ctx => ctx.Fill(gradient, path, options));
        AssertSamePixels(rgbExpected, rgbActual);

        using var bgraExpected = new Image<Bgra32>(26, 20, new Bgra32(10, 220, 30, 255));
        using Image<Bgra32> bgraActual = bgraExpected.Clone();
        bgraExpected.Mutate(ctx => ctx.Fill(new SolidBrush(color), path, options));
        bgraActual.Mutate(ctx => ctx.Fill(gradient, path, options));
        AssertSamePixels(bgraExpected, bgraActual);
    }

    [Fact]
    public void DefaultStylePens_DrawExactlyLikeTheColourOverloads()
    {
        PointF[] zigzag = [new(2.5f, 3.5f), new(20.2f, 8.9f), new(5.3f, 22.1f), new(27.7f, 26.4f)];
        using var expected = new Image<Rgb24>(32, 32, new Rgb24(255, 255, 255));
        using Image<Rgb24> actual = expected.Clone();
        expected.Mutate(ctx => ctx
            .DrawLines(Color.Red, 2.5f, zigzag, DrawingOptions.Default)
            .DrawPolygon(Color.Blue, 1.5f, zigzag, DrawingOptions.Default)
            .DrawRectangle(Color.Green, 3, new RectangleF(4, 4, 20, 14))
            .DrawEllipse(Color.Purple, 2, new RectangleF(6.5f, 9.5f, 18, 12)));
        actual.Mutate(ctx => ctx
            .DrawLines(new Pen(Color.Red, 2.5f), zigzag)
            .DrawPolygon(new Pen(new SolidBrush(Color.Blue)) { Thickness = 1.5f, MiterLimit = 4 }, zigzag)
            .DrawRectangle(new Pen(Color.Green, 3), new RectangleF(4, 4, 20, 14))
            .DrawEllipse(new Pen(Color.Purple, 2), new RectangleF(6.5f, 9.5f, 18, 12)));
        AssertSamePixels(expected, actual);
    }

    // ----- Gradients -----

    [Fact]
    public void LinearGradient_ColoursEachPixelByItsProjection()
    {
        var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(10, 0), Color.Black, Color.White);
        using var image = new Image<Rgba32>(10, 3, Rgba32.Transparent);
        image.Mutate(ctx => ctx.Fill(brush));
        for (int x = 0; x < 10; x++)
        {
            double t = (x + 0.5) / 10;
            byte expected = (byte)Math.Round(255 * t);
            for (int y = 0; y < 3; y++)
            {
                Assert.Equal(new Rgba32(expected, expected, expected, 255), image[x, y]);
            }
        }
    }

    [Theory]
    [InlineData(WrapMode.Clamp)]
    [InlineData(WrapMode.Repeat)]
    [InlineData(WrapMode.Reflect)]
    [InlineData(WrapMode.Transparent)]
    public void LinearGradient_WrapModes(WrapMode mode)
    {
        var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(4, 0), Color.Black, Color.White) { WrapMode = mode };
        using var image = new Image<Rgba32>(12, 1, Rgba32.Transparent);
        image.Mutate(ctx => ctx.Fill(brush));
        for (int x = 0; x < 12; x++)
        {
            double t = (x + 0.5) / 4;
            double wrapped = mode switch
            {
                WrapMode.Repeat => t - Math.Floor(t),
                WrapMode.Reflect => t % 2 > 1 ? 2 - (t % 2) : t % 2,
                _ => Math.Min(t, 1),
            };
            if (mode == WrapMode.Transparent && t > 1)
            {
                Assert.Equal(0, image[x, 0].A);
                continue;
            }

            byte expected = (byte)Math.Round(255 * wrapped);
            Assert.Equal(new Rgba32(expected, expected, expected, 255), image[x, 0]);
        }
    }

    [Fact]
    public void GradientStops_AreSorted_AndEqualOffsetsMakeAHardEdge()
    {
        var brush = new LinearGradientBrush(
            new PointF(0, 0),
            new PointF(10, 0),
            new ColorStop(1f, Color.Blue),
            new ColorStop(0.5f, Color.Red),
            new ColorStop(0.5f, Color.Lime),
            new ColorStop(0f, Color.Red));
        Assert.Equal(new[] { 0f, 0.5f, 0.5f, 1f }, brush.Stops.Select(s => s.Offset));
        using var image = new Image<Rgba32>(10, 1, Rgba32.Transparent);
        image.Mutate(ctx => ctx.Fill(brush));
        Assert.Equal(new Rgba32(255, 0, 0, 255), image[2, 0]);    // Red up to the edge...
        Assert.Equal(new Rgba32(255, 0, 0, 255), image[4, 0]);
        Assert.Equal(0, image[5, 0].R);                          // ...lime after it (offset 0.55: 10% towards blue).
        Assert.InRange(image[5, 0].G, 229, 230);
        Assert.InRange(image[5, 0].B, 25, 26);
        Assert.True(image[9, 0].B > 200);
    }

    [Fact]
    public void Gradient_FadeToTransparent_KeepsItsHue()
    {
        // Premultiplied interpolation: halfway between opaque red and transparent blue is half-transparent red.
        var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(9, 0), new Color(255, 0, 0, 255), new Color(0, 0, 255, 0));
        using var image = new Image<Rgba32>(9, 1, Rgba32.Transparent);
        image.Mutate(ctx => ctx.Fill(brush));
        Rgba32 middle = image[4, 0];
        Assert.Equal(255, middle.R);
        Assert.Equal(0, middle.B);
        Assert.InRange(middle.A, 127, 128);
    }

    [Fact]
    public void Gradient_LinearLightInterpolation_BlendsInLinearLight()
    {
        var srgb = new LinearGradientBrush(new PointF(0, 0), new PointF(10, 0), Color.Black, Color.White);
        var linear = new LinearGradientBrush(new PointF(0, 0), new PointF(10, 0), Color.Black, Color.White)
        {
            Interpolation = GradientInterpolation.LinearLight,
        };
        using var a = new Image<Rgb24>(10, 1);
        using var b = new Image<Rgb24>(10, 1);
        a.Mutate(ctx => ctx.Fill(srgb));
        b.Mutate(ctx => ctx.Fill(linear));
        for (int x = 0; x < 10; x++)
        {
            double t = (x + 0.5) / 10;
            Assert.Equal((byte)Math.Round(255 * t), a[x, 0].R);

            // The sRGB encoding of linear value t, rounded to nearest.
            double encoded = t <= 0.0031308 ? 12.92 * t : (1.055 * Math.Pow(t, 1 / 2.4)) - 0.055;
            Assert.Equal((byte)Math.Round(255 * encoded), b[x, 0].R);
        }

        // Linear 0.5 (the middle of a 9-pixel gradient) is sRGB 0.7354, code 188, where sRGB blending gives 128.
        using var middle = new Image<Rgb24>(9, 1);
        middle.Mutate(ctx => ctx.Fill(new LinearGradientBrush(new PointF(0, 0), new PointF(9, 0), Color.Black, Color.White)
        {
            Interpolation = GradientInterpolation.LinearLight,
        }));
        Assert.Equal(188, middle[4, 0].R);
    }

    [Fact]
    public void RadialGradient_OffsetIsDistanceOverRadius()
    {
        var brush = new RadialGradientBrush(new PointF(10, 10), 10, Color.Black, Color.White);
        using var image = new Image<Rgba32>(20, 20, Rgba32.Transparent);
        image.Mutate(ctx => ctx.Fill(brush));
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                double d = Math.Sqrt(Math.Pow(x + 0.5 - 10, 2) + Math.Pow(y + 0.5 - 10, 2));
                double expected = 255 * Math.Min(d / 10, 1);
                Assert.InRange((double)image[x, y].R, expected - 0.51, expected + 0.51);
            }
        }
    }

    [Fact]
    public void RadialGradient_FocalPoint_RunsFromFocusToCircle()
    {
        // Centre (20, 20.5), radius 10, focus 5 pixels to the left: on the axis the offset is the distance from the
        // focus over the distance from the focus to the circle (15 to the right, 5 to the left).
        var brush = new RadialGradientBrush(new PointF(20, 20.5f), 10, new PointF(15, 20.5f), new ColorStop(0, Color.Black), new ColorStop(1, Color.White));
        using var image = new Image<Rgba32>(40, 41, Rgba32.Transparent);
        image.Mutate(ctx => ctx.Fill(brush));
        foreach (int x in new[] { 15, 18, 22, 26, 28 })
        {
            double t = (x + 0.5 - 15) / 15;
            Assert.InRange((double)image[x, 20].R, (255 * t) - 0.51, (255 * t) + 0.51);
        }

        foreach (int x in new[] { 10, 12, 14 })
        {
            double t = (15 - (x + 0.5)) / 5;
            Assert.InRange((double)image[x, 20].R, (255 * t) - 0.51, (255 * t) + 0.51);
        }

        Assert.Equal(255, image[35, 20].R); // Outside the circle: the last stop (clamp).
    }

    [Fact]
    public void ConicGradient_SweepsClockwiseFromTheStartAngle()
    {
        var brush = new ConicGradientBrush(new PointF(10.5f, 10.5f), 0, new ColorStop(0, Color.Black), new ColorStop(1, Color.White));
        using var image = new Image<Rgba32>(21, 21, Rgba32.Transparent);
        image.Mutate(ctx => ctx.Fill(brush));
        Assert.Equal(0, image[15, 10].R);     // 0 degrees: right of the centre.
        Assert.Equal(64, image[10, 15].R);    // 90 degrees: below (y points down), offset 0.25.
        Assert.Equal(128, image[5, 10].R);    // 180 degrees: offset 0.5.
        Assert.Equal(191, image[10, 5].R);    // 270 degrees: offset 0.75.

        var rotated = new ConicGradientBrush(new PointF(10.5f, 10.5f), 90, new ColorStop(0, Color.Black), new ColorStop(1, Color.White));
        image.Mutate(ctx => ctx.Fill(rotated));
        Assert.Equal(0, image[10, 15].R);
        Assert.Equal(64, image[5, 10].R);
    }

    [Fact]
    public void BrushTransform_RotatesTheGradient()
    {
        var vertical = new LinearGradientBrush(new PointF(0, 0), new PointF(0, 10), Color.Black, Color.White);
        var rotated = new LinearGradientBrush(new PointF(0, 0), new PointF(10, 0), Color.Black, Color.White)
        {
            Transform = Matrix3x2.CreateRotation(MathF.PI / 2),
        };
        using var a = new Image<Rgb24>(6, 10);
        using var b = new Image<Rgb24>(6, 10);
        a.Mutate(ctx => ctx.Fill(vertical));
        b.Mutate(ctx => ctx.Fill(rotated));
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 6; x++)
            {
                Assert.InRange(b[x, y].R - a[x, y].R, -1, 1);
            }
        }

        Assert.True(a[0, 9].R > a[0, 0].R);
    }

    [Fact]
    public void SingularBrushTransform_PaintsNothing()
    {
        var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(10, 0), Color.Black, Color.White)
        {
            Transform = Matrix3x2.CreateScale(0, 1),
        };
        using var image = new Image<Rgba32>(4, 4, Rgba32.Transparent);
        image.Mutate(ctx => ctx.Fill(brush));
        Assert.Equal(Rgba32.Transparent, image[2, 2]);
    }

    // ----- Image brushes -----

    private static Image<Rgba32> Tile()
    {
        var tile = new Image<Rgba32>(3, 2);
        tile[0, 0] = new Rgba32(255, 0, 0);
        tile[1, 0] = new Rgba32(0, 255, 0);
        tile[2, 0] = new Rgba32(0, 0, 255);
        tile[0, 1] = new Rgba32(255, 255, 0);
        tile[1, 1] = new Rgba32(0, 255, 255);
        tile[2, 1] = new Rgba32(255, 0, 255, 128);
        return tile;
    }

    [Theory]
    [InlineData(ImageBrushSampling.Bilinear)]
    [InlineData(ImageBrushSampling.NearestNeighbor)]
    public void ImageBrush_Repeat_TilesTheImageExactly(ImageBrushSampling sampling)
    {
        using Image<Rgba32> tile = Tile();
        var brush = new ImageBrush(tile) { Sampling = sampling };
        using var image = new Image<Rgba32>(9, 6, Rgba32.Transparent);
        image.Mutate(ctx => ctx.Fill(brush));
        for (int y = 0; y < 6; y++)
        {
            for (int x = 0; x < 9; x++)
            {
                Assert.Equal(tile[x % 3, y % 2], image[x, y]);
            }
        }
    }

    [Fact]
    public void ImageBrush_ReflectClampAndTransparent()
    {
        using Image<Rgba32> tile = Tile();
        using var image = new Image<Rgba32>(9, 2, Rgba32.Transparent);
        image.Mutate(ctx => ctx.Fill(new ImageBrush(tile) { WrapModeX = WrapMode.Reflect }));
        int[] reflected = [0, 1, 2, 2, 1, 0, 0, 1, 2];
        for (int x = 0; x < 9; x++)
        {
            Assert.Equal(tile[reflected[x], 0], image[x, 0]);
        }

        image.Mutate(ctx => ctx.Fill(Color.Transparent).Fill(new ImageBrush(tile) { WrapModeX = WrapMode.Clamp }));
        Assert.Equal(tile[2, 0], image[7, 0]);

        using var cleared = new Image<Rgba32>(9, 2, Rgba32.Transparent);
        cleared.Mutate(ctx => ctx.Fill(new ImageBrush(tile) { WrapModeX = WrapMode.Transparent }));
        Assert.Equal(tile[1, 1], cleared[1, 1]);
        Assert.Equal(Rgba32.Transparent, cleared[5, 0]);
    }

    [Fact]
    public void ImageBrush_Transform_ScalesAndMovesThePattern()
    {
        using Image<Rgba32> tile = Tile();
        var brush = new ImageBrush(tile, new Rectangle(0, 0, 3, 1))
        {
            Sampling = ImageBrushSampling.NearestNeighbor,
            Transform = Matrix3x2.CreateScale(2) * Matrix3x2.CreateTranslation(1, 0),
        };
        using var image = new Image<Rgba32>(13, 2, Rgba32.Transparent);
        image.Mutate(ctx => ctx.Fill(brush));
        for (int x = 0; x < 13; x++)
        {
            int source = (((x - 1) >> 1) % 3 + 3) % 3;
            Assert.Equal(tile[source, 0], image[x, 0]);
            Assert.Equal(tile[source, 0], image[x, 1]);
        }
    }

    [Fact]
    public void ImageBrush_CopiesItsSource()
    {
        using Image<Rgba32> tile = Tile();
        var brush = new ImageBrush(tile);
        tile[0, 0] = new Rgba32(1, 2, 3);
        using var image = new Image<Rgba32>(1, 1, Rgba32.Transparent);
        image.Mutate(ctx => ctx.Fill(brush));
        Assert.Equal(new Rgba32(255, 0, 0), image[0, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageBrush(tile, new Rectangle(1, 0, 3, 1)));

        // Other pixel formats are read through their RGBA conversion.
        using Image<Rgb24> rgb = tile.CloneAs<Rgb24>();
        var rgbBrush = new ImageBrush(rgb, new Rectangle(1, 1, 2, 1));
        Assert.Equal(new Size(2, 1), rgbBrush.Size);
        image.Mutate(ctx => ctx.Fill(rgbBrush));
        Assert.Equal(new Rgba32(0, 255, 255), image[0, 0]);

        var disposed = new Image<Rgba32>(2, 2);
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => new ImageBrush(disposed));
        var disposedRgb = new Image<Rgb24>(2, 2);
        disposedRgb.Dispose();
        Assert.Throws<ObjectDisposedException>(() => new ImageBrush(disposedRgb));
    }

    // ----- Fill rules -----

    [Fact]
    public void FillRules_NestedFiguresInTheSameDirection()
    {
        VectorPath path = new PathBuilder()
            .AddRectangle(new RectangleF(2, 2, 16, 16))
            .AddRectangle(new RectangleF(6, 6, 8, 8))
            .Build();
        byte[,] nonZero = Coverage(20, 20, ctx => ctx.Fill(Color.White, path));
        byte[,] evenOdd = Coverage(20, 20, ctx => ctx.Fill(Color.White, path, new DrawingOptions { FillRule = FillRule.EvenOdd }));
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                bool outer = x >= 2 && x < 18 && y >= 2 && y < 18;
                bool inner = x >= 6 && x < 14 && y >= 6 && y < 14;
                Assert.Equal(outer ? 255 : 0, nonZero[x, y]);
                Assert.Equal(outer && !inner ? 255 : 0, evenOdd[x, y]);
            }
        }
    }

    [Fact]
    public void NonZero_OppositeDirectionCutsAHole()
    {
        VectorPath path = new PathBuilder()
            .AddRectangle(new RectangleF(2, 2, 16, 16))
            .MoveTo(6, 6).LineTo(6, 14).LineTo(14, 14).LineTo(14, 6).Close()
            .Build();
        byte[,] coverage = Coverage(20, 20, ctx => ctx.Fill(Color.White, path));
        Assert.Equal(255, coverage[3, 3]);
        Assert.Equal(0, coverage[10, 10]);
        Assert.Equal(255, coverage[16, 10]);
    }

    [Fact]
    public void FillPolygon_Pentagram_HonoursTheFillRule()
    {
        var star = new PointF[5];
        for (int k = 0; k < 5; k++)
        {
            double angle = (-90 + (144 * k)) * Math.PI / 180;
            star[k] = new PointF((float)(20 + (15 * Math.Cos(angle))), (float)(20 + (15 * Math.Sin(angle))));
        }

        byte[,] nonZero = Coverage(40, 40, ctx => ctx.FillPolygon(Color.White, star));
        byte[,] evenOdd = Coverage(40, 40, ctx => ctx.FillPolygon(Color.White, star, new DrawingOptions { FillRule = FillRule.EvenOdd }));
        byte[,] brush = Coverage(40, 40, ctx => ctx.FillPolygon(new SolidBrush(Color.White), star, new DrawingOptions { FillRule = FillRule.EvenOdd }));
        Assert.Equal(255, nonZero[20, 20]);
        Assert.Equal(0, evenOdd[20, 20]);
        Assert.Equal(evenOdd, brush);
        Assert.Equal(255, evenOdd[20, 8]); // A tip is inside under both rules.
        Assert.Equal(255, nonZero[20, 8]);
    }

    [Fact]
    public void HugeCoordinates_AreClippedNotClamped()
    {
        // The long edge runs from the origin with slope 1/2. Clamping each coordinate to the fixed-point range
        // separately would bend it to slope 1 and uncover pixel (10, 8).
        byte[,] coverage = Coverage(24, 24, ctx => ctx.FillPolygon(Color.White, new PointF(0, 0), new PointF(2e8f, 1e8f), new PointF(0, 1e8f)));
        Assert.Equal(255, coverage[10, 8]);
        Assert.Equal(0, coverage[19, 5]);
        Assert.Equal(255, coverage[2, 20]);
    }

    // ----- Paths -----

    [Fact]
    public void Bounds_UseTheTrueCurveExtremes()
    {
        VectorPath path = new PathBuilder()
            .MoveTo(0, 0)
            .CubicBezierTo(new PointF(10, 20), new PointF(20, -20), new PointF(30, 0))
            .QuadraticBezierTo(new PointF(40, 30), new PointF(50, 0))
            .Build();
        double minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i <= 100000; i++)
        {
            double t = i / 100000.0, u = 1 - t;
            double cubic = (3 * u * u * t * 20) + (3 * u * t * t * -20);
            double quad = 2 * u * t * 30;
            minY = Math.Min(minY, Math.Min(cubic, quad));
            maxY = Math.Max(maxY, Math.Max(cubic, quad));
        }

        RectangleF b = path.Bounds;
        Assert.Equal(0, b.X, 4);
        Assert.Equal(50, b.Right, 4);
        Assert.Equal(minY, b.Y, 3);
        Assert.Equal(maxY, b.Bottom, 3);
        Assert.Equal(15, b.Bottom, 3); // The quadratic peaks at half its control point's height.
    }

    [Fact]
    public void Flatten_StaysWithinTheTolerance()
    {
        var p0 = new PointD(2, 40);
        var p1 = new PointD(10, -30);
        var p2 = new PointD(60, 90);
        var p3 = new PointD(70, 5);
        VectorPath path = new PathBuilder()
            .MoveTo((float)p0.X, (float)p0.Y)
            .CubicBezierTo(new PointF((float)p1.X, (float)p1.Y), new PointF((float)p2.X, (float)p2.Y), new PointF((float)p3.X, (float)p3.Y))
            .Build();
        foreach (double tolerance in new[] { 0.01, 0.1, 0.5 })
        {
            List<PointD> polyline = path.Flatten(tolerance, null)[0].Points;
            for (int i = 0; i <= 2000; i++)
            {
                double t = i / 2000.0, u = 1 - t;
                var point = new PointD(
                    (u * u * u * p0.X) + (3 * u * u * t * p1.X) + (3 * u * t * t * p2.X) + (t * t * t * p3.X),
                    (u * u * u * p0.Y) + (3 * u * u * t * p1.Y) + (3 * u * t * t * p2.Y) + (t * t * t * p3.Y));
                double best = double.MaxValue;
                for (int k = 1; k < polyline.Count; k++)
                {
                    best = Math.Min(best, VectorPath.DistanceSquaredToSegment(point, polyline[k - 1], polyline[k]));
                }

                Assert.True(Math.Sqrt(best) <= tolerance + 1e-9, $"tolerance {tolerance}: t={t} is {Math.Sqrt(best)} away");
            }
        }

        Assert.True(path.Flatten(0.01, null)[0].Points.Count > 3 * path.Flatten(0.5, null)[0].Points.Count);
    }

    [Fact]
    public void DashFlattening_MeasuresArcLengthAccurately()
    {
        // The ellipse of radii 48 x 36 as four cubics is 265.2794 pixels long (64-point Gauss-Legendre quadrature
        // of |B'(t)| on each cubic, computed independently with NumPy). Dashes are laid out along the polyline
        // flattened at a twentieth of the drawing tolerance, which must measure that length to within 0.01 pixel.
        VectorPath ellipse = new PathBuilder().AddEllipse(new PointF(64, 64), 48, 36).Build();
        FlatFigure figure = ellipse.Flatten(DrawingOptions.DefaultCurveTolerance * 0.05, null)[0];
        double length = 0;
        for (int i = 1; i < figure.Points.Count; i++)
        {
            length += Math.Sqrt(VectorPath.DistanceSquaredToSegment(figure.Points[i], figure.Points[i - 1], figure.Points[i - 1]));
        }

        Assert.InRange(length, 265.2794 - 0.01, 265.2794 + 0.0001);
    }

    [Fact]
    public void ArcTo_FollowsSvgSemantics()
    {
        // Sweep (clockwise on screen) from (0, 0) to (10, 0) on a radius-5 circle passes above the chord.
        VectorPath upper = new PathBuilder().MoveTo(0, 0).ArcTo(5, 5, 0, largeArc: false, sweep: true, new PointF(10, 0)).Build();
        Assert.Equal(-5, upper.Bounds.Y, 3);
        Assert.Equal(0, upper.Bounds.Bottom, 3);
        Assert.Equal(10, upper.Bounds.Right, 3);

        VectorPath lower = new PathBuilder().MoveTo(0, 0).ArcTo(5, 5, 0, largeArc: false, sweep: false, new PointF(10, 0)).Build();
        Assert.Equal(5, lower.Bounds.Bottom, 3);

        // Radii too small to span the endpoints are scaled up to the smallest circle that does.
        VectorPath scaled = new PathBuilder().MoveTo(0, 0).ArcTo(1, 1, 0, false, true, new PointF(10, 0)).Build();
        Assert.Equal(upper.Bounds, scaled.Bounds);

        // Large arc on a radius-10 circle through two points 10 apart bulges far beyond the chord.
        VectorPath large = new PathBuilder().MoveTo(0, 0).ArcTo(10, 10, 0, largeArc: true, sweep: true, new PointF(10, 0)).Build();
        double depth = 10 + Math.Sqrt(75); // Centre 8.66 above the chord, plus the radius.
        Assert.Equal(-depth, large.Bounds.Y, 3);

        // Every flattened point lies on the circle.
        foreach (PointD p in large.Flatten(0.01, null)[0].Points)
        {
            double r = Math.Sqrt(Math.Pow(p.X - 5, 2) + Math.Pow(p.Y + Math.Sqrt(75), 2));
            Assert.InRange(r, 10 - 0.011, 10 + 0.011);
        }
    }

    [Fact]
    public void Shapes_AndTransforms()
    {
        VectorPath star = new PathBuilder().AddStar(new PointF(10, 10), 5, 8, 4).Build();
        List<FlatFigure> figures = star.Flatten(0.1, null);
        Assert.Single(figures);
        Assert.True(figures[0].Closed);
        Assert.Equal(10, figures[0].Points.Count);
        Assert.Equal(10, figures[0].Points[0].X, 6);
        Assert.Equal(2, figures[0].Points[0].Y, 6);

        VectorPath rounded = new PathBuilder().AddRoundedRectangle(new RectangleF(0, 0, 20, 10), 50).Build();
        Assert.Equal(new RectangleF(0, 0, 20, 10), rounded.Bounds);

        VectorPath ellipse = VectorPath.FromEllipse(new RectangleF(2, 3, 10, 6));
        Assert.Equal(new RectangleF(2, 3, 10, 6), ellipse.Bounds);
        VectorPath moved = ellipse.Transform(Matrix3x2.CreateScale(2) * Matrix3x2.CreateTranslation(1, -1));
        Assert.Equal(new RectangleF(5, 5, 20, 12), moved.Bounds);

        Assert.True(new PathBuilder().MoveTo(3, 3).Build().IsEmpty);
        Assert.True(new PathBuilder().AddRectangle(new RectangleF(1, 1, 0, 5)).Build().IsEmpty);
        Assert.Throws<ArgumentException>(() => new PathBuilder().LineTo(float.NaN, 0));
        Assert.Throws<ArgumentException>(() => ellipse.Transform(new Matrix3x2(float.PositiveInfinity, 0, 0, 1, 0, 0)));
    }

    [Fact]
    public void Close_ReturnsToTheFigureStart()
    {
        var builder = new PathBuilder().MoveTo(1, 1).LineTo(5, 1).LineTo(5, 5).Close();
        Assert.Equal(new PointF(1, 1), builder.CurrentPoint);
        builder.LineTo(1, 9);
        List<FlatFigure> figures = builder.Build().Flatten(0.1, null);
        Assert.Equal(2, figures.Count);
        Assert.Equal(new PointD(1, 1), figures[1].Points[0]);
        Assert.False(figures[1].Closed);
    }

    // ----- Strokes -----

    [Fact]
    public void Dashes_FollowThePatternAndOffset()
    {
        VectorPath line = VectorPath.FromLines([new PointF(0, 5.5f), new PointF(20, 5.5f)]);
        byte[,] dashed = Coverage(20, 12, ctx => ctx.Draw(new Pen(Color.White, 1) { DashPattern = [4, 2] }, line));
        byte[,] shifted = Coverage(20, 12, ctx => ctx.Draw(new Pen(Color.White, 1) { DashPattern = [4, 2], DashOffset = 1 }, line));
        for (int x = 0; x < 20; x++)
        {
            Assert.Equal(x % 6 < 4 ? 255 : 0, dashed[x, 5]);
            Assert.Equal((x + 1) % 6 < 4 ? 255 : 0, shifted[x, 5]);
            Assert.Equal(0, dashed[x, 4]);
            Assert.Equal(0, dashed[x, 6]);
        }
    }

    [Fact]
    public void Dashes_OddPatternRepeats_AndZeroLengthDashesDrawDots()
    {
        VectorPath line = VectorPath.FromLines([new PointF(0, 5.5f), new PointF(30, 5.5f)]);

        // [3] is [3, 3]: on 0-3, off 3-6, ...
        byte[,] odd = Coverage(30, 12, ctx => ctx.Draw(new Pen(Color.White, 1) { DashPattern = [3] }, line));
        for (int x = 0; x < 30; x++)
        {
            Assert.Equal(x % 6 < 3 ? 255 : 0, odd[x, 5]);
        }

        // Zero-length dashes every 10 pixels with round caps: discs of radius 2 at x = 0 (half of it inside the
        // image), 10 and 20. The next one would start exactly at the end of the path, where no dash begins.
        byte[,] dots = Coverage(34, 12, ctx => ctx.Draw(new Pen(Color.White, 4) { DashPattern = [0, 10], LineCap = LineCap.Round }, line));
        Assert.Equal(255, dots[10, 5]);
        Assert.Equal(255, dots[20, 5]);
        Assert.Equal(0, dots[15, 5]);
        Assert.Equal(0, dots[30, 5]);
        Assert.InRange(Sum(dots), (2.5 * Math.PI * 4) - 0.3, (2.5 * Math.PI * 4) + 0.1);
    }

    [Fact]
    public void Dashes_OnAClosedFigure_JoinAcrossTheStartPoint()
    {
        // Perimeter 80 starting at (5, 5) clockwise; dash 30 / gap 10 shifted by 10, so a dash runs from 70 to 100:
        // up the left edge, through the start corner and along the top. It must get a miter join there.
        VectorPath square = VectorPath.FromRectangle(new RectangleF(5, 5, 20, 20));
        var pen = new Pen(Color.White, 2) { DashPattern = [30, 10], DashOffset = 10 };
        byte[,] coverage = Coverage(30, 30, ctx => ctx.Draw(pen, square));
        Assert.Equal(255, coverage[4, 4]);      // Outer corner of the miter at the start point.
        Assert.Equal(255, coverage[4, 12]);     // Left edge, inside the dash.
        Assert.Equal(0, coverage[25, 4]);       // The first dash ends with a butt cap at the top-right corner.
        Assert.Equal(255, coverage[24, 4]);
        Assert.Equal(0, coverage[25, 8]);       // Gap from 20 to 30 down the right edge.
        Assert.Equal(255, coverage[25, 16]);
    }

    [Theory]
    [InlineData(LineCap.Butt, 0.0)]
    [InlineData(LineCap.Square, 1.0)]
    public void Caps_ButtAndSquare_AreExact(LineCap cap, double extension)
    {
        VectorPath line = VectorPath.FromLines([new PointF(4, 5.5f), new PointF(10, 5.5f)]);
        byte[,] coverage = Coverage(16, 12, ctx => ctx.Draw(new Pen(Color.White, 2) { LineCap = cap }, line));
        for (int x = 0; x < 16; x++)
        {
            double left = Math.Clamp(x + 1 - (4 - extension), 0, 1);
            double right = Math.Clamp((10 + extension) - x, 0, 1);
            double covered = Math.Min(left, right);
            byte expected = (byte)Math.Round(255 * covered, MidpointRounding.AwayFromZero);

            // The band is y in [4.5, 6.5]: rows 4 and 6 are half covered, row 5 fully.
            Assert.Equal(expected, coverage[x, 5]);
            Assert.InRange(coverage[x, 4], (expected / 2) - 1, (expected / 2) + 1);
        }
    }

    [Fact]
    public void Caps_Round_AddHalfDiscs()
    {
        VectorPath line = VectorPath.FromLines([new PointF(6, 6), new PointF(14, 6)]);
        double butt = Sum(Coverage(20, 12, ctx => ctx.Draw(new Pen(Color.White, 4), line)));
        double round = Sum(Coverage(20, 12, ctx => ctx.Draw(new Pen(Color.White, 4) { LineCap = LineCap.Round }, line)));
        Assert.Equal(32, butt, 1);
        Assert.InRange(round - butt, (Math.PI * 4) - 0.2, (Math.PI * 4) + 0.2); // Two half discs of radius 2.
    }

    [Theory]
    [InlineData(LineCap.Butt, 0.0)]
    [InlineData(LineCap.Round, 28.274)]
    [InlineData(LineCap.Square, 36.0)]
    public void ZeroLengthSubpath_DrawsItsCap(LineCap cap, double area)
    {
        VectorPath dot = new PathBuilder().MoveTo(10, 10).LineTo(10, 10).Build();
        byte[,] coverage = Coverage(20, 20, ctx => ctx.Draw(new Pen(Color.White, 6) { LineCap = cap }, dot));
        Assert.InRange(Sum(coverage), area - 0.3, area + 0.3);
        if (cap == LineCap.Square)
        {
            Assert.Equal(255, coverage[7, 7]);
            Assert.Equal(255, coverage[12, 12]);
            Assert.Equal(0, coverage[13, 13]);
        }
    }

    [Fact]
    public void Joins_MiterBevelRound()
    {
        // A right-angle turn at (10, 10), width 4: the outer corner is the square [10, 12] x [10, 12].
        VectorPath corner = VectorPath.FromLines([new PointF(2, 10), new PointF(10, 10), new PointF(10, 2)]);
        byte[,] miter = Coverage(16, 16, ctx => ctx.Draw(new Pen(Color.White, 4), corner));
        byte[,] bevel = Coverage(16, 16, ctx => ctx.Draw(new Pen(Color.White, 4) { LineJoin = LineJoin.Bevel }, corner));
        byte[,] round = Coverage(16, 16, ctx => ctx.Draw(new Pen(Color.White, 4) { LineJoin = LineJoin.Round }, corner));

        Assert.Equal(255, miter[11, 11]);
        Assert.Equal(255, miter[10, 11]);

        // The bevel cuts the corner along x + y = 22.
        Assert.Equal(255, bevel[10, 10]);
        Assert.InRange(bevel[11, 10], 127, 129);
        Assert.InRange(bevel[10, 11], 127, 129);
        Assert.Equal(0, bevel[11, 11]);

        // The round join is a quarter disc of radius 2: about 31% of pixel (11, 11).
        Assert.Equal(255, round[10, 10]);
        Assert.InRange(round[11, 11], 70, 92);
        Assert.InRange(Sum(miter) - Sum(round), 4 - Math.PI - 0.1, 4 - Math.PI + 0.1);
        Assert.InRange(Sum(miter) - Sum(bevel), 2 - 0.05, 2 + 0.05);

        // Inner side and the rest of the stroke are the same for all three joins.
        Assert.Equal(miter[5, 9], round[5, 9]);
        Assert.Equal(miter[9, 5], bevel[9, 5]);
    }

    [Fact]
    public void MiterLimit_BevelsSharpCorners()
    {
        // The turn needs a miter ratio of about 4.12: bevelled at the default limit 4, mitered at 5.
        VectorPath sharp = VectorPath.FromLines([new PointF(0, 20), new PointF(20, 15), new PointF(0, 10)]);
        byte[,] bevelled = Coverage(32, 30, ctx => ctx.Draw(new Pen(Color.White, 4), sharp));
        byte[,] mitered = Coverage(32, 30, ctx => ctx.Draw(new Pen(Color.White, 4) { MiterLimit = 5 }, sharp));
        Assert.Equal(0, bevelled[25, 14] + bevelled[25, 15]);
        Assert.True(mitered[25, 14] + mitered[25, 15] > 100);
        Assert.Equal(0, mitered[29, 14] + mitered[29, 15]); // The tip ends at x = 28.24.
    }

    [Fact]
    public void TightCurve_StrokeHasNoHoles()
    {
        // A circle of radius 2 stroked 8 wide covers the whole disc of radius 6, centre included.
        VectorPath circle = new PathBuilder().AddCircle(new PointF(16, 16), 2).Build();
        byte[,] coverage = Coverage(32, 32, ctx => ctx.Draw(new Pen(Color.White, 8), circle));
        for (int y = 11; y < 21; y++)
        {
            for (int x = 11; x < 21; x++)
            {
                if (Math.Pow(x + 0.5 - 16, 2) + Math.Pow(y + 0.5 - 16, 2) < 5 * 5)
                {
                    Assert.Equal(255, coverage[x, y]);
                }
            }
        }

        // The circle is flattened to within 0.1 pixel (inscribed chords), so the outline may sit up to that far inside.
        Assert.InRange(Sum(coverage), Math.PI * 5.9 * 5.9, (Math.PI * 36) + 0.5);
    }

    [Fact]
    public void Cusp_AndUTurns_StayCovered()
    {
        // A cubic with a cusp and a polyline that doubles back: overlapping stroke pieces must be painted once.
        VectorPath cusp = new PathBuilder().MoveTo(4, 20).CubicBezierTo(new PointF(30, 0), new PointF(-10, 0), new PointF(20, 20)).Build();
        using var image = new Image<Rgba32>(30, 26, Rgba32.Transparent);
        image.Mutate(ctx => ctx.Draw(new Pen(new Color(255, 255, 255, 128), 3), cusp));
        foreach (Rgba32 p in image.Frames.RootFrame.PixelSpanForTests())
        {
            Assert.True(p.A <= 128, "a semi-transparent stroke never darkens where its pieces overlap");
        }

        VectorPath back = VectorPath.FromLines([new PointF(2, 5), new PointF(15, 5), new PointF(6, 5)]);
        byte[,] round = Coverage(20, 10, ctx => ctx.Draw(new Pen(Color.White, 2) { LineJoin = LineJoin.Round }, back));
        byte[,] miter = Coverage(20, 10, ctx => ctx.Draw(new Pen(Color.White, 2), back));
        Assert.Equal(0, miter[15, 4]);              // A U-turn has no outer corner to miter...
        Assert.True(round[15, 4] + round[15, 5] > 150); // ...but a round join caps it with a half disc.
    }

    [Fact]
    public void StyledRectangleAndEllipsePens_StayInsideTheBounds()
    {
        var pen = new Pen(Color.White, 3) { LineJoin = LineJoin.Round, DashPattern = [5, 2] };
        byte[,] rect = Coverage(30, 30, ctx => ctx.DrawRectangle(pen, new RectangleF(5, 5, 20, 15)));
        byte[,] ellipse = Coverage(30, 30, ctx => ctx.DrawEllipse(pen, new RectangleF(5, 5, 20, 15)));
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                bool inside = x >= 5 && x < 25 && y >= 5 && y < 20;
                if (!inside)
                {
                    Assert.Equal(0, rect[x, y]);
                    Assert.Equal(0, ellipse[x, y]);
                }
            }
        }

        Assert.True(rect[5, 5] < 255);   // Round join: the outer corner is rounded off.
        Assert.Equal(255, rect[6, 6]);
        Assert.Equal(0, rect[12, 12]);
    }

    [Fact]
    public void BrushPens_PaintTheStrokeWithTheBrush()
    {
        var gradient = new LinearGradientBrush(new PointF(0, 0), new PointF(20, 0), Color.Red, Color.Blue);
        using var image = new Image<Rgba32>(20, 10, Rgba32.Transparent);
        image.Mutate(ctx => ctx.Draw(gradient, 4, VectorPath.FromLines([new PointF(0, 5), new PointF(20, 5)])));
        Assert.True(image[1, 5].R > 230 && image[1, 5].B < 25);
        Assert.True(image[18, 5].B > 200 && image[18, 5].R < 50);
        Assert.Equal(Rgba32.Transparent, image[10, 0]);
    }

    [Fact]
    public void NonAntialiasedPaths_ProduceOnlyFullOrNoCoverage()
    {
        VectorPath path = new PathBuilder().AddEllipse(new PointF(15.3f, 14.8f), 11.2f, 8.4f).AddStar(new PointF(15, 15), 7, 12, 5).Build();
        byte[,] fill = Coverage(30, 30, ctx => ctx.Fill(Color.White, path, new DrawingOptions { Antialias = false, FillRule = FillRule.EvenOdd }));
        byte[,] stroke = Coverage(30, 30, ctx => ctx.Draw(new Pen(Color.White, 1.7f) { LineJoin = LineJoin.Round }, path, NoAa));
        Assert.All(fill.Cast<byte>(), v => Assert.True(v == 0 || v == 255));
        Assert.All(stroke.Cast<byte>(), v => Assert.True(v == 0 || v == 255));
        Assert.Contains((byte)255, fill.Cast<byte>());
        Assert.Contains((byte)255, stroke.Cast<byte>());
    }

    // ----- Clipping -----

    [Fact]
    public void ClipPath_LimitsEveryDrawingOperation()
    {
        var options = new DrawingOptions { ClipPath = VectorPath.FromRectangle(new RectangleF(5, 5, 10, 10)) };
        using var image = new Image<Rgb24>(20, 20, new Rgb24(0, 0, 0));
        image.Mutate(ctx => ctx
            .FillRectangle(Color.White, new RectangleF(0, 0, 20, 20), options)
            .DrawLine(Color.Red, 3, new PointF(0, 2), new PointF(20, 2), options));
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                bool inside = x >= 5 && x < 15 && y >= 5 && y < 15;
                Assert.Equal(inside ? new Rgb24(255, 255, 255) : new Rgb24(0, 0, 0), image[x, y]);
            }
        }

        using var text = new Image<Rgb24>(40, 12, new Rgb24(0, 0, 0));
        var textClip = new DrawingOptions { ClipPath = VectorPath.FromRectangle(new RectangleF(0, 0, 12, 12)) };
        text.Mutate(ctx => ctx.Fill(Color.White, textClip).DrawText("HHHH", Color.Red, new PointF(0, 2), TextOptions.Default, textClip));
        for (int y = 0; y < 12; y++)
        {
            for (int x = 12; x < 40; x++)
            {
                Assert.Equal(new Rgb24(0, 0, 0), text[x, y]);
            }
        }

        Assert.Contains(Enumerable.Range(0, 12).SelectMany(y => Enumerable.Range(0, 12).Select(x => text[x, y])), p => p == new Rgb24(255, 0, 0));
    }

    [Fact]
    public void ClipPath_AntialiasedEdgeEqualsTheFilledPath()
    {
        VectorPath circle = VectorPath.FromEllipse(new RectangleF(2.5f, 3.2f, 21, 17));
        using var expected = new Image<Rgb24>(26, 24, new Rgb24(0, 0, 0));
        using Image<Rgb24> clipped = expected.Clone();
        expected.Mutate(ctx => ctx.Fill(Color.White, circle));
        clipped.Mutate(ctx => ctx.Fill(Color.White, new DrawingOptions { ClipPath = circle }));
        AssertSamePixels(expected, clipped);

        using Image<Rgb24> gradient = expected.Clone();
        gradient.Mutate(ctx => ctx.Fill(Color.Black).Fill(new LinearGradientBrush(new PointF(0, 0), new PointF(1, 0), Color.White, Color.White), new DrawingOptions { ClipPath = circle }));
        AssertSamePixels(expected, gradient);
    }

    [Fact]
    public void ClipPath_EvenOddRule()
    {
        VectorPath ring = new PathBuilder().AddRectangle(new RectangleF(2, 2, 16, 16)).AddRectangle(new RectangleF(6, 6, 8, 8)).Build();
        byte[,] coverage = Coverage(20, 20, ctx => ctx.Fill(Color.White, new DrawingOptions { ClipPath = ring, ClipFillRule = FillRule.EvenOdd }));
        Assert.Equal(255, coverage[3, 3]);
        Assert.Equal(0, coverage[10, 10]);
        Assert.Equal(0, coverage[19, 19]);
    }

    [Fact]
    public void ClipScope_AppliesAnyOperationInsideThePathOnly()
    {
        using var image = new Image<Rgb24>(16, 16);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                image[x, y] = new Rgb24((byte)(x * 16), (byte)(y * 16), 77);
            }
        }

        using Image<Rgb24> original = image.Clone();
        image.Mutate(ctx => ctx.Clip(new RectangleF(4, 3, 6, 8), inner => inner.Invert()));
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                Rgb24 o = original[x, y];
                bool inside = x >= 4 && x < 10 && y >= 3 && y < 11;
                Rgb24 expected = inside ? new Rgb24((byte)(255 - o.R), (byte)(255 - o.G), (byte)(255 - o.B)) : o;
                Assert.Equal(expected, image[x, y]);
            }
        }

        // A clipped fill is exactly the fill of the clip path.
        VectorPath circle = VectorPath.FromEllipse(new RectangleF(1.5f, 2.5f, 12, 11));
        using var a = new Image<Rgba32>(16, 16, new Rgba32(0, 0, 255, 255));
        using Image<Rgba32> b = a.Clone();
        a.Mutate(ctx => ctx.Fill(Color.Yellow, circle));
        b.Mutate(ctx => ctx.Clip(circle, inner => inner.Fill(Color.Yellow)));
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                Assert.InRange(a[x, y].R - b[x, y].R, -1, 1);
                Assert.InRange(a[x, y].B - b[x, y].B, -1, 1);
            }
        }
    }

    [Fact]
    public void ClipScope_RejectsSizeChanges_WithoutTouchingTheImage()
    {
        using var image = new Image<Rgb24>(10, 10, new Rgb24(1, 2, 3));
        Assert.Throws<InvalidOperationException>(() =>
            image.Mutate(ctx => ctx.Clip(new RectangleF(0, 0, 5, 5), inner => inner.Fill(Color.Red).Resize(5, 5))));
        Assert.Equal(10, image.Width);
        Assert.Equal(new Rgb24(1, 2, 3), image[2, 2]);
    }

    [Fact]
    public void Cancellation_StopsPathRendering_AndAClipLeavesTheImageUntouched()
    {
        using var cancellation = new CancellationTokenSource();
        using var image = new Image<Rgb24>(64, 64, new Rgb24(9, 9, 9));
        VectorPath circle = VectorPath.FromEllipse(new RectangleF(4, 4, 56, 56));
        var gradient = new LinearGradientBrush(new PointF(0, 0), new PointF(64, 0), Color.Red, Color.Blue);
        Assert.ThrowsAny<OperationCanceledException>(() => image.Mutate(
            new ImageExecutionOptions { CancellationToken = cancellation.Token },
            ctx => ctx.Clip(new RectangleF(0, 0, 32, 64), inner =>
            {
                cancellation.Cancel();
                inner.Fill(gradient, circle);
            })));
        Assert.All(Enumerable.Range(0, 64 * 64), i => Assert.Equal(new Rgb24(9, 9, 9), image[i % 64, i / 64]));
    }

    [Fact]
    public void Operations_ApplyToEveryFrame()
    {
        using var image = new Image<Rgba32>(8, 8, Rgba32.Transparent);
        using var second = new Image<Rgba32>(8, 8, Rgba32.Transparent);
        image.Frames.AddFrame(second.Frames.RootFrame);
        image.Mutate(ctx => ctx
            .Fill(new RadialGradientBrush(new PointF(4, 4), 4, Color.Red, Color.Blue), VectorPath.FromEllipse(new RectangleF(0, 0, 8, 8)))
            .Clip(new RectangleF(0, 0, 4, 8), inner => inner.Fill(Color.Lime)));
        foreach (ImageFrame<Rgba32> frame in image.Frames)
        {
            Assert.Equal(new Rgba32(0, 255, 0, 255), frame[1, 4]);
            Assert.True(frame[5, 4].R > 100);
            Assert.Equal(0, frame[7, 0].A);
        }
    }

    // ----- Argument validation -----

    [Fact]
    public void Pen_ValidatesItsStyle()
    {
        Assert.Throws<ArgumentException>(() => new Pen(Color.Red) { DashPattern = [-1, 2] });
        Assert.Throws<ArgumentException>(() => new Pen(Color.Red) { DashPattern = [0, 0] });
        Assert.Throws<ArgumentException>(() => new Pen(Color.Red) { DashPattern = [float.NaN] });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(Color.Red) { MiterLimit = float.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(Color.Red) { Thickness = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(Color.Red) { DashOffset = float.PositiveInfinity });
        Assert.Throws<ArgumentNullException>(() => new Pen((Brush)null!));

        var pen = new Pen(Color.Red, 2) { DashPattern = [3, 1], LineCap = LineCap.Round, MiterLimit = 0.5f };
        Assert.Equal(1f, pen.MiterLimit);
        Assert.Equal(new[] { 3f, 1f }, pen.DashPattern);
        Assert.Equal(pen, new Pen(Color.Red, 2) { DashPattern = [3, 1], LineCap = LineCap.Round, MiterLimit = 1 });
        Assert.NotEqual(pen, new Pen(Color.Red, 2) { DashPattern = [3, 2], LineCap = LineCap.Round, MiterLimit = 1 });
        Assert.Equal(new Pen(Color.Red, 2).GetHashCode(), new Pen(Color.Red, 2) { MiterLimit = 4 }.GetHashCode());
        Assert.Equal("Pen [ Color=" + Color.Red + ", Thickness=2 ]", new Pen(Color.Red, 2).ToString());
        Assert.Equal(4f, default(Pen).MiterLimit);
        Assert.Empty(default(Pen).DashPattern);
    }

    [Fact]
    public void Brushes_ValidateTheirArguments()
    {
        Assert.Throws<ArgumentException>(() => new LinearGradientBrush(new PointF(0, 0), new PointF(1, 0)));
        Assert.Throws<ArgumentException>(() => new LinearGradientBrush(new PointF(float.NaN, 0), new PointF(1, 0), Color.Red, Color.Blue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadialGradientBrush(new PointF(0, 0), -1, Color.Red, Color.Blue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColorStop(float.NaN, Color.Red));
        Assert.Throws<ArgumentException>(() => new SolidBrush(Color.Red) { Transform = new Matrix3x2(float.NaN, 0, 0, 1, 0, 0) });

        using var image = new Image<Rgba32>(4, 4);
        Assert.Throws<ArgumentNullException>(() => image.Mutate(ctx => ctx.Fill((Brush)null!)));
        Assert.Throws<ArgumentNullException>(() => image.Mutate(ctx => ctx.Fill(Color.Red, (VectorPath)null!)));
        Assert.Throws<ArgumentException>(() => image.Mutate(ctx => ctx.FillPolygon(new SolidBrush(Color.Red), new PointF(0, 0), new PointF(float.NaN, 1), new PointF(2, 2))));
    }

    [Fact]
    public void DegenerateGradients_PaintTheLastStop()
    {
        using var image = new Image<Rgba32>(4, 4, Rgba32.Transparent);
        image.Mutate(ctx => ctx.Fill(new LinearGradientBrush(new PointF(2, 2), new PointF(2, 2), Color.Red, Color.Blue)));
        Assert.Equal(new Rgba32(0, 0, 255, 255), image[0, 0]);
        image.Mutate(ctx => ctx.Fill(new RadialGradientBrush(new PointF(2, 2), 0, Color.Red, Color.Lime)));
        Assert.Equal(new Rgba32(0, 255, 0, 255), image[3, 3]);
    }

    [Fact]
    public void AbsurdDashPatterns_FailFast()
    {
        using var image = new Image<Rgba32>(8, 8);
        VectorPath longLine = VectorPath.FromLines([new PointF(0, 4), new PointF(1e7f, 4)]);
        Assert.Throws<ArgumentException>(() => image.Mutate(ctx => ctx.Draw(new Pen(Color.Red, 1) { DashPattern = [0.5f, 0.5f] }, longLine)));
    }
}

/// <summary>Test-only access to a frame's pixels in one span.</summary>
internal static class FrameTestExtensions
{
    public static Span<TPixel> PixelSpanForTests<TPixel>(this ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
        => frame.PixelSpan;
}
