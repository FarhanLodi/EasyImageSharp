using EasyImageSharp.Drawing;
using EasyImageSharp.Fonts;
using EasyImageSharp.Fonts.Rendering;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests.Fonts;

/// <summary>Drawing font text into images through the processing pipeline.</summary>
public class TextDrawingTests
{
    private static Font Font(float size = 28) => new(FontAssets.Face(FontAssets.Regular), size);

    [Fact]
    public void Text_is_drawn_inside_its_measured_ink_bounds()
    {
        using var image = new Image<Rgba32>(220, 60, new Rgba32(255, 255, 255, 255));
        var origin = new PointF(10.3f, 8);
        image.Mutate(ctx => ctx.DrawText("Hello, World!", Font(), Color.Black, origin));

        RectangleF ink = TextMeasurer.Measure("Hello, World!", Font(), new TextLayoutOptions { Origin = origin }).InkBounds;
        int changed = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (image[x, y].R == 255)
                {
                    continue;
                }

                changed++;
                Assert.True(x >= ink.Left - 1 && x < ink.Right + 1 && y >= ink.Top - 1 && y < ink.Bottom + 1, $"ink at ({x}, {y}) outside {ink}");
            }
        }

        Assert.True(changed > 300, $"only {changed} pixels drawn");
    }

    [Fact]
    public void Drawn_glyph_matches_its_rasterised_coverage()
    {
        // One glyph at an integer origin: every pixel is white blended towards black by exactly its coverage.
        FontFace face = FontAssets.Face(FontAssets.Regular);
        Font font = Font(40);
        using var image = new Image<Rgb24>(60, 60, new Rgb24(255, 255, 255));
        image.Mutate(ctx => ctx.DrawText("B", font, Color.Black, new TextLayoutOptions { Origin = new PointF(5, 50), VerticalAlignment = VerticalAlignment.Baseline }));
        GlyphBitmap bitmap = GlyphRasterizer.Render(face.GetOutline(face.GetGlyphIndex('B')), font.Scale, 0f, 0f);
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                int coverage = bitmap.Coverage[(y * bitmap.Width) + x];
                int expected = ((0 * coverage) + (255 * (255 - coverage)) + 127) / 255;
                Assert.Equal(expected, image[5 + bitmap.Left + x, 50 + bitmap.Top + y].R);
            }
        }
    }

    [Fact]
    public void Translucent_text_blends_once_where_glyphs_overlap()
    {
        // Negative tracking from kerning makes "AV" overlap; a 50% colour must never get darker than one blend.
        using var image = new Image<Rgb24>(120, 60, new Rgb24(255, 255, 255));
        var color = Color.FromRgba(0, 0, 0, 128);
        image.Mutate(ctx => ctx.DrawText("AVAVA", Font(40), color, new PointF(4, 4)));
        int darkest = 255;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                darkest = Math.Min(darkest, image[x, y].R);
            }
        }

        Assert.InRange(darkest, 126, 128);
    }

    [Fact]
    public void Antialias_off_draws_only_the_two_colours()
    {
        using var image = new Image<Rgb24>(120, 50, new Rgb24(255, 255, 255));
        image.Mutate(ctx => ctx.DrawText("Sample", Font(), Color.Black, new TextLayoutOptions { Origin = new PointF(2, 2) }, new DrawingOptions { Antialias = false }));
        var seen = new HashSet<byte>();
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                seen.Add(image[x, y].R);
            }
        }

        Assert.Equal(new HashSet<byte> { 0, 255 }, seen);
    }

    [Fact]
    public void Outline_surrounds_the_fill_in_its_own_colour()
    {
        using var image = new Image<Rgb24>(80, 70, new Rgb24(0, 0, 255));
        var pen = new Pen(Color.Black, 3);
        image.Mutate(ctx => ctx.DrawText("I", Font(48), Color.White, pen, new PointF(20, 5)));

        // Scan the middle row: blue, then black outline, then the white stem, then black, then blue.
        int y = 35;
        var runs = new List<char>();
        for (int x = 0; x < image.Width; x++)
        {
            Rgb24 p = image[x, y];
            char kind = p == new Rgb24(0, 0, 255) ? 'b' : p == new Rgb24(0, 0, 0) ? 'k' : p == new Rgb24(255, 255, 255) ? 'w' : '~';
            if (kind != '~' && (runs.Count == 0 || runs[^1] != kind))
            {
                runs.Add(kind);
            }
        }

        Assert.Equal("bkwkb", new string(runs.ToArray()));
    }

    [Fact]
    public void Font_text_and_bitmap_text_coexist()
    {
        using var image = new Image<Rgba32>(200, 60, new Rgba32(255, 255, 255, 255));
        image.Mutate(ctx => ctx
            .DrawText("bitmap", Color.Black, new PointF(2, 2))
            .DrawText("vector", Font(20), Color.Black, new PointF(2, 30)));
        Assert.True(DarkPixels(image, 0, 2, 200, 18) > 20, "bitmap text drawn");
        Assert.True(DarkPixels(image, 0, 30, 200, 60) > 20, "font text drawn");
        Assert.Equal(0, DarkPixels(image, 0, 19, 200, 29));
    }

    [Fact]
    public void Rendering_is_deterministic_and_pixel_formats_agree()
    {
        using var a = new Image<Rgba32>(160, 50, new Rgba32(255, 255, 255, 255));
        using var b = new Image<Rgba32>(160, 50, new Rgba32(255, 255, 255, 255));
        using var c = new Image<Rgb24>(160, 50, new Rgb24(255, 255, 255));
        var layout = new TextLayoutOptions { Origin = new PointF(3.6f, 4.2f), WrappingWidth = 150 };
        a.Mutate(ctx => ctx.DrawText("Wrapped text", Font(), Color.DarkBlue, layout));
        b.Mutate(ctx => ctx.DrawText("Wrapped text", Font(), Color.DarkBlue, layout));
        c.Mutate(ctx => ctx.DrawText("Wrapped text", Font(), Color.DarkBlue, layout));
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                Assert.Equal(a[x, y], b[x, y]);
                Assert.Equal(new Rgb24(a[x, y].R, a[x, y].G, a[x, y].B), c[x, y]);
            }
        }
    }

    [Fact]
    public void Text_partly_or_wholly_outside_the_image_is_clipped()
    {
        using var image = new Image<Rgb24>(40, 30, new Rgb24(255, 255, 255));
        image.Mutate(ctx => ctx
            .DrawText("Clipped text", Font(), Color.Black, new PointF(-20, -10))
            .DrawText("Gone", Font(), Color.Black, new PointF(5000, 5000))
            .DrawText("Gone", Font(), Color.Black, new PointF(-5000, 10)));
        int drawn = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                drawn += image[x, y].R < 255 ? 1 : 0;
            }
        }

        Assert.True(drawn > 20, "the visible part of the first text is drawn");
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        using var image = new Image<Rgb24>(10, 10);
        Assert.Throws<ArgumentNullException>(() => image.Mutate(ctx => ctx.DrawText(null!, Font(), Color.Black, PointF.Empty)));
        Assert.Throws<ArgumentNullException>(() => image.Mutate(ctx => ctx.DrawText("x", (Font)null!, Color.Black, PointF.Empty)));
        Assert.Throws<ArgumentException>(() => image.Mutate(ctx => ctx.DrawText("x", Font(), Color.Black, new PointF(float.NaN, 0))));
    }

    private static int DarkPixels(Image<Rgba32> image, int x0, int y0, int x1, int y1)
    {
        int count = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                count += image[x, y].R < 128 ? 1 : 0;
            }
        }

        return count;
    }
}
