using EasyImageSharp.Drawing;
using EasyImageSharp.Fonts;
using EasyImageSharp.Fonts.Layout;
using Xunit;

namespace EasyImageSharp.Tests.Fonts;

/// <summary>Layout and measurement: advances, kerning, line breaking, alignment, tabs, spacing and fallback.</summary>
public class TextLayoutTests
{
    private static FontFace Face => FontAssets.Face(FontAssets.Regular);

    private static Font Font32 => new(Face, 32);

    [Fact]
    public void Pen_positions_are_advances_plus_kerning()
    {
        Font font = Font32;
        int a = Face.GetGlyphIndex('A');
        int v = Face.GetGlyphIndex('V');
        float scale = font.Scale;
        int kern = Face.GetKerning(a, v);
        Assert.True(kern < 0);

        LaidOutText kerned = TextLayout.Layout("AV", font, TextLayoutOptions.Default);
        Assert.Equal(0f, kerned.Glyphs[0].X, 3);
        Assert.Equal((Face.GetAdvanceWidth(a) + kern) * scale, kerned.Glyphs[1].X, 3);

        LaidOutText plain = TextLayout.Layout("AV", font, new TextLayoutOptions { Kerning = false });
        Assert.Equal(Face.GetAdvanceWidth(a) * scale, plain.Glyphs[1].X, 3);

        TextMeasurement measured = TextMeasurer.Measure("AV", font);
        Assert.Equal((Face.GetAdvanceWidth(a) + kern + Face.GetAdvanceWidth(v)) * scale, measured.Advance, 3);
        Assert.Equal(font.Ascent + font.Descent, measured.Bounds.Height, 3);
        Assert.Equal(0f, measured.Bounds.Y, 3);
    }

    [Fact]
    public void Wrapping_breaks_at_spaces_and_keeps_lines_within_the_width()
    {
        const string text = "The quick brown fox jumps over the lazy dog";
        float width = TextMeasurer.Measure("The quick brown", Font32).Advance + 1;
        TextMeasurement m = TextMeasurer.Measure(text, Font32, new TextLayoutOptions { WrappingWidth = width });
        Assert.Equal(["The quick brown ", "fox jumps over ", "the lazy dog"], m.Lines);
        Assert.True(m.Advance <= width);
        Assert.Equal((2 * Font32.LineHeight) + Font32.Ascent + Font32.Descent, m.Bounds.Height, 3);
    }

    [Fact]
    public void Words_longer_than_the_width_break_between_characters()
    {
        float width = TextMeasurer.Measure("abcd", Font32).Advance + 0.5f;
        TextMeasurement m = TextMeasurer.Measure("abcdefghij", Font32, new TextLayoutOptions { WrappingWidth = width });
        Assert.Equal(["abcd", "efgh", "ij"], m.Lines);
    }

    [Fact]
    public void Hyphens_offer_breaks_but_a_leading_minus_before_digits_does_not()
    {
        float width = Math.Max(TextMeasurer.Measure("well-", Font32).Advance, TextMeasurer.Measure("known", Font32).Advance) + 1;
        Assert.Equal(["well-", "known"], TextMeasurer.Measure("well-known", Font32, new TextLayoutOptions { WrappingWidth = width }).Lines);
        Assert.False(LineBreaking.CanBreakBetween('-', '5', ' '));
        Assert.True(LineBreaking.CanBreakBetween('-', 'k', 'l'));
        Assert.False(LineBreaking.CanBreakBetween('a', ' ', 'b'));
        Assert.True(LineBreaking.CanBreakBetween(' ', 'b', 'a'));
        Assert.False(LineBreaking.CanBreakBetween(0xA0, 'b', 'a'));
    }

    [Fact]
    public void Cjk_text_breaks_between_ideographs_but_not_before_closing_punctuation()
    {
        Assert.True(LineBreaking.CanBreakBetween(0x6F22, 0x5B57, 'x'));
        Assert.False(LineBreaking.CanBreakBetween(0x5B57, 0x3002, 0x6F22));
        Assert.False(LineBreaking.CanBreakBetween(0x300C, 0x6F22, 'x'));
        Assert.False(LineBreaking.CanBreakBetween(0x3042, 0x3063, 0x3042));

        // The subset has no CJK glyphs, so every ideograph is a missing-glyph box of the same width.
        string text = "\u6F22\u5B57\u6F22\u5B57\u3002\u6F22";
        float box = Face.GetAdvanceWidth(0) * Font32.Scale;
        TextMeasurement m = TextMeasurer.Measure(text, Font32, new TextLayoutOptions { WrappingWidth = (box * 4) + 1 });
        Assert.Equal(["\u6F22\u5B57\u6F22", "\u5B57\u3002\u6F22"], m.Lines);
    }

    [Fact]
    public void Soft_hyphen_is_invisible_unless_the_line_breaks_there()
    {
        const string text = "hyphen\u00ADation";
        LaidOutText unbroken = TextLayout.Layout(text, Font32, TextLayoutOptions.Default);
        Assert.Equal(11, unbroken.Glyphs.Length);

        float width = TextMeasurer.Measure("hyphen-", Font32).Advance + 1;
        LaidOutText broken = TextLayout.Layout(text, Font32, new TextLayoutOptions { WrappingWidth = width });
        Assert.Equal(2, broken.Lines.Length);
        Assert.Equal(12, broken.Glyphs.Length);
        int hyphen = Face.GetGlyphIndex(0x2010) is int h and not 0 ? h : Face.GetGlyphIndex('-');
        Assert.Equal(hyphen, broken.Glyphs[6].Glyph);
    }

    [Fact]
    public void Hard_breaks_split_lines_and_keep_empty_lines()
    {
        TextMeasurement m = TextMeasurer.Measure("one\ntwo\r\n\rthree\u2028four", Font32);
        Assert.Equal(["one", "two", "", "three", "four"], m.Lines);
        Assert.Equal(Font32.LineHeight, m.LineHeight, 4);
    }

    [Fact]
    public void Alignment_places_lines_and_the_block_relative_to_the_origin()
    {
        var origin = new PointF(200, 100);
        TextMeasurement left = TextMeasurer.Measure("Hello\nWorld!!", Font32, new TextLayoutOptions { Origin = origin });
        Assert.Equal(200f, left.Bounds.X, 3);
        Assert.Equal(100f, left.Bounds.Y, 3);

        TextMeasurement center = TextMeasurer.Measure("Hello\nWorld!!", Font32, new TextLayoutOptions
        {
            Origin = origin,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Assert.Equal(200f, center.Bounds.X + (center.Bounds.Width / 2), 3);
        Assert.Equal(100f, center.Bounds.Y + (center.Bounds.Height / 2), 3);

        TextMeasurement right = TextMeasurer.Measure("Hello\nWorld!!", Font32, new TextLayoutOptions
        {
            Origin = origin,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        });
        Assert.Equal(200f, right.Bounds.Right, 3);
        Assert.Equal(100f, right.Bounds.Bottom, 3);

        LaidOutText baseline = TextLayout.Layout("Hi", Font32, new TextLayoutOptions { Origin = origin, VerticalAlignment = VerticalAlignment.Baseline });
        Assert.Equal(100f, baseline.Glyphs[0].Baseline, 3);

        // Each line is aligned on its own: the shorter first line is centred too.
        LaidOutText lines = TextLayout.Layout("Hi\nWorld", Font32, new TextLayoutOptions { Origin = origin, HorizontalAlignment = HorizontalAlignment.Center });
        Assert.Equal(200f, lines.Lines[0].X + (lines.Lines[0].Width / 2), 3);
        Assert.Equal(200f, lines.Lines[1].X + (lines.Lines[1].Width / 2), 3);
    }

    [Fact]
    public void Tabs_advance_to_stops_measured_from_the_line_start()
    {
        float space = Face.GetAdvanceWidth(Face.GetGlyphIndex(' ')) * Font32.Scale;
        LaidOutText layout = TextLayout.Layout("a\tb\n\tc", Font32, new TextLayoutOptions { TabWidth = 4 });
        Assert.Equal(4 * space, layout.Glyphs[1].X, 3);
        Assert.Equal(4 * space, layout.Glyphs[2].X, 3);
    }

    [Fact]
    public void Line_spacing_scales_the_distance_between_baselines()
    {
        LaidOutText layout = TextLayout.Layout("a\nb", Font32, new TextLayoutOptions { LineSpacing = 1.5f });
        Assert.Equal(1.5f * Font32.LineHeight, layout.Glyphs[1].Baseline - layout.Glyphs[0].Baseline, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextLayoutOptions { LineSpacing = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextLayoutOptions { WrappingWidth = -1 });
    }

    [Fact]
    public void Missing_characters_come_from_fallback_faces()
    {
        FontFace bold = FontAssets.Face(FontAssets.Bold);
        Assert.False(Face.HasGlyph(0x2665));
        Assert.True(bold.HasGlyph(0x2665));

        LaidOutText without = TextLayout.Layout("a\u2665", Font32, TextLayoutOptions.Default);
        Assert.Equal(0, without.Glyphs[1].Glyph);
        Assert.Same(Face, without.Glyphs[1].Face);

        LaidOutText with = TextLayout.Layout("a\u2665", Font32, new TextLayoutOptions { FallbackFaces = [bold] });
        Assert.Same(bold, with.Glyphs[1].Face);
        Assert.Equal(bold.GetGlyphIndex(0x2665), with.Glyphs[1].Glyph);
        Assert.Same(Face, with.Glyphs[0].Face);
    }

    [Fact]
    public void Ignorable_characters_and_surrogates_are_handled()
    {
        // Zero-width joiner and a variation selector draw nothing; an unpaired surrogate becomes U+FFFD.
        LaidOutText layout = TextLayout.Layout("a\u200D\uFE0Fb\uD800", Font32, TextLayoutOptions.Default);
        Assert.Equal(3, layout.Glyphs.Length);
        Assert.Equal(Face.GetAdvanceWidth(Face.GetGlyphIndex('a')) * Font32.Scale, layout.Glyphs[1].X, 3);

        // A supplementary-plane character is one glyph.
        LaidOutText math = TextLayout.Layout("\U0001D400", Font32, TextLayoutOptions.Default);
        Assert.Single(math.Glyphs);
        Assert.Equal(Face.GetGlyphIndex('A'), math.Glyphs[0].Glyph);
    }

    [Fact]
    public void Empty_and_blank_texts_measure_sensibly()
    {
        TextMeasurement empty = TextMeasurer.Measure(string.Empty, Font32, new TextLayoutOptions { Origin = new PointF(5, 6) });
        Assert.Equal(0, empty.LineCount);
        Assert.Equal(new RectangleF(5, 6, 0, 0), empty.Bounds);
        Assert.True(empty.InkBounds.IsEmpty);

        TextMeasurement blank = TextMeasurer.Measure("   ", Font32);
        Assert.Equal(1, blank.LineCount);
        Assert.Equal(0f, blank.Advance);
        Assert.True(blank.InkBounds.IsEmpty);
    }

    [Fact]
    public void Ink_bounds_enclose_the_glyph_control_boxes()
    {
        TextMeasurement m = TextMeasurer.Measure("Ag", Font32, new TextLayoutOptions { Origin = new PointF(10, 20) });
        Assert.True(m.InkBounds.Top >= m.Bounds.Top - 1);
        Assert.True(m.InkBounds.Bottom > 20 + Font32.Ascent, "the descender of 'g' reaches below the baseline");
        Assert.True(m.InkBounds.Left >= 10);
        Assert.True(m.InkBounds.Right <= m.Bounds.Right + 1);
    }
}
