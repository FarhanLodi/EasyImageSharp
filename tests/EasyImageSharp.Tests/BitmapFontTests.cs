using EasyImageSharp.Drawing;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

public class BitmapFontTests
{
    [Fact]
    public void Default_CoversPrintableAscii_PlusFallbackBox()
    {
        BitmapFont font = BitmapFont.Default;
        Assert.Equal(8, font.GlyphWidth);
        Assert.Equal(16, font.GlyphHeight);
        Assert.Equal(' ', font.FirstChar);
        Assert.Equal(96, font.GlyphCount);
        Assert.True(font.Contains(' '));
        Assert.True(font.Contains('~'));
        Assert.True(font.Contains('\x7f'));
        Assert.False(font.Contains('\x1f'));
        Assert.False(font.Contains('\x80'));
        Assert.False(font.Contains('é'));
    }

    [Fact]
    public void EveryPrintableGlyph_HasInk_ExceptSpace_AndAllAreDistinct()
    {
        BitmapFont font = BitmapFont.Default;
        var seen = new Dictionary<string, char>();
        for (char c = ' '; c <= '\x7f'; c++)
        {
            byte[] rows = font.GetGlyph(c).ToArray();
            Assert.Equal(16, rows.Length);
            bool ink = rows.Any(static b => b != 0);
            Assert.True(c == ' ' ? !ink : ink, $"glyph 0x{(int)c:X2} '{c}' {(ink ? "has" : "lacks")} ink");

            string key = Convert.ToHexString(rows);
            Assert.False(seen.TryGetValue(key, out char other), $"glyph '{c}' is identical to '{other}'");
            seen[key] = c;
        }
    }

    [Fact]
    public void Glyphs_LeaveTheLastColumnBlank_ForSpacing()
    {
        BitmapFont font = BitmapFont.Default;
        for (char c = ' '; c <= '\x7f'; c++)
        {
            foreach (byte row in font.GetGlyph(c))
            {
                Assert.True((row & 0x01) == 0, $"glyph '{c}' uses column 7");
            }
        }
    }

    [Fact]
    public void Glyphs_RespectTheVerticalLayout()
    {
        // Row 0 is never used; capitals and digits sit on the baseline (row 12) and never descend.
        BitmapFont font = BitmapFont.Default;
        for (char c = ' '; c <= '\x7f'; c++)
        {
            Assert.Equal(0, font.GetGlyph(c)[0]);
        }

        foreach (char c in "ABCDEFGHIJKLMNOPRSTUVWXYZ0123456789")
        {
            ReadOnlySpan<byte> rows = font.GetGlyph(c);
            Assert.NotEqual(0, rows[2]);
            Assert.NotEqual(0, rows[12]);
            Assert.Equal(0, rows[13]);
            Assert.Equal(0, rows[1]);
        }

        foreach (char c in "gjpqy")
        {
            Assert.NotEqual(0, font.GetGlyph(c)[14]);
        }
    }

    [Fact]
    public void RenderedCharset_EveryCellHasInk_WithinItsCell_AndCellsAreDistinct()
    {
        const int Columns = 16;
        const int Scale = 2;
        var text = new System.Text.StringBuilder();
        for (int c = 0x21; c < 0x7f; c++)
        {
            text.Append((char)c);
            if ((c - 0x21) % Columns == Columns - 1)
            {
                text.Append('\n');
            }
        }

        string charset = text.ToString();
        int rows = charset.Split('\n').Length;
        using var image = new Image<L8>(Columns * 8 * Scale, rows * 16 * Scale, new L8(0));
        image.Mutate(ctx => ctx.DrawText(charset, Color.White, new PointF(0, 0), new TextOptions { Scale = Scale }));

        var cells = new Dictionary<string, char>();
        for (int c = 0x21; c < 0x7f; c++)
        {
            int index = c - 0x21;
            int cellX = (index % Columns) * 8 * Scale;
            int cellY = (index / Columns) * 16 * Scale;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            var bits = new System.Text.StringBuilder();
            for (int y = 0; y < 16 * Scale; y++)
            {
                for (int x = 0; x < 8 * Scale; x++)
                {
                    bool ink = image[cellX + x, cellY + y].PackedValue == 255;
                    bits.Append(ink ? '1' : '0');
                    if (ink)
                    {
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }

            Assert.True(maxX >= 0, $"glyph '{(char)c}' rendered empty");
            Assert.InRange(maxX, minX, (7 * Scale) - 1); // Column 7 (spacing) stays blank.
            Assert.InRange(maxY, minY, (16 * Scale) - 1);
            string key = bits.ToString();
            Assert.False(cells.TryGetValue(key, out char other), $"rendered '{(char)c}' equals '{other}'");
            cells[key] = (char)c;
        }

        // Nothing leaks outside the cells: the sheet is exactly the union of the cells, so no ink at the padding column of every cell.
        for (int c = 0x21; c < 0x7f; c++)
        {
            int index = c - 0x21;
            int cellX = (index % Columns) * 8 * Scale;
            int cellY = (index / Columns) * 16 * Scale;
            for (int y = 0; y < 16 * Scale; y++)
            {
                for (int x = 7 * Scale; x < 8 * Scale; x++)
                {
                    Assert.Equal(0, image[cellX + x, cellY + y].PackedValue);
                }
            }
        }
    }

    [Fact]
    public void GetGlyph_OutsideRange_ReturnsFallbackBox()
    {
        BitmapFont font = BitmapFont.Default;
        Assert.True(font.GetGlyph('é').SequenceEqual(font.GetGlyph('\x7f')));
        Assert.True(font.GetGlyph('中').SequenceEqual(font.GetGlyph('\x7f')));
        Assert.True(font.GetGlyph('\n').SequenceEqual(font.GetGlyph('\x7f')));
        Assert.False(font.GetGlyph('A').SequenceEqual(font.GetGlyph('\x7f')));
    }

    [Fact]
    public void Measure_CountsColumnsAndLines()
    {
        BitmapFont font = BitmapFont.Default;
        Assert.Equal(Size.Empty, font.Measure(string.Empty));
        Assert.Equal(new Size(8, 16), font.Measure("a"));
        Assert.Equal(new Size(40, 16), font.Measure("hello"));
        Assert.Equal(new Size(4 * 8 * 2, 2 * 16 * 2), font.Measure("ab\ncdef", 2));
        Assert.Equal(new Size(2 * 8, 3 * 16), font.Measure("a\r\nbc\r\n", 1)); // Trailing newline adds an empty line; CR ignored.
        Assert.Equal(new Size(5 * 8, 16), font.Measure("\tx")); // Tab expands to the next multiple of 4 columns.
        Assert.Throws<ArgumentOutOfRangeException>(() => font.Measure("a", 0));
        Assert.Throws<ArgumentNullException>(() => font.Measure(null!));
    }

    [Fact]
    public void TextOptions_Measure_AddsPaddingOnlyWithBackground()
    {
        Assert.Equal(new Size(16, 16), new TextOptions().Measure("ab"));
        Assert.Equal(new Size(16 + 6, 16 + 6), new TextOptions { Background = Color.Red, Padding = 3 }.Measure("ab"));
        Assert.Equal(new Size(32 + 4, 32 + 4), new TextOptions { Background = Color.Red, Scale = 2 }.Measure("ab"));
        Assert.Equal(Size.Empty, new TextOptions { Background = Color.Red }.Measure(string.Empty));
    }

    [Fact]
    public void CustomFont_RendersItsOwnGlyphs()
    {
        // Two 8x4 glyphs for 'a' (top row set) and 'b' (bottom row set); 'a' is the fallback.
        byte[] rows = [0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF];
        var font = new BitmapFont(rows, 4, 'a', 'a');
        Assert.Equal(2, font.GlyphCount);
        Assert.Equal(4, font.GlyphHeight);
        Assert.True(font.Contains('b'));
        Assert.False(font.Contains('c'));
        Assert.True(font.GetGlyph('c').SequenceEqual(font.GetGlyph('a')));
        Assert.Equal(new Size(16, 8), font.Measure("ab\nz", 1));

        using var image = new Image<Rgb24>(16, 4, new Rgb24(0, 0, 0));
        image.Mutate(ctx => ctx.DrawText("ab", Color.White, new PointF(0, 0), new TextOptions { Font = font }));
        Assert.Equal(new Rgb24(255, 255, 255), image[0, 0]);
        Assert.Equal(new Rgb24(0, 0, 0), image[0, 3]);
        Assert.Equal(new Rgb24(0, 0, 0), image[8, 0]);
        Assert.Equal(new Rgb24(255, 255, 255), image[15, 3]);

        // Rows are copied: mutating the source array does not change the font.
        rows[0] = 0;
        Assert.Equal(0xFF, font.GetGlyph('a')[0]);

        // A fallback outside the font renders blank.
        var noFallback = new BitmapFont(rows, 4, 'a', 'z');
        Assert.True(noFallback.GetGlyph('q').ToArray().All(static b => b == 0));
    }

    [Fact]
    public void CustomFont_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new BitmapFont(null!, 4, 'a', 'a'));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitmapFont(new byte[8], 0, 'a', 'a'));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitmapFont(new byte[65], 65, 'a', 'a'));
        Assert.Throws<ArgumentException>(() => new BitmapFont(new byte[7], 4, 'a', 'a'));
        Assert.Throws<ArgumentException>(() => new BitmapFont([], 4, 'a', 'a'));
        Assert.Throws<ArgumentException>(() => new BitmapFont(new byte[8], 4, '￿', 'a'));
        Assert.Throws<ArgumentNullException>(() => new TextOptions { Font = null! });
    }
}
