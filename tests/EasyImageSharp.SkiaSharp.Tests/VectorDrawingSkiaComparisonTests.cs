using EasyImageSharp.Drawing;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.SkiaSharp.Tests;

/// <summary>
/// Renders the same vector scenes with EasyImageSharp's managed rasteriser and with Skia, an independent
/// native implementation, and compares the results.
/// </summary>
/// <remarks>
/// <para>
/// The two renderers agree on the geometry (fill rules, stroke outlines, joins, caps, dashes, gradient offsets)
/// but not on how an edge pixel's coverage is estimated: EasyImageSharp samples 16 sub-scanlines with exact
/// horizontal coverage and flattens curves to within 0.1 pixel, Skia uses its own analytic or supersampled
/// scan converters and curve subdivision. Edge pixels therefore differ by a few levels, rarely by more, and
/// interior pixels must be identical up to gradient rounding.
/// </para>
/// <para>
/// The documented tolerance, checked for every scene on an opaque white background, is: the mean absolute
/// difference over all channels of all pixels is at most 0.6 levels; no more than 1.5% of pixels differ by
/// more than 16 levels in any channel; and no pixel differs by more than 96 levels (which only a missing or
/// extra piece of geometry, not an anti-aliasing difference, would exceed). Gradient scenes additionally
/// require every pixel at least two pixels away from any edge to be within 2 levels.
/// </para>
/// </remarks>
public class VectorDrawingSkiaComparisonTests
{
    private const int Size = 128;

    private readonly ITestOutputHelper output;

    public VectorDrawingSkiaComparisonTests(ITestOutputHelper output) => this.output = output;

    // ----- Scenes: each one is described once for each renderer -----

    private static VectorPath CurvedBlob() => new PathBuilder()
        .MoveTo(20, 64)
        .CubicBezierTo(new PointF(20, 10), new PointF(100, 0), new PointF(108, 50))
        .QuadraticBezierTo(new PointF(118, 110), new PointF(64, 112))
        .CubicBezierTo(new PointF(30, 114), new PointF(60, 80), new PointF(20, 64))
        .Close()
        .Build();

    private static SKPath SkCurvedBlob()
    {
        using var b = new SKPathBuilder();
        b.MoveTo(20, 64);
        b.CubicTo(20, 10, 100, 0, 108, 50);
        b.QuadTo(118, 110, 64, 112);
        b.CubicTo(30, 114, 60, 80, 20, 64);
        b.Close();
        return b.Detach();
    }

    private static PointF[] StarPoints()
    {
        var star = new PointF[7];
        for (int k = 0; k < 7; k++)
        {
            double angle = (-90 + (k * 360.0 * 3 / 7)) * Math.PI / 180;
            star[k] = new PointF((float)(64 + (54 * Math.Cos(angle))), (float)(64 + (54 * Math.Sin(angle))));
        }

        return star;
    }

    private static VectorPath StarWithHole()
    {
        var builder = new PathBuilder().AddPolygon(StarPoints());
        builder.AddEllipse(new PointF(64, 64), 18, 12);
        return builder.Build();
    }

    private static SKPath SkStarWithHole(SKPathFillType fillType)
    {
        using var b = new SKPathBuilder();
        PointF[] star = StarPoints();
        b.MoveTo(star[0].X, star[0].Y);
        for (int i = 1; i < star.Length; i++)
        {
            b.LineTo(star[i].X, star[i].Y);
        }

        b.Close();
        b.AddOval(new SKRect(64 - 18, 64 - 12, 64 + 18, 64 + 12));
        b.FillType = fillType;
        return b.Detach();
    }

    private static VectorPath OpenCurve() => new PathBuilder()
        .MoveTo(16, 100)
        .LineTo(40, 30)
        .CubicBezierTo(new PointF(60, 0), new PointF(80, 120), new PointF(96, 40))
        .LineTo(112, 104)
        .LineTo(70, 90)
        .Build();

    private static SKPath SkOpenCurve()
    {
        using var b = new SKPathBuilder();
        b.MoveTo(16, 100);
        b.LineTo(40, 30);
        b.CubicTo(60, 0, 80, 120, 96, 40);
        b.LineTo(112, 104);
        b.LineTo(70, 90);
        return b.Detach();
    }

    private static VectorPath Zigzag() => VectorPath.FromLines(
        [new PointF(10, 110), new PointF(30, 20), new PointF(50, 100), new PointF(64, 30), new PointF(80, 106), new PointF(118, 14)]);

    private static SKPath SkZigzag()
    {
        using var b = new SKPathBuilder();
        b.MoveTo(10, 110);
        b.LineTo(30, 20);
        b.LineTo(50, 100);
        b.LineTo(64, 30);
        b.LineTo(80, 106);
        b.LineTo(118, 14);
        return b.Detach();
    }

    private static VectorPath DashedPolylines() => new PathBuilder()
        .AddPolygon([new PointF(20, 20), new PointF(108, 30), new PointF(90, 70), new PointF(30, 60)])
        .AddLines([new PointF(10, 120), new PointF(40, 80), new PointF(70, 116), new PointF(118, 84)])
        .Build();

    private static SKPath SkDashedPolylines()
    {
        using var b = new SKPathBuilder();
        b.MoveTo(20, 20);
        b.LineTo(108, 30);
        b.LineTo(90, 70);
        b.LineTo(30, 60);
        b.Close();
        b.MoveTo(10, 120);
        b.LineTo(40, 80);
        b.LineTo(70, 116);
        b.LineTo(118, 84);
        return b.Detach();
    }

    private static VectorPath DashedEllipse() => new PathBuilder().AddEllipse(new PointF(64, 64), 48, 36).Build();

    private static SKPath SkDashedEllipse()
    {
        using var b = new SKPathBuilder();
        AddEllipseLikeEasyImageSharp(b, 64, 64, 48, 36);
        return b.Detach();
    }

    /// <summary>
    /// The ellipse as EasyImageSharp builds it (four cubics, clockwise from the rightmost point), so that dash
    /// positions, which depend on where a figure starts and which way it runs, are the same for both renderers.
    /// </summary>
    private static void AddEllipseLikeEasyImageSharp(SKPathBuilder b, float cx, float cy, float rx, float ry)
    {
        const float k = 0.55228474983f;
        b.MoveTo(cx + rx, cy);
        b.CubicTo(cx + rx, cy + (ry * k), cx + (rx * k), cy + ry, cx, cy + ry);
        b.CubicTo(cx - (rx * k), cy + ry, cx - rx, cy + (ry * k), cx - rx, cy);
        b.CubicTo(cx - rx, cy - (ry * k), cx - (rx * k), cy - ry, cx, cy - ry);
        b.CubicTo(cx + (rx * k), cy - ry, cx + rx, cy - (ry * k), cx + rx, cy);
        b.Close();
    }

    // ----- Tests -----

    [Fact]
    public void CurvedFill_MatchesSkia()
        => this.Compare(
            "curved fill",
            ctx => ctx.Fill(new Color(30, 90, 200), CurvedBlob()),
            canvas =>
            {
                using SKPath path = SkCurvedBlob();
                using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(30, 90, 200) };
                canvas.DrawPath(path, paint);
            });

    [Theory]
    [InlineData(FillRule.NonZero)]
    [InlineData(FillRule.EvenOdd)]
    public void SelfIntersectingFill_MatchesSkia(FillRule rule)
        => this.Compare(
            "star " + rule,
            ctx => ctx.Fill(new SolidBrush(new Color(200, 30, 60)), StarWithHole(), new DrawingOptions { FillRule = rule }),
            canvas =>
            {
                using SKPath path = SkStarWithHole(rule == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding);
                using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(200, 30, 60) };
                canvas.DrawPath(path, paint);
            });

    [Theory]
    [InlineData(LineJoin.Round, LineCap.Round)]
    [InlineData(LineJoin.Miter, LineCap.Butt)]
    [InlineData(LineJoin.Bevel, LineCap.Square)]
    public void CurvedStroke_MatchesSkia(LineJoin join, LineCap cap)
        => this.Compare(
            $"stroke {join}/{cap}",
            ctx => ctx.Draw(new Pen(new Color(20, 120, 40), 7) { LineJoin = join, LineCap = cap }, OpenCurve()),
            canvas =>
            {
                using SKPath path = SkOpenCurve();
                using var paint = StrokePaint(new SKColor(20, 120, 40), 7, join, cap, 4);
                canvas.DrawPath(path, paint);
            });

    [Theory]
    [InlineData(4f)]
    [InlineData(10f)]
    public void SharpPolyline_MiterLimit_MatchesSkia(float miterLimit)
        => this.Compare(
            $"zigzag miter {miterLimit}",
            ctx => ctx.Draw(new Pen(Color.Black, 6) { MiterLimit = miterLimit }, Zigzag()),
            canvas =>
            {
                using SKPath path = SkZigzag();
                using var paint = StrokePaint(SKColors.Black, 6, LineJoin.Miter, LineCap.Butt, miterLimit);
                canvas.DrawPath(path, paint);
            });

    [Theory]
    [InlineData(LineCap.Butt, LineJoin.Miter)]
    [InlineData(LineCap.Round, LineJoin.Round)]
    [InlineData(LineCap.Square, LineJoin.Bevel)]
    public void DashedPolylines_MatchSkia(LineCap cap, LineJoin join)
        => this.Compare(
            $"dashed lines {cap}/{join}",
            ctx => ctx.Draw(new Pen(new Color(150, 40, 160), 4) { DashPattern = [14, 6, 2, 6], DashOffset = 5, LineCap = cap, LineJoin = join }, DashedPolylines()),
            canvas =>
            {
                using SKPath path = SkDashedPolylines();
                using var paint = StrokePaint(new SKColor(150, 40, 160), 4, join, cap, 4);
                using var dash = SKPathEffect.CreateDash([14, 6, 2, 6], 5);
                paint.PathEffect = dash;
                canvas.DrawPath(path, paint);
            });

    /// <summary>
    /// Dashes along a curve depend on how its arc length is measured. Skia measures this ellipse (four cubics) as
    /// 264.85 pixels long, 0.43 pixel (0.16%) less than its true length of 265.28 pixels found by Gauss-Legendre
    /// integration, which EasyImageSharp's dash flattening reproduces to within 0.01 pixel (see the core test
    /// <c>DashFlattening_MeasuresArcLengthAccurately</c>). Dashes late in the figure are therefore shifted by up to
    /// 0.43 pixel between the two renderers, and this scene allows 3% of pixels to differ by more than 16 levels and
    /// single pixels by up to 160 (measured: 128, about half a pixel of dash end); the mean bound is unchanged.
    /// </summary>
    [Theory]
    [InlineData(LineCap.Butt)]
    [InlineData(LineCap.Round)]
    public void DashedEllipse_MatchesSkia_UpToItsArcLengthMeasure(LineCap cap)
        => this.Compare(
            "dashed ellipse " + cap,
            ctx => ctx.Draw(new Pen(new Color(150, 40, 160), 4) { DashPattern = [14, 6, 2, 6], DashOffset = 5, LineCap = cap }, DashedEllipse()),
            canvas =>
            {
                using SKPath path = SkDashedEllipse();
                using var paint = StrokePaint(new SKColor(150, 40, 160), 4, LineJoin.Miter, cap, 4);
                using var dash = SKPathEffect.CreateDash([14, 6, 2, 6], 5);
                paint.PathEffect = dash;
                canvas.DrawPath(path, paint);
            },
            maxOver16: 0.03,
            maxSingle: 160);

    [Fact]
    public void LinearGradient_MatchesSkia()
    {
        var brush = new LinearGradientBrush(
            new PointF(10, 20),
            new PointF(110, 100),
            new ColorStop(0, new Color(255, 0, 0)),
            new ColorStop(0.4f, new Color(255, 220, 0)),
            new ColorStop(1, new Color(0, 60, 255)));
        this.Compare(
            "linear gradient",
            ctx => ctx.Fill(brush, CurvedBlob()),
            canvas =>
            {
                using SKPath path = SkCurvedBlob();
                using var shader = SKShader.CreateLinearGradient(
                    new SKPoint(10, 20),
                    new SKPoint(110, 100),
                    [new SKColor(255, 0, 0), new SKColor(255, 220, 0), new SKColor(0, 60, 255)],
                    [0, 0.4f, 1],
                    SKShaderTileMode.Clamp);
                using var paint = new SKPaint { IsAntialias = true, Shader = shader };
                canvas.DrawPath(path, paint);
            },
            gradient: true);
    }

    [Theory]
    [InlineData(WrapMode.Clamp)]
    [InlineData(WrapMode.Repeat)]
    [InlineData(WrapMode.Reflect)]
    public void RadialGradientWithFocus_MatchesSkia(WrapMode wrap)
    {
        var brush = new RadialGradientBrush(new PointF(64, 64), 40, new PointF(50, 52), new ColorStop(0, Color.White), new ColorStop(1, new Color(0, 40, 120)))
        {
            WrapMode = wrap,
        };
        SKShaderTileMode mode = wrap switch
        {
            WrapMode.Repeat => SKShaderTileMode.Repeat,
            WrapMode.Reflect => SKShaderTileMode.Mirror,
            _ => SKShaderTileMode.Clamp,
        };
        this.Compare(
            "radial " + wrap,
            ctx => ctx.Fill(brush),
            canvas =>
            {
                using var shader = SKShader.CreateTwoPointConicalGradient(
                    new SKPoint(50, 52), 0, new SKPoint(64, 64), 40, [SKColors.White, new SKColor(0, 40, 120)], [0, 1], mode);
                using var paint = new SKPaint { Shader = shader };
                canvas.DrawRect(new SKRect(0, 0, Size, Size), paint);
            },
            gradient: true);
    }

    [Fact]
    public void ConicGradient_MatchesSkia()
    {
        var brush = new ConicGradientBrush(new PointF(64, 60), 0, new ColorStop(0, Color.Red), new ColorStop(0.5f, Color.Lime), new ColorStop(1, Color.Red));
        this.Compare(
            "conic",
            ctx => ctx.Fill(brush),
            canvas =>
            {
                using var shader = SKShader.CreateSweepGradient(new SKPoint(64, 60), [SKColors.Red, SKColors.Lime, SKColors.Red], [0, 0.5f, 1]);
                using var paint = new SKPaint { Shader = shader };
                canvas.DrawRect(new SKRect(0, 0, Size, Size), paint);
            },
            gradient: true);
    }

    [Fact]
    public void ClipPath_MatchesSkia()
    {
        VectorPath clip = StarWithHole();
        this.Compare(
            "clip",
            ctx => ctx
                .Fill(new Color(255, 140, 0), new DrawingOptions { ClipPath = clip, ClipFillRule = FillRule.EvenOdd })
                .Clip(VectorPath.FromEllipse(new RectangleF(20, 20, 88, 88)), inner => inner.Draw(new Pen(Color.Black, 5), Zigzag())),
            canvas =>
            {
                using SKPath star = SkStarWithHole(SKPathFillType.EvenOdd);
                using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(255, 140, 0) };
                canvas.Save();
                canvas.ClipPath(star, SKClipOperation.Intersect, antialias: true);
                canvas.DrawRect(new SKRect(0, 0, Size, Size), fill);
                canvas.Restore();

                using var b = new SKPathBuilder();
                AddEllipseLikeEasyImageSharp(b, 64, 64, 44, 44);
                using SKPath circle = b.Detach();
                canvas.Save();
                canvas.ClipPath(circle, SKClipOperation.Intersect, antialias: true);
                using SKPath zigzag = SkZigzag();
                using var stroke = StrokePaint(SKColors.Black, 5, LineJoin.Miter, LineCap.Butt, 4);
                canvas.DrawPath(zigzag, stroke);
                canvas.Restore();
            });
    }

    [Fact]
    public void SemiTransparentStroke_PaintsOverlapsOnce_LikeSkia()
        => this.Compare(
            "translucent stroke",
            ctx => ctx.Draw(new Pen(new Color(0, 0, 0, 128), 12) { LineJoin = LineJoin.Round }, OpenCurve()),
            canvas =>
            {
                using SKPath path = SkOpenCurve();
                using var paint = StrokePaint(new SKColor(0, 0, 0, 128), 12, LineJoin.Round, LineCap.Butt, 4);
                canvas.DrawPath(path, paint);
            });

    // ----- Comparison -----

    private static SKPaint StrokePaint(SKColor color, float width, LineJoin join, LineCap cap, float miterLimit) => new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        Color = color,
        StrokeWidth = width,
        StrokeMiter = miterLimit,
        StrokeJoin = join switch
        {
            LineJoin.Round => SKStrokeJoin.Round,
            LineJoin.Bevel => SKStrokeJoin.Bevel,
            _ => SKStrokeJoin.Miter,
        },
        StrokeCap = cap switch
        {
            LineCap.Round => SKStrokeCap.Round,
            LineCap.Square => SKStrokeCap.Square,
            _ => SKStrokeCap.Butt,
        },
    };

    private void Compare(
        string scene,
        Action<IImageProcessingContext> ours,
        Action<SKCanvas> skia,
        bool gradient = false,
        double maxOver16 = 0.015,
        int maxSingle = 96)
    {
        using var image = new Image<Rgba32>(Size, Size, new Rgba32(255, 255, 255));
        image.Mutate(ours);

        using var bitmap = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            skia(canvas);
            canvas.Flush();
        }

        int[,] diff = new int[Size, Size];
        long total = 0;
        int over16 = 0;
        int max = 0;
        int changed = 0;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                Rgba32 a = image[x, y];
                SKColor b = bitmap.GetPixel(x, y);
                int d = Math.Max(Math.Max(Math.Abs(a.R - b.Red), Math.Abs(a.G - b.Green)), Math.Max(Math.Abs(a.B - b.Blue), Math.Abs(a.A - b.Alpha)));
                total += Math.Abs(a.R - b.Red) + Math.Abs(a.G - b.Green) + Math.Abs(a.B - b.Blue) + Math.Abs(a.A - b.Alpha);
                diff[x, y] = d;
                max = Math.Max(max, d);
                over16 += d > 16 ? 1 : 0;
                changed += a != new Rgba32(255, 255, 255) ? 1 : 0;
            }
        }

        double mean = total / (4.0 * Size * Size);
        double fraction = over16 / (double)(Size * Size);
        this.output.WriteLine(FormattableString.Invariant(
            $"{scene}: mean |diff| {mean:F3} levels, {over16} pixels ({fraction:P2}) differ by more than 16, max {max}; {changed} pixels drawn"));
        IEnumerable<string> worstPixels = Enumerable.Range(0, Size * Size)
            .Select(i => (X: i % Size, Y: i / Size))
            .OrderByDescending(p => diff[p.X, p.Y])
            .Take(8)
            .Select(p => FormattableString.Invariant($"({p.X},{p.Y}) ours {image[p.X, p.Y]} skia {bitmap.GetPixel(p.X, p.Y)}"));
        this.output.WriteLine("  largest differences: " + string.Join("; ", worstPixels));

        Assert.True(changed > Size * Size / 50, $"{scene}: the scene draws almost nothing, so the comparison proves nothing");
        Assert.True(mean <= 0.6, $"{scene}: mean difference {mean:F3} exceeds 0.6 levels");
        Assert.True(fraction <= maxOver16, $"{scene}: {fraction:P2} of pixels differ by more than 16 levels");
        Assert.True(max <= maxSingle, $"{scene}: a pixel differs by {max} levels");

        if (gradient)
        {
            // Away from shape edges (and, for wrapping gradients, from the seams where the colour jumps), the
            // gradient offset and colour arithmetic must agree to rounding.
            int worst = 0;
            for (int y = 2; y < Size - 2; y++)
            {
                for (int x = 2; x < Size - 2; x++)
                {
                    bool interior = true;
                    for (int dy = -2; dy <= 2 && interior; dy++)
                    {
                        for (int dx = -2; dx <= 2 && interior; dx++)
                        {
                            Rgba32 n = image[x + dx, y + dy];
                            interior = n.A == 255 && n != new Rgba32(255, 255, 255) && Seamless(image, x + dx, y + dy);
                        }
                    }

                    if (interior)
                    {
                        worst = Math.Max(worst, diff[x, y]);
                    }
                }
            }

            this.output.WriteLine(FormattableString.Invariant($"{scene}: interior max |diff| {worst}"));
            Assert.True(worst <= 2, $"{scene}: interior gradient pixels differ by up to {worst} levels");
        }
    }

    /// <summary>Whether a pixel's colour is continuous with its right and lower neighbours (no gradient seam).</summary>
    private static bool Seamless(Image<Rgba32> image, int x, int y)
    {
        Rgba32 p = image[x, y];
        Rgba32 r = image[Math.Min(x + 1, image.Width - 1), y];
        Rgba32 d = image[x, Math.Min(y + 1, image.Height - 1)];
        return Close(p, r) && Close(p, d);

        static bool Close(Rgba32 a, Rgba32 b)
            => Math.Abs(a.R - b.R) <= 24 && Math.Abs(a.G - b.G) <= 24 && Math.Abs(a.B - b.B) <= 24;
    }
}
