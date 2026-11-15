using System.Text.Json;
using EasyImageSharp.Fonts;
using EasyImageSharp.Fonts.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests.Fonts;

/// <summary>
/// Glyph coverage compared against FreeType 2.13 (unhinted, FT_RENDER_MODE_NORMAL) as an independent
/// rasteriser. Both compute exact area coverage of the same outlines, so they differ only where their curve
/// flattening differs and in FreeType's 26.6 fixed-point rounding of the scaled outline.
/// </summary>
public class GlyphRenderingTests
{
    /// <summary>
    /// Tolerances against FreeType's rendering of the TrueType subsets: the largest single-pixel difference (of
    /// 255), the mean absolute difference over the pixels either rasteriser covers, and the relative difference
    /// in total ink. 'A' (straight edges only) agrees within 3/255, which is FreeType's 26.6 rounding; curved
    /// glyphs differ more because FreeType flattens curves more coarsely than this engine's 1/20 px. Measured
    /// worst cases over 10 glyphs at 12, 24 and 48 px: max 16, mean 2.9, ink 2.0% (all at 12 px).
    /// </summary>
    private const int TrueTypeMaxPixel = 24;
    private const double TrueTypeMaxMean = 4.0;
    private const double TrueTypeMaxInk = 0.03;

    /// <summary>
    /// Tolerances against FreeType's own rendering of the CFF files. FreeType's CFF and TrueType renderings of
    /// these identical shapes already differ from each other by up to 38/255 (mean up to 7.4 at 12 px), so this
    /// comparison validates the charstring decoding (shape, placement, ink) rather than rasterisation precision.
    /// Measured worst cases: max 37, mean 11.1, ink 3.1%.
    /// </summary>
    private const int CffMaxPixel = 48;
    private const double CffMaxMean = 14.0;
    private const double CffMaxInk = 0.05;

    private readonly ITestOutputHelper output;

    public GlyphRenderingTests(ITestOutputHelper output) => this.output = output;

    /// <summary>
    /// Each font is compared with FreeType's rendering of the TrueType subset, whose outlines it shares (the CFF
    /// fonts hold the same shapes as exact cubics), and, for the CFF fonts, also with FreeType's own rendering of
    /// the CFF file, which validates the charstring interpretation independently.
    /// </summary>
    [Theory]
    [InlineData(FontAssets.Regular, FontAssets.Regular)]
    [InlineData(FontAssets.Bold, FontAssets.Bold)]
    [InlineData(FontAssets.Cff, FontAssets.Regular)]
    [InlineData(FontAssets.Cff, FontAssets.Cff)]
    [InlineData(FontAssets.Cid, FontAssets.Regular)]
    [InlineData(FontAssets.Cid, FontAssets.Cid)]
    public void Coverage_matches_freetype_within_tolerance(string file, string referenceFile)
    {
        bool cffReference = referenceFile.EndsWith(".otf", StringComparison.Ordinal);
        int maxPixel = cffReference ? CffMaxPixel : TrueTypeMaxPixel;
        double maxMean = cffReference ? CffMaxMean : TrueTypeMaxMean;
        double maxInk = cffReference ? CffMaxInk : TrueTypeMaxInk;
        FontFace face = FontAssets.Face(file);
        int worst = 0;
        double worstMean = 0;
        double worstInk = 0;
        foreach (JsonElement render in FontAssets.Reference(referenceFile).GetProperty("renders").EnumerateArray())
        {
            int size = render.GetProperty("size").GetInt32();
            int codePoint = render.GetProperty("codepoint").GetInt32();
            GlyphBitmap ours = GlyphRasterizer.Render(face.GetOutline(face.GetGlyphIndex(codePoint)), (float)size / face.UnitsPerEm, 0f, 0f);
            byte[] freetype = Convert.FromBase64String(render.GetProperty("coverage").GetString()!);
            (int max, double mean, double ink) = Compare(
                ours,
                render.GetProperty("left").GetInt32(),
                -render.GetProperty("top").GetInt32(),
                render.GetProperty("width").GetInt32(),
                render.GetProperty("height").GetInt32(),
                freetype);

            Assert.True(max <= maxPixel, $"'{(char)codePoint}' at {size}px: max difference {max}");
            Assert.True(mean <= maxMean, $"'{(char)codePoint}' at {size}px: mean difference {mean:F2}");
            Assert.True(ink <= maxInk, $"'{(char)codePoint}' at {size}px: ink differs by {ink:P2}");
            worst = Math.Max(worst, max);
            worstMean = Math.Max(worstMean, mean);
            worstInk = Math.Max(worstInk, ink);
        }

        this.output.WriteLine($"{file} vs FreeType({referenceFile}): worst max {worst}, worst mean {worstMean:F3}, worst ink {worstInk:P3}");
    }

    [Fact]
    public void Subpixel_offset_shifts_ink_without_changing_its_amount()
    {
        FontFace face = FontAssets.Face(FontAssets.Regular);
        GlyphOutline outline = face.GetOutline(face.GetGlyphIndex('O'));
        float scale = 32f / face.UnitsPerEm;
        GlyphBitmap at0 = GlyphRasterizer.Render(outline, scale, 0f, 0f);
        GlyphBitmap at05 = GlyphRasterizer.Render(outline, scale, 0.5f, 0f);
        (double ink0, double cx0) = InkAndCentroid(at0);
        (double ink5, double cx5) = InkAndCentroid(at05);
        Assert.InRange(ink5 / ink0, 0.995, 1.005);
        Assert.InRange(cx5 - cx0, 0.45, 0.55);
    }

    [Fact]
    public void Nonzero_winding_unions_same_direction_and_cuts_opposite_direction()
    {
        // Two overlapping squares wound the same way: the overlap stays covered (even-odd would clear it).
        var same = new GlyphOutline.Builder();
        AddSquare(same, 0, 0, 20, clockwise: true);
        AddSquare(same, 10, 10, 20, clockwise: true);
        GlyphBitmap union = GlyphRasterizer.Render(same.Build(), 1f, 0f, 0f);
        Assert.Equal(255, At(union, 15, -15));
        Assert.Equal(255, At(union, 5, -5));

        // A square inside another wound the other way is a hole.
        var opposite = new GlyphOutline.Builder();
        AddSquare(opposite, 0, 0, 30, clockwise: true);
        AddSquare(opposite, 10, 10, 10, clockwise: false);
        GlyphBitmap hole = GlyphRasterizer.Render(opposite.Build(), 1f, 0f, 0f);
        Assert.Equal(0, At(hole, 15, -15));
        Assert.Equal(255, At(hole, 5, -5));

        // A half-pixel edge gives half coverage.
        var half = new GlyphOutline.Builder();
        AddSquare(half, 0, 0, 10, clockwise: true);
        GlyphBitmap shifted = GlyphRasterizer.Render(half.Build(), 1f, 0.5f, 0f);
        Assert.InRange(At(shifted, 0, -5), 126, 129);
        Assert.Equal(255, At(shifted, 5, -5));
    }

    [Fact]
    public void Outline_halo_contains_the_glyph_and_reaches_the_requested_distance()
    {
        FontFace face = FontAssets.Face(FontAssets.Regular);
        GlyphOutline outline = face.GetOutline(face.GetGlyphIndex('l'));
        float scale = 40f / face.UnitsPerEm;
        GlyphBitmap fill = GlyphRasterizer.Render(outline, scale, 0f, 0f);
        GlyphBitmap halo = GlyphRasterizer.Render(outline, scale, 0f, 3f);
        Assert.True(halo.Width >= fill.Width + 6);

        // Every covered fill pixel is at least as covered in the halo.
        for (int y = 0; y < fill.Height; y++)
        {
            for (int x = 0; x < fill.Width; x++)
            {
                int fx = x + fill.Left, fy = y + fill.Top;
                Assert.True(At(halo, fx, fy) >= fill.Coverage[(y * fill.Width) + x]);
            }
        }

        // The 'l' stem is a vertical bar: two pixels outside its left edge are fully covered by a 3 px halo, five
        // pixels outside are not covered at all.
        int middleRow = (fill.Top + fill.Height + fill.Top) / 2;
        int stemLeft = Enumerable.Range(fill.Left, fill.Width).First(x => At(fill, x, middleRow) > 0);
        Assert.Equal(255, At(halo, stemLeft - 2, middleRow));
        Assert.Equal(0, At(halo, stemLeft - 5, middleRow));
    }

    [Fact]
    public void Glyph_cache_is_bounded_and_consistent_across_threads()
    {
        FontFace face = FontAssets.Face(FontAssets.Regular);
        int glyph = face.GetGlyphIndex('g');
        GlyphBitmap expected = GlyphRasterizer.Render(face.GetOutline(glyph), 21f / face.UnitsPerEm, 0.25f, 0f);
        Parallel.For(0, 64, i =>
        {
            GlyphBitmap cached = GlyphCache.Get(face, glyph, 21f, 1, 0f);
            Assert.Equal(expected.Coverage, cached.Coverage);
            Assert.Equal(expected.Left, cached.Left);
            Assert.Equal(expected.Top, cached.Top);
        });

        // Fill the cache with many large renders; it must stay within its budget.
        for (int size = 200; size < 1200; size += 10)
        {
            GlyphCache.Get(face, face.GetGlyphIndex('W'), size, 0, 0f);
        }

        Assert.True(GlyphCache.CachedBytes <= GlyphCache.MaxBytes, $"cache holds {GlyphCache.CachedBytes} bytes");
        Assert.True(GlyphCache.Count > 0);
    }

    private static void AddSquare(GlyphOutline.Builder builder, float x, float y, float size, bool clockwise)
    {
        builder.MoveTo(x, y);
        if (clockwise)
        {
            builder.LineTo(x, y + size);
            builder.LineTo(x + size, y + size);
            builder.LineTo(x + size, y);
        }
        else
        {
            builder.LineTo(x + size, y);
            builder.LineTo(x + size, y + size);
            builder.LineTo(x, y + size);
        }

        builder.Close();
    }

    /// <summary>Coverage at pen-relative pixel (x, y) (y down from the baseline), 0 outside the bitmap.</summary>
    private static int At(GlyphBitmap bitmap, int x, int y)
    {
        int col = x - bitmap.Left;
        int row = y - bitmap.Top;
        return (uint)col < (uint)bitmap.Width && (uint)row < (uint)bitmap.Height ? bitmap.Coverage[(row * bitmap.Width) + col] : 0;
    }

    private static (double Ink, double CentroidX) InkAndCentroid(GlyphBitmap bitmap)
    {
        double ink = 0, moment = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                int c = bitmap.Coverage[(y * bitmap.Width) + x];
                ink += c;
                moment += c * (x + bitmap.Left + 0.5);
            }
        }

        return (ink, moment / ink);
    }

    private static (int Max, double Mean, double Ink) Compare(GlyphBitmap ours, int left, int top, int width, int height, byte[] reference)
    {
        int x0 = Math.Min(ours.Left, left);
        int y0 = Math.Min(ours.Top, top);
        int x1 = Math.Max(ours.Left + ours.Width, left + width);
        int y1 = Math.Max(ours.Top + ours.Height, top + height);
        int max = 0;
        long total = 0;
        int counted = 0;
        long inkOurs = 0, inkReference = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int a = At(ours, x, y);
                int col = x - left, row = y - top;
                int b = (uint)col < (uint)width && (uint)row < (uint)height ? reference[(row * width) + col] : 0;
                if (a == 0 && b == 0)
                {
                    continue;
                }

                int d = Math.Abs(a - b);
                max = Math.Max(max, d);
                total += d;
                counted++;
                inkOurs += a;
                inkReference += b;
            }
        }

        double ink = inkReference == 0 ? (inkOurs == 0 ? 0 : 1) : Math.Abs(inkOurs - inkReference) / (double)inkReference;
        return (max, counted == 0 ? 0 : total / (double)counted, ink);
    }
}
