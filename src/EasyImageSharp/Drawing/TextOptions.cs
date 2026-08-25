namespace EasyImageSharp.Drawing;

/// <summary>Horizontal placement of a text block relative to its anchor point.</summary>
public enum HorizontalAlignment
{
    /// <summary>The anchor is the left edge of the block.</summary>
    Left,

    /// <summary>The anchor is the horizontal centre of the block.</summary>
    Center,

    /// <summary>The anchor is the right edge of the block.</summary>
    Right,
}

/// <summary>
/// Options for text rendered with a <see cref="BitmapFont"/>: integer scale, optional filled background
/// box, padding and alignment.
/// </summary>
public sealed class TextOptions
{
    private readonly int scale = 1;
    private readonly int padding = 2;
    private readonly BitmapFont font = BitmapFont.Default;

    /// <summary>The default options: scale 1, no background, 2 px padding, left aligned.</summary>
    public static TextOptions Default { get; } = new();

    /// <summary>The bitmap font to render with. Defaults to <see cref="BitmapFont.Default"/>.</summary>
    public BitmapFont Font
    {
        get => this.font;
        init => this.font = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Integer magnification of the glyphs, 1-8. With the default 8x16 font, scale 2 renders 16x32 pixel
    /// glyphs. Defaults to 1.
    /// </summary>
    public int Scale
    {
        get => this.scale;
        init => this.scale = value is >= 1 and <= 8
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Text scale must be between 1 and 8.");
    }

    /// <summary>
    /// When set, a filled box of this colour is drawn behind the text. The box starts at the text location
    /// and surrounds the glyphs with <see cref="Padding"/> pixels on every side. <see langword="null"/>
    /// (the default) draws no background.
    /// </summary>
    public Color? Background { get; init; }

    /// <summary>
    /// Space in pixels between the glyphs and the edge of the background box; ignored when
    /// <see cref="Background"/> is <see langword="null"/>. Defaults to 2.
    /// </summary>
    public int Padding
    {
        get => this.padding;
        init => this.padding = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Padding must be non-negative.");
    }

    /// <summary>
    /// How the text block is placed relative to the location's X coordinate; lines shorter than the widest
    /// line are aligned the same way inside the block. Defaults to <see cref="HorizontalAlignment.Left"/>.
    /// </summary>
    public HorizontalAlignment HorizontalAlignment { get; init; } = HorizontalAlignment.Left;

    /// <summary>
    /// Measures the block a text occupies with these options: the glyph area plus the padding on every
    /// side when <see cref="Background"/> is set. Line breaks (<c>\n</c>) start new lines; an empty text
    /// measures as an empty size.
    /// </summary>
    public Size Measure(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Size glyphs = this.Font.Measure(text, this.Scale);
        if (this.Background is null || glyphs.IsEmpty)
        {
            return glyphs;
        }

        return new Size(glyphs.Width + (2 * this.Padding), glyphs.Height + (2 * this.Padding));
    }
}
