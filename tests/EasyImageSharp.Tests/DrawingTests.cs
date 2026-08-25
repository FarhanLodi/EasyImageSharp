using System.Text.Json;
using EasyImageSharp.Drawing;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

public class DrawingTests
{
    private static readonly DrawingOptions NoAa = new() { Antialias = false };

    // ----- Rectangles -----

    [Fact]
    public void FillRectangle_IntegerCoordinates_FillsExactlyThosePixels()
    {
        using var image = new Image<Rgb24>(20, 20, new Rgb24(255, 255, 255));
        image.Mutate(ctx => ctx.FillRectangle(Color.Red, new RectangleF(3, 4, 5, 6)));

        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                bool inside = x >= 3 && x < 8 && y >= 4 && y < 10;
                Assert.Equal(inside ? new Rgb24(255, 0, 0) : new Rgb24(255, 255, 255), image[x, y]);
            }
        }
    }

    [Fact]
    public void DrawRectangle_OutlineLiesInsideBounds_AndFillsBorderPixels()
    {
        using var image = new Image<Rgb24>(30, 30, new Rgb24(255, 255, 255));
        image.Mutate(ctx => ctx.DrawRectangle(Color.Black, 2, new RectangleF(5, 5, 10, 8)));

        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                bool inBounds = x >= 5 && x < 15 && y >= 5 && y < 13;
                bool inHole = x >= 7 && x < 13 && y >= 7 && y < 11;
                Rgb24 expected = inBounds && !inHole ? new Rgb24(0, 0, 0) : new Rgb24(255, 255, 255);
                Assert.Equal(expected, image[x, y]);
            }
        }
    }

    [Fact]
    public void DrawRectangle_ThicknessCoveringWholeBox_FillsIt()
    {
        using var image = new Image<Rgb24>(20, 20, new Rgb24(255, 255, 255));
        image.Mutate(ctx => ctx.DrawRectangle(Color.Blue, 10, new RectangleF(2, 2, 6, 6)));
        Assert.Equal(new Rgb24(0, 0, 255), image[5, 5]);
        Assert.Equal(new Rgb24(0, 0, 255), image[2, 2]);
        Assert.Equal(new Rgb24(255, 255, 255), image[8, 8]);
    }

    [Fact]
    public void FillRectangle_HalfPixelEdges_ReceiveHalfCoverage()
    {
        using var image = new Image<Rgb24>(20, 20, new Rgb24(0, 0, 0));
        image.Mutate(ctx => ctx.FillRectangle(Color.White, new RectangleF(4.5f, 4.5f, 6, 6)));

        Assert.Equal(new Rgb24(255, 255, 255), image[6, 6]);          // Interior.
        Assert.InRange(image[4, 6].R, 127, 128);                       // Left edge: half covered.
        Assert.InRange(image[10, 6].R, 127, 128);                      // Right edge: half covered.
        Assert.InRange(image[4, 4].R, 63, 64);                         // Corner: quarter covered.
        Assert.Equal(new Rgb24(0, 0, 0), image[3, 6]);
        Assert.Equal(new Rgb24(0, 0, 0), image[11, 6]);
    }

    [Fact]
    public void FillRectangle_NegativeSize_IsNormalized()
    {
        using var image = new Image<Rgb24>(20, 20, new Rgb24(0, 0, 0));
        image.Mutate(ctx => ctx.FillRectangle(Color.White, new RectangleF(10, 10, -4, -4)));
        Assert.Equal(new Rgb24(255, 255, 255), image[6, 6]);
        Assert.Equal(new Rgb24(255, 255, 255), image[9, 9]);
        Assert.Equal(new Rgb24(0, 0, 0), image[10, 10]);
        Assert.Equal(new Rgb24(0, 0, 0), image[5, 5]);
    }

    [Fact]
    public void Fill_WholeImage_AndIntegerRectangleOverload()
    {
        using var image = new Image<Rgb24>(8, 8, new Rgb24(0, 0, 0));
        image.Mutate(ctx => ctx.Fill(Color.Lime).Fill(Color.Red, new Rectangle(2, 2, 3, 3)));
        Assert.Equal(new Rgb24(0, 255, 0), image[0, 0]);
        Assert.Equal(new Rgb24(0, 255, 0), image[7, 7]);
        Assert.Equal(new Rgb24(255, 0, 0), image[2, 2]);
        Assert.Equal(new Rgb24(255, 0, 0), image[4, 4]);
        Assert.Equal(new Rgb24(0, 255, 0), image[5, 5]);
    }

    // ----- Pillow references (non-anti-aliased, integer coordinates) -----

    public static IEnumerable<object[]> PillowRectangleFixtures()
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(FixturePath.Get("drawing/manifest.json")));
        foreach (JsonElement entry in manifest.RootElement.GetProperty("entries").EnumerateArray())
        {
            yield return [entry.GetProperty("name").GetString()!];
        }
    }

    [Theory]
    [MemberData(nameof(PillowRectangleFixtures))]
    public void Rectangles_NonAntialiased_MatchPillowExactly(string name)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(FixturePath.Get("drawing/manifest.json")));
        JsonElement entry = manifest.RootElement.GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("name").GetString() == name);
        int[] c = entry.GetProperty("color").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        int[] r = entry.GetProperty("rect").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        float thickness = entry.GetProperty("thickness").GetSingle();
        var color = new Color((byte)c[0], (byte)c[1], (byte)c[2]);
        var rect = new RectangleF(r[0], r[1], r[2], r[3]);

        using Image<Rgb24> expected = Image.Load<Rgb24>(FixturePath.Get("drawing/" + entry.GetProperty("file").GetString()));
        using var actual = new Image<Rgb24>(expected.Width, expected.Height, new Rgb24(255, 255, 255));
        if (entry.GetProperty("op").GetString() == "FillRectangle")
        {
            actual.Mutate(ctx => ctx.FillRectangle(color, rect, NoAa));
        }
        else
        {
            actual.Mutate(ctx => ctx.DrawRectangle(color, thickness, rect, NoAa));
        }

        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Assert.True(expected[x, y] == actual[x, y], $"{name}: pixel ({x},{y}) expected {expected[x, y]} got {actual[x, y]}");
            }
        }
    }

    [Fact]
    public void Antialias_False_ProducesOnlyFullOrNoCoverage()
    {
        using var image = new Image<Rgb24>(50, 50, new Rgb24(0, 0, 0));
        image.Mutate(ctx => ctx
            .FillCircle(Color.White, new PointF(20.3f, 22.7f), 12.4f, NoAa)
            .DrawLine(Color.White, 2.5f, new PointF(3.2f, 40.1f), new PointF(47.9f, 31.4f), NoAa));

        int lit = 0;
        for (int y = 0; y < 50; y++)
        {
            for (int x = 0; x < 50; x++)
            {
                byte v = image[x, y].R;
                Assert.True(v is 0 or 255, $"({x},{y}) has partial coverage {v}");
                lit += v == 255 ? 1 : 0;
            }
        }

        Assert.InRange(lit, 500, 700); // ~pi*12.4^2 (483) + ~45.5*2.5 (114).
    }

    // ----- Circles and ellipses -----

    [Theory]
    [InlineData(64, 32f, 20f, 0f)]      // Centre on a pixel corner, even-sized image: mirror x <-> 63 - x.
    [InlineData(65, 32.5f, 20f, 0f)]    // Centre on a pixel centre, odd-sized image.
    [InlineData(64, 32f, 25f, 3f)]      // Outline.
    [InlineData(65, 32.5f, 30f, 1.5f)]  // Thin outline touching the border.
    public void Circle_Centered_IsExactlySymmetricUnderFlips(int size, float center, float radius, float thickness)
    {
        using var image = new Image<Rgba32>(size, size, new Rgba32(0, 0, 0, 255));
        image.Mutate(ctx =>
        {
            if (thickness > 0)
            {
                ctx.DrawCircle(Color.White, thickness, new PointF(center, center), radius);
            }
            else
            {
                ctx.FillCircle(Color.White, new PointF(center, center), radius);
            }
        });

        int partial = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Rgba32 p = image[x, y];
                Assert.True(p == image[size - 1 - x, y], $"x-mirror mismatch at ({x},{y}): {p} vs {image[size - 1 - x, y]}");
                Assert.True(p == image[x, size - 1 - y], $"y-mirror mismatch at ({x},{y}): {p} vs {image[x, size - 1 - y]}");
                partial += p.R is > 0 and < 255 ? 1 : 0;
            }
        }

        Assert.True(partial > 0, "expected anti-aliased edge pixels");
    }

    [Fact]
    public void FillCircle_Coverage_MatchesArea()
    {
        using var image = new Image<L8>(100, 100, new L8(0));
        image.Mutate(ctx => ctx.FillCircle(Color.White, new PointF(50.25f, 49.5f), 30f));
        double coverage = TotalCoverage(image);
        Assert.InRange(coverage / (Math.PI * 30 * 30), 0.995, 1.005);
    }

    [Fact]
    public void FillEllipse_Coverage_MatchesArea_AndStaysInsideBounds()
    {
        using var image = new Image<L8>(120, 80, new L8(0));
        image.Mutate(ctx => ctx.FillEllipse(Color.White, new RectangleF(10, 10, 100, 60)));
        double coverage = TotalCoverage(image);
        Assert.InRange(coverage / (Math.PI * 50 * 30), 0.995, 1.005);
        Assert.Equal(0, image[9, 40].PackedValue);
        Assert.Equal(0, image[110, 40].PackedValue);
        Assert.InRange(image[10, 40].PackedValue, 235, 255);   // Extreme pixels are almost, not quite, fully covered.
        Assert.InRange(image[109, 40].PackedValue, 235, 255);
        Assert.InRange(image[60, 10].PackedValue, 235, 255);
        Assert.Equal(0, image[60, 9].PackedValue);
        Assert.Equal(255, image[60, 40].PackedValue);
    }

    [Fact]
    public void DrawEllipse_RingCoverage_MatchesAreaDifference()
    {
        using var image = new Image<L8>(120, 120, new L8(0));
        image.Mutate(ctx => ctx.DrawEllipse(Color.White, 5f, new RectangleF(10, 20, 100, 80)));
        double expected = (Math.PI * 50 * 40) - (Math.PI * 45 * 35);
        Assert.InRange(TotalCoverage(image) / expected, 0.99, 1.01);
        Assert.Equal(0, image[60, 60].PackedValue); // Hollow centre.
        Assert.Equal(255, image[12, 60].PackedValue); // Inside the band on the left.
    }

    // ----- Lines and polygons -----

    [Theory]
    [InlineData(10f, 20f, 90f, 70f, 3f)]
    [InlineData(5.5f, 5.5f, 94.5f, 94.5f, 1f)]
    [InlineData(80f, 10f, 20f, 85f, 2.5f)]
    [InlineData(10f, 50f, 90f, 52f, 1f)]
    public void DrawLine_Diagonal_HasNoGaps_AndCoverageMatchesLengthTimesThickness(float x0, float y0, float x1, float y1, float thickness)
    {
        using var image = new Image<L8>(100, 100, new L8(0));
        image.Mutate(ctx => ctx.DrawLine(Color.White, thickness, new PointF(x0, y0), new PointF(x1, y1)));

        double length = Math.Sqrt(((x1 - x0) * (x1 - x0)) + ((y1 - y0) * (y1 - y0)));
        Assert.InRange(TotalCoverage(image) / (length * thickness), 0.95, 1.05);

        // Every column strictly between the end points has ink (the line is more horizontal than 45 degrees or vice versa).
        bool horizontal = Math.Abs(x1 - x0) >= Math.Abs(y1 - y0);
        int from = (int)Math.Ceiling(Math.Min(horizontal ? x0 : y0, horizontal ? x1 : y1)) + 1;
        int to = (int)Math.Floor(Math.Max(horizontal ? x0 : y0, horizontal ? x1 : y1)) - 1;
        for (int i = from; i < to; i++)
        {
            int ink = 0;
            for (int j = 0; j < 100; j++)
            {
                ink += horizontal ? image[i, j].PackedValue : image[j, i].PackedValue;
            }

            Assert.True(ink > 0, $"gap at {(horizontal ? "column" : "row")} {i}");
        }
    }

    [Fact]
    public void DrawLine_OnPixelCentres_IsCrispOnePixelWide()
    {
        using var image = new Image<Rgb24>(20, 20, new Rgb24(0, 0, 0));
        image.Mutate(ctx => ctx.DrawLine(Color.White, 1, new PointF(10.5f, 2), new PointF(10.5f, 18)));
        for (int y = 2; y < 18; y++)
        {
            Assert.Equal(new Rgb24(255, 255, 255), image[10, y]);
            Assert.Equal(new Rgb24(0, 0, 0), image[9, y]);
            Assert.Equal(new Rgb24(0, 0, 0), image[11, y]);
        }

        Assert.Equal(new Rgb24(0, 0, 0), image[10, 1]); // Butt caps: nothing beyond the end points.
        Assert.Equal(new Rgb24(0, 0, 0), image[10, 18]);
    }

    [Fact]
    public void DrawLines_RightAngle_MiterJoinFillsTheCorner()
    {
        using var image = new Image<Rgb24>(30, 30, new Rgb24(0, 0, 0));
        image.Mutate(ctx => ctx.DrawLines(Color.White, 3, new PointF(5, 10.5f), new PointF(20.5f, 10.5f), new PointF(20.5f, 25)));

        Assert.Equal(new Rgb24(255, 255, 255), image[21, 9]);   // Outer corner pixel, only covered by the join.
        Assert.Equal(new Rgb24(255, 255, 255), image[21, 10]);
        Assert.Equal(new Rgb24(255, 255, 255), image[19, 9]);
        Assert.Equal(new Rgb24(0, 0, 0), image[22, 8]);
        Assert.Equal(new Rgb24(0, 0, 0), image[18, 12]);      // Inside the elbow.
    }

    [Fact]
    public void DrawPolygon_SquareOnPixelCentres_MatchesDrawRectangle()
    {
        using var viaPolygon = new Image<Rgb24>(40, 40, new Rgb24(0, 0, 0));
        using var viaRectangle = new Image<Rgb24>(40, 40, new Rgb24(0, 0, 0));
        viaPolygon.Mutate(ctx => ctx.DrawPolygon(Color.White, 1, new PointF(10.5f, 10.5f), new PointF(29.5f, 10.5f), new PointF(29.5f, 29.5f), new PointF(10.5f, 29.5f)));
        viaRectangle.Mutate(ctx => ctx.DrawRectangle(Color.White, 1, new RectangleF(10, 10, 20, 20)));

        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                Assert.True(viaPolygon[x, y] == viaRectangle[x, y], $"mismatch at ({x},{y})");
            }
        }

        Assert.Equal(new Rgb24(255, 255, 255), viaRectangle[10, 10]);
        Assert.Equal(new Rgb24(255, 255, 255), viaRectangle[29, 29]);
        Assert.Equal(new Rgb24(0, 0, 0), viaRectangle[11, 11]);
    }

    [Fact]
    public void FillPolygon_Triangle_CoverageMatchesArea()
    {
        using var image = new Image<L8>(100, 100, new L8(0));
        image.Mutate(ctx => ctx.FillPolygon(Color.White, new PointF(10, 90), new PointF(90, 90), new PointF(50, 10)));
        Assert.InRange(TotalCoverage(image) / (0.5 * 80 * 80), 0.995, 1.005);
        Assert.Equal(255, image[50, 60].PackedValue);
        Assert.Equal(0, image[15, 20].PackedValue);
    }

    [Fact]
    public void FillPolygon_SelfIntersectingStar_FillsCentreWithNonZeroRule()
    {
        using var image = new Image<L8>(100, 100, new L8(0));
        var points = new PointF[5];
        for (int i = 0; i < 5; i++)
        {
            double angle = (-Math.PI / 2) + (i * 4 * Math.PI / 5); // Pentagram order.
            points[i] = new PointF(50 + (40 * (float)Math.Cos(angle)), 50 + (40 * (float)Math.Sin(angle)));
        }

        image.Mutate(ctx => ctx.FillPolygon(Color.White, points));
        Assert.Equal(255, image[50, 50].PackedValue);
        Assert.Equal(255, image[50, 15].PackedValue);
    }

    [Fact]
    public void FillPolygon_FewerThanThreePoints_DrawsNothing()
    {
        using var image = new Image<Rgb24>(10, 10, new Rgb24(0, 0, 0));
        image.Mutate(ctx => ctx.FillPolygon(Color.White, new PointF(1, 1), new PointF(8, 8)).DrawLines(Color.White, 2, new PointF(3, 3)));
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                Assert.Equal(new Rgb24(0, 0, 0), image[x, y]);
            }
        }
    }

    // ----- Compositing -----

    [Fact]
    public void SemiTransparentPolyline_BlendsOnceAtJoins()
    {
        using var image = new Image<Rgb24>(60, 60, new Rgb24(0, 0, 0));
        image.Mutate(ctx => ctx.DrawLines(Color.White.WithAlpha(128), 6, new PointF(5, 30.5f), new PointF(30.5f, 30.5f), new PointF(55, 5)));

        Rgb24 midSegment = image[15, 30];
        Assert.InRange(midSegment.R, 127, 129);
        Assert.Equal(midSegment, image[30, 30]);   // Vertex: overlap of both quads and the join, still blended once.
        Assert.Equal(midSegment, image[31, 29]);
    }

    [Fact]
    public void BlendPercentage_ScalesOpacity()
    {
        using var image = new Image<Rgb24>(10, 10, new Rgb24(255, 255, 255));
        image.Mutate(ctx => ctx.FillRectangle(Color.Blue, new RectangleF(0, 0, 10, 10), new DrawingOptions { BlendPercentage = 0.5f }));
        Rgb24 p = image[5, 5];
        Assert.InRange(p.R, 127, 128);
        Assert.InRange(p.G, 127, 128);
        Assert.Equal(255, p.B);
    }

    [Fact]
    public void ColourAlpha_IsHonoured_OnOpaqueAndTransparentBackgrounds()
    {
        using var opaque = new Image<Rgba32>(4, 4, new Rgba32(0, 0, 0, 255));
        opaque.Mutate(ctx => ctx.Fill(Color.Red.WithAlpha(64)));
        Assert.Equal(new Rgba32(64, 0, 0, 255), opaque[1, 1]);

        using var transparent = new Image<Rgba32>(4, 4);
        transparent.Mutate(ctx => ctx.FillRectangle(Color.Red.WithAlpha(128), new RectangleF(0, 0, 4, 4)));
        Assert.Equal(new Rgba32(255, 0, 0, 128), transparent[2, 2]);

        // Source-over on a translucent destination: alpha accumulates.
        transparent.Mutate(ctx => ctx.FillRectangle(Color.Blue.WithAlpha(128), new RectangleF(0, 0, 4, 4)));
        Rgba32 p = transparent[2, 2];
        Assert.InRange(p.A, 191, 192);
        Assert.InRange(p.B, 168, 172);
        Assert.InRange(p.R, 83, 87);
    }

    [Fact]
    public void FullyTransparentColour_ChangesNothing()
    {
        using var image = new Image<Rgb24>(10, 10, new Rgb24(9, 9, 9));
        image.Mutate(ctx => ctx.Fill(Color.Transparent).FillCircle(Color.Transparent, new PointF(5, 5), 4).DrawText("hi", Color.Transparent, new PointF(0, 0)));
        Assert.Equal(new Rgb24(9, 9, 9), image[5, 5]);
        Assert.Equal(new Rgb24(9, 9, 9), image[1, 3]);
    }

    [Fact]
    public void Drawing_AppliesToEveryFrame()
    {
        using var image = new Image<Rgb24>(10, 10, new Rgb24(0, 0, 0));
        image.Frames.CreateFrame(10, 10);
        image.Mutate(ctx => ctx.FillRectangle(Color.Red, new RectangleF(2, 2, 4, 4)).DrawText("A", Color.White, new PointF(0, 0)));
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(new Rgb24(255, 0, 0), image.Frames[i][5, 2]);   // Filled, not under a glyph pixel.
            Assert.Equal(new Rgb24(255, 255, 255), image.Frames[i][3, 3]); // 'A' row 3 covers column 3.
            Assert.Equal(new Rgb24(0, 0, 0), image.Frames[i][8, 8]);
        }
    }

    [Fact]
    public void Clone_WithDrawing_LeavesOriginalUntouched()
    {
        using var image = new Image<Rgb24>(10, 10, new Rgb24(0, 0, 0));
        using Image<Rgb24> copy = image.Clone(ctx => ctx.Fill(Color.White));
        Assert.Equal(new Rgb24(0, 0, 0), image[5, 5]);
        Assert.Equal(new Rgb24(255, 255, 255), copy[5, 5]);
    }

    [Fact]
    public void Drawing_WorksOnEveryPixelFormat()
    {
        AssertFormat<Rgb24>();
        AssertFormat<Rgba32>();
        AssertFormat<Bgr24>();
        AssertFormat<Bgra32>();
        AssertFormat<L8>();

        static void AssertFormat<TPixel>()
            where TPixel : unmanaged, IPixel<TPixel>
        {
            using var image = new Image<TPixel>(32, 32, TPixel.FromRgba32(new Rgba32(0, 0, 0, 255)));
            image.Mutate(ctx => ctx
                .FillRectangle(Color.Red, new RectangleF(2, 2, 6, 6))
                .DrawCircle(Color.White, 2, new PointF(20, 20), 8)
                .DrawText("Q", Color.White, new PointF(0, 16)));

            Rgba32 filled = image[4, 4].ToRgba32();
            Rgba32 untouched = image[12, 4].ToRgba32();
            Rgba32 expectedRed = TPixel.FromRgba32(new Rgba32(255, 0, 0, 255)).ToRgba32();
            Assert.Equal(expectedRed, filled);
            Assert.Equal(new Rgba32(0, 0, 0, 255), untouched);
            Assert.Equal(new Rgba32(255, 255, 255, 255), image[26, 20].ToRgba32()); // On the ring (r 6..8).
            Assert.True(image[20, 20].ToRgba32().R == 0, "circle centre must stay hollow");
        }
    }

    // ----- Text -----

    [Fact]
    public void DrawText_RendersGlyphBits_AtLocation()
    {
        using var image = new Image<Rgb24>(24, 24, new Rgb24(0, 0, 0));
        image.Mutate(ctx => ctx.DrawText("A", Color.White, new PointF(3, 4)));

        ReadOnlySpan<byte> glyph = BitmapFont.Default.GetGlyph('A');
        for (int y = 0; y < 24; y++)
        {
            for (int x = 0; x < 24; x++)
            {
                int gx = x - 3;
                int gy = y - 4;
                bool set = gx is >= 0 and < 8 && gy is >= 0 and < 16 && (glyph[gy] & (0x80 >> gx)) != 0;
                Assert.Equal(set ? new Rgb24(255, 255, 255) : new Rgb24(0, 0, 0), image[x, y]);
            }
        }
    }

    [Fact]
    public void DrawText_Scale_MagnifiesByIntegerFactor()
    {
        using var small = new Image<Rgb24>(16, 32, new Rgb24(0, 0, 0));
        using var large = new Image<Rgb24>(48, 96, new Rgb24(0, 0, 0));
        small.Mutate(ctx => ctx.DrawText("g", Color.White, new PointF(4, 8)));
        large.Mutate(ctx => ctx.DrawText("g", Color.White, new PointF(12, 24), new TextOptions { Scale = 3 }));

        for (int y = 0; y < 96; y++)
        {
            for (int x = 0; x < 48; x++)
            {
                Assert.True(large[x, y] == small[x / 3, y / 3], $"({x},{y})");
            }
        }
    }

    [Fact]
    public void DrawText_Background_DrawsPaddedBoxBehindGlyphs()
    {
        using var image = new Image<Rgb24>(40, 30, new Rgb24(0, 0, 0));
        var options = new TextOptions { Background = Color.Blue, Padding = 2 };
        image.Mutate(ctx => ctx.DrawText("ab", Color.White, new PointF(5, 3), options));

        Assert.Equal(new Size(20, 20), options.Measure("ab"));
        Assert.Equal(new Rgb24(0, 0, 255), image[5, 3]);      // Box top-left.
        Assert.Equal(new Rgb24(0, 0, 255), image[24, 22]);    // Box bottom-right (5 + 20 - 1, 3 + 20 - 1).
        Assert.Equal(new Rgb24(0, 0, 0), image[25, 22]);
        Assert.Equal(new Rgb24(0, 0, 0), image[24, 23]);
        Assert.Equal(new Rgb24(0, 0, 0), image[4, 3]);

        // Glyph 'a' has ink at row 6, column 1 -> image (5 + 2 + 1, 3 + 2 + 6).
        Assert.Equal(new Rgb24(255, 255, 255), image[8, 11]);
    }

    [Fact]
    public void DrawText_Alignment_AnchorsCentreAndRight()
    {
        using var left = new Image<Rgb24>(64, 20, new Rgb24(0, 0, 0));
        using var centre = new Image<Rgb24>(64, 20, new Rgb24(0, 0, 0));
        using var right = new Image<Rgb24>(64, 20, new Rgb24(0, 0, 0));
        left.Mutate(ctx => ctx.DrawText("MM", Color.White, new PointF(16, 2)));
        centre.Mutate(ctx => ctx.DrawText("MM", Color.White, new PointF(24, 2), new TextOptions { HorizontalAlignment = HorizontalAlignment.Center }));
        right.Mutate(ctx => ctx.DrawText("MM", Color.White, new PointF(32, 2), new TextOptions { HorizontalAlignment = HorizontalAlignment.Right }));

        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                Assert.True(left[x, y] == centre[x, y], $"centre ({x},{y})");
                Assert.True(left[x, y] == right[x, y], $"right ({x},{y})");
            }
        }
    }

    [Fact]
    public void DrawText_MultiLine_StacksLines()
    {
        using var image = new Image<Rgb24>(40, 40, new Rgb24(0, 0, 0));
        image.Mutate(ctx => ctx.DrawText("|\n|", Color.White, new PointF(0, 0)));
        ReadOnlySpan<byte> bar = BitmapFont.Default.GetGlyph('|');
        Assert.NotEqual(0, bar[8]);
        Assert.Equal(new Rgb24(255, 255, 255), image[3, 8]);
        Assert.Equal(new Rgb24(255, 255, 255), image[3, 24]);
        Assert.Equal(new Rgb24(0, 0, 0), image[3, 15]); // Row 15 of '|' is blank.
    }

    [Fact]
    public void DrawText_PartiallyOutside_IsClippedWithoutError()
    {
        using var image = new Image<Rgb24>(20, 20, new Rgb24(0, 0, 0));
        image.Mutate(ctx => ctx
            .DrawText("Hello", Color.White, new PointF(-12, -6))
            .DrawText("World", Color.White, new PointF(15, 12), new TextOptions { Scale = 4, Background = Color.Red })
            .DrawText("Nowhere", Color.White, new PointF(500, 500))
            .DrawText(string.Empty, Color.White, new PointF(1, 1)));
        Assert.Equal(new Rgb24(255, 0, 0), image[15, 12]);
        Assert.Equal(new Rgb24(0, 0, 0), image[14, 12]);
    }

    [Fact]
    public void DrawText_UnknownCharacter_UsesFallbackGlyph()
    {
        using var withEuro = new Image<Rgb24>(16, 16, new Rgb24(0, 0, 0));
        using var withBox = new Image<Rgb24>(16, 16, new Rgb24(0, 0, 0));
        withEuro.Mutate(ctx => ctx.DrawText("€", Color.White, new PointF(0, 0)));
        withBox.Mutate(ctx => ctx.DrawText("\x7f", Color.White, new PointF(0, 0)));
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                Assert.Equal(withBox[x, y], withEuro[x, y]);
            }
        }

        Assert.Equal(new Rgb24(255, 255, 255), withEuro[0, 2]);
    }

    // ----- Labels and bounding boxes -----

    [Fact]
    public void DrawLabel_PlacesBoxAboveAnchor_WhenThereIsRoom()
    {
        using var image = new Image<Rgb24>(100, 60, new Rgb24(0, 0, 0));
        image.Mutate(ctx => ctx.DrawLabel("ab", Color.Black, Color.Lime, new RectangleF(20, 30, 40, 20)));

        // Box is 2*8 + 4 = 20 wide, 16 + 4 = 20 tall, sitting on the anchor's top edge.
        Assert.Equal(new Rgb24(0, 255, 0), image[20, 10]);
        Assert.Equal(new Rgb24(0, 255, 0), image[39, 29]);
        Assert.Equal(new Rgb24(0, 0, 0), image[20, 30]);
        Assert.Equal(new Rgb24(0, 0, 0), image[20, 9]);
        Assert.Equal(new Rgb24(0, 0, 0), image[40, 20]);
        Assert.Equal(new Rgb24(0, 0, 0), image[19, 20]);
    }

    [Fact]
    public void DrawLabel_FallsInsideAnchor_WhenNoRoomAbove()
    {
        using var image = new Image<Rgb24>(100, 60, new Rgb24(0, 0, 0));
        image.Mutate(ctx => ctx.DrawLabel("ab", Color.Black, Color.Lime, new RectangleF(20, 5, 40, 30)));
        Assert.Equal(new Rgb24(0, 255, 0), image[20, 5]);
        Assert.Equal(new Rgb24(0, 255, 0), image[39, 24]);
        Assert.Equal(new Rgb24(0, 0, 0), image[20, 4]);
        Assert.Equal(new Rgb24(0, 0, 0), image[20, 25]);
    }

    [Theory]
    [InlineData(-30f, -10f)]
    [InlineData(95f, 2f)]
    [InlineData(90f, 55f)]
    [InlineData(50f, 30f)]
    public void DrawLabel_StaysInsideTheImage(float anchorX, float anchorY)
    {
        using var image = new Image<Rgb24>(100, 60, new Rgb24(0, 0, 0));
        image.Mutate(ctx => ctx.DrawLabel("label", Color.Red, Color.Lime, new RectangleF(anchorX, anchorY, 25, 25)));

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
        for (int y = 0; y < 60; y++)
        {
            for (int x = 0; x < 100; x++)
            {
                if (image[x, y] != new Rgb24(0, 0, 0))
                {
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                    count++;
                }
            }
        }

        // Full 44x20 label box is visible: 5 glyphs * 8 + 4 by 16 + 4.
        Assert.Equal(44 * 20, count);
        Assert.Equal(43, maxX - minX);
        Assert.Equal(19, maxY - minY);
    }

    [Fact]
    public void DrawBoundingBoxes_DrawsOutlinesAndContrastingLabels()
    {
        using var image = new Image<Rgb24>(120, 80, new Rgb24(0, 0, 0));
        var boxes = new List<(Rectangle, string?)>
        {
            (new Rectangle(10, 30, 40, 30), "cat"),
            (new Rectangle(70, 40, 30, 20), null),
        };
        image.Mutate(ctx => ctx.DrawBoundingBoxes(boxes, Color.Yellow, 2));

        // Outline pixels (2 px inside the box).
        Assert.Equal(new Rgb24(255, 255, 0), image[10, 45]);
        Assert.Equal(new Rgb24(255, 255, 0), image[11, 45]);
        Assert.Equal(new Rgb24(0, 0, 0), image[12, 45]);
        Assert.Equal(new Rgb24(255, 255, 0), image[70, 50]);
        Assert.Equal(new Rgb24(255, 255, 0), image[99, 50]);

        // Label box above the first rectangle: 3*8+4 = 28 wide, 20 tall, bottom at y = 29; black text on yellow.
        Assert.Equal(new Rgb24(255, 255, 0), image[10, 10]);
        Assert.Equal(new Rgb24(255, 255, 0), image[37, 29]);
        Assert.Equal(new Rgb24(0, 0, 0), image[38, 20]);
        bool blackInk = false;
        for (int y = 12; y < 28 && !blackInk; y++)
        {
            for (int x = 12; x < 36; x++)
            {
                if (image[x, y] == new Rgb24(0, 0, 0))
                {
                    blackInk = true;
                    break;
                }
            }
        }

        Assert.True(blackInk, "expected black label text on the yellow box");

        // Second box has no label: nothing above it.
        Assert.Equal(new Rgb24(0, 0, 0), image[72, 30]);
    }

    [Fact]
    public void DrawBoundingBoxes_DarkColour_UsesWhiteText()
    {
        using var image = new Image<Rgb24>(80, 60, new Rgb24(128, 128, 128));
        image.Mutate(ctx => ctx.DrawBoundingBoxes([(new Rectangle(10, 30, 30, 20), "x")], Color.Navy, 1));
        bool whiteInk = false;
        for (int y = 10; y < 30 && !whiteInk; y++)
        {
            for (int x = 10; x < 22; x++)
            {
                if (image[x, y] == new Rgb24(255, 255, 255))
                {
                    whiteInk = true;
                    break;
                }
            }
        }

        Assert.True(whiteInk);
    }

    // ----- Pens, validation -----

    [Fact]
    public void Pen_Overloads_MatchColourThicknessOverloads()
    {
        using var a = new Image<Rgb24>(40, 40, new Rgb24(0, 0, 0));
        using var b = new Image<Rgb24>(40, 40, new Rgb24(0, 0, 0));
        var pen = new Pen(Color.Orange, 2.5f);
        a.Mutate(ctx => ctx
            .DrawRectangle(pen, new RectangleF(2, 2, 12, 10))
            .DrawLine(pen, new PointF(3, 30), new PointF(37, 20))
            .DrawCircle(pen, new PointF(28, 12), 7)
            .DrawPolygon(pen, new PointF(5, 20), new PointF(15, 22), new PointF(8, 36)));
        b.Mutate(ctx => ctx
            .DrawRectangle(Color.Orange, 2.5f, new RectangleF(2, 2, 12, 10))
            .DrawLine(Color.Orange, 2.5f, new PointF(3, 30), new PointF(37, 20))
            .DrawCircle(Color.Orange, 2.5f, new PointF(28, 12), 7)
            .DrawPolygon(Color.Orange, 2.5f, new PointF(5, 20), new PointF(15, 22), new PointF(8, 36)));

        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                Assert.Equal(b[x, y], a[x, y]);
            }
        }

        Assert.Equal(1f, default(Pen).Thickness);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(Color.Red, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pen(Color.Red, float.NaN));
    }

    [Fact]
    public void InvalidArguments_Throw()
    {
        using var image = new Image<Rgb24>(10, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(ctx => ctx.DrawRectangle(Color.Red, 0f, new RectangleF(0, 0, 5, 5))));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(ctx => ctx.DrawLine(Color.Red, -1f, new PointF(0, 0), new PointF(5, 5))));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Mutate(ctx => ctx.DrawCircle(Color.Red, 1f, new PointF(0, 0), -3f)));
        Assert.Throws<ArgumentException>(() => image.Mutate(ctx => ctx.FillRectangle(Color.Red, new RectangleF(float.NaN, 0, 5, 5))));
        Assert.Throws<ArgumentException>(() => image.Mutate(ctx => ctx.FillPolygon(Color.Red, new PointF(0, 0), new PointF(float.PositiveInfinity, 1), new PointF(2, 2))));
        Assert.Throws<ArgumentNullException>(() => image.Mutate(ctx => ctx.FillRectangle(Color.Red, new RectangleF(0, 0, 5, 5), null!)));
        Assert.Throws<ArgumentNullException>(() => image.Mutate(ctx => ctx.DrawText(null!, Color.Red, new PointF(0, 0))));
        Assert.Throws<ArgumentNullException>(() => image.Mutate(ctx => ctx.DrawBoundingBoxes((IEnumerable<(Rectangle, string?)>)null!, Color.Red, 1f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextOptions { Scale = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextOptions { Scale = 9 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextOptions { Padding = -1 });
        Assert.Equal(1f, new DrawingOptions { BlendPercentage = 3f }.BlendPercentage);
        Assert.Equal(0f, new DrawingOptions { BlendPercentage = -1f }.BlendPercentage);
    }

    [Fact]
    public void HugeAndTinyShapes_DoNotThrow()
    {
        using var image = new Image<Rgb24>(16, 16, new Rgb24(0, 0, 0));
        image.Mutate(ctx => ctx
            .FillRectangle(Color.White, new RectangleF(-1e9f, -1e9f, 2e9f, 2e9f))
            .DrawCircle(Color.Red, 1, new PointF(8, 8), 1e7f)
            .FillCircle(Color.Blue, new PointF(4, 4), 0.001f)
            .DrawLine(Color.Blue, 0.01f, new PointF(0, 0), new PointF(16, 16))
            .DrawLines(Color.Blue, 3, new PointF(2, 2), new PointF(2, 2), new PointF(2, 2)));
        Assert.Equal(new Rgb24(255, 255, 255), image[15, 0]);
        Assert.Equal(new Rgb24(255, 255, 255), image[0, 15]);
    }

    // ----- Helpers -----

    private static double TotalCoverage(Image<L8> image)
    {
        double sum = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                sum += image[x, y].PackedValue / 255.0;
            }
        }

        return sum;
    }
}
