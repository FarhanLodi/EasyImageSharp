namespace EasyImageSharp.Drawing;

/// <summary>
/// A fixed-width bitmap font: every glyph is 8 pixels wide and <see cref="GlyphHeight"/> rows tall, one
/// byte per row with the most significant bit being the leftmost pixel. <see cref="Default"/> is the
/// embedded 8x16 font covering printable ASCII (0x20-0x7E); characters outside a font's range render as
/// its fallback glyph. Custom fonts can be supplied through the constructor.
/// </summary>
public sealed partial class BitmapFont
{
    private static readonly byte[] BlankGlyph = new byte[64];

    private readonly byte[] glyphs;
    private readonly int fallbackIndex;

    /// <summary>
    /// Initializes a font from packed glyph rows.
    /// </summary>
    /// <param name="glyphRows">
    /// <c>glyphCount * glyphHeight</c> bytes: consecutive glyphs starting at <paramref name="firstChar"/>, each
    /// <paramref name="glyphHeight"/> rows of 8 pixels (MSB = leftmost). The array is copied.
    /// </param>
    /// <param name="glyphHeight">Rows per glyph, 1-64.</param>
    /// <param name="firstChar">The character of the first glyph; following glyphs map to consecutive characters.</param>
    /// <param name="fallbackChar">
    /// The character whose glyph stands in for characters the font does not contain; when it is itself
    /// outside the font, missing characters render blank.
    /// </param>
    public BitmapFont(byte[] glyphRows, int glyphHeight, char firstChar, char fallbackChar)
    {
        ArgumentNullException.ThrowIfNull(glyphRows);
        if (glyphHeight is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(glyphHeight), glyphHeight, "Glyph height must be between 1 and 64.");
        }

        if (glyphRows.Length == 0 || glyphRows.Length % glyphHeight != 0)
        {
            throw new ArgumentException("Glyph data length must be a positive multiple of the glyph height.", nameof(glyphRows));
        }

        int count = glyphRows.Length / glyphHeight;
        if (firstChar + count - 1 > char.MaxValue)
        {
            throw new ArgumentException("The glyph range extends past the last UTF-16 code unit.", nameof(glyphRows));
        }

        this.glyphs = (byte[])glyphRows.Clone();
        this.GlyphHeight = glyphHeight;
        this.FirstChar = firstChar;
        this.GlyphCount = count;
        this.fallbackIndex = fallbackChar >= firstChar && fallbackChar < firstChar + count ? fallbackChar - firstChar : -1;
    }

    /// <summary>The embedded 8x16 font: printable ASCII 0x20-0x7E plus a box glyph at 0x7F used for missing characters.</summary>
    public static BitmapFont Default { get; } = new(DefaultGlyphRows.ToArray(), 16, ' ', '\x7f');

    /// <summary>The width of every glyph in pixels (always 8).</summary>
    public int GlyphWidth => 8;

    /// <summary>The height of every glyph in pixels.</summary>
    public int GlyphHeight { get; }

    /// <summary>The first character the font contains.</summary>
    public char FirstChar { get; }

    /// <summary>The number of consecutive characters the font contains, starting at <see cref="FirstChar"/>.</summary>
    public int GlyphCount { get; }

    /// <summary>Whether the font has a glyph of its own for <paramref name="c"/> (rather than the fallback).</summary>
    public bool Contains(char c) => c >= this.FirstChar && c < this.FirstChar + this.GlyphCount;

    /// <summary>
    /// Returns the <see cref="GlyphHeight"/> row bytes of the glyph for <paramref name="c"/>, or the fallback
    /// glyph (blank when the font has none) for characters outside the font.
    /// </summary>
    public ReadOnlySpan<byte> GetGlyph(char c)
    {
        int index = this.Contains(c) ? c - this.FirstChar : this.fallbackIndex;
        return index < 0
            ? BlankGlyph.AsSpan(0, this.GlyphHeight)
            : this.glyphs.AsSpan(index * this.GlyphHeight, this.GlyphHeight);
    }

    /// <summary>
    /// Measures the pixel size of <paramref name="text"/> at the given integer scale: the widest line times
    /// <c>8 * scale</c> by the number of lines times <c>GlyphHeight * scale</c>. Line breaks (<c>\n</c>) start
    /// new lines, <c>\r</c> is ignored and tabs advance to the next multiple of four columns. An empty text
    /// measures as an empty size.
    /// </summary>
    public Size Measure(string text, int scale = 1)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (scale < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be at least 1.");
        }

        if (text.Length == 0)
        {
            return Size.Empty;
        }

        List<string> lines = SplitLines(text);
        int columns = 0;
        foreach (string line in lines)
        {
            columns = Math.Max(columns, line.Length);
        }

        return new Size(columns * this.GlyphWidth * scale, lines.Count * this.GlyphHeight * scale);
    }

    /// <summary>Splits text into display lines: <c>\n</c> breaks, <c>\r</c> dropped, tabs expanded to 4-column stops.</summary>
    internal static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (char c in text)
        {
            switch (c)
            {
                case '\n':
                    lines.Add(current.ToString());
                    current.Clear();
                    break;
                case '\r':
                    break;
                case '\t':
                    current.Append(' ', 4 - (current.Length % 4));
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        lines.Add(current.ToString());
        return lines;
    }
}
