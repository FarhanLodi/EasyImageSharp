using EasyImageSharp.Drawing;

namespace EasyImageSharp.Fonts;

/// <summary>
/// Where and how text is laid out: the origin and its alignment, word wrapping, line spacing, tab stops,
/// kerning and fallback faces for characters the font lacks.
/// </summary>
/// <remarks>
/// Horizontal alignment places each line relative to <see cref="Origin"/>'s X: lines start at it
/// (<see cref="HorizontalAlignment.Left"/>), are centred on it, or end at it. Vertical alignment places the
/// block of lines relative to its Y: the top of the first line's ascent, the middle of the block, the bottom of
/// the last line's descent, or the first line's baseline. To centre a caption in a box, put the origin at the
/// box centre and use centre alignment on both axes, with <see cref="WrappingWidth"/> set to the box width.
/// </remarks>
public sealed class TextLayoutOptions
{
    private readonly float wrappingWidth;
    private readonly float lineSpacing = 1f;
    private readonly float tabWidth = 4f;
    private readonly IReadOnlyList<FontFace> fallbackFaces = Array.Empty<FontFace>();

    /// <summary>The default options: origin (0, 0), top-left aligned, no wrapping, single spacing, kerning on.</summary>
    public static TextLayoutOptions Default { get; } = new();

    /// <summary>The anchor point the text is aligned to, in pixels. Defaults to (0, 0).</summary>
    public PointF Origin { get; init; }

    /// <summary>
    /// The width in pixels at which lines wrap, or 0 (the default) for no wrapping. Lines break at spaces,
    /// after hyphens and dashes, at zero-width spaces and soft hyphens, and between CJK ideographs and kana
    /// (never before closing punctuation or after opening punctuation); a word longer than the width is broken
    /// between characters. Trailing spaces do not count towards a line's width.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public float WrappingWidth
    {
        get => this.wrappingWidth;
        init => this.wrappingWidth = value >= 0f && float.IsFinite(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Wrapping width must be zero or a positive finite number.");
    }

    /// <summary>How each line is placed relative to <see cref="Origin"/>'s X. Defaults to <see cref="HorizontalAlignment.Left"/>.</summary>
    public HorizontalAlignment HorizontalAlignment { get; init; } = HorizontalAlignment.Left;

    /// <summary>How the block of lines is placed relative to <see cref="Origin"/>'s Y. Defaults to <see cref="VerticalAlignment.Top"/>.</summary>
    public VerticalAlignment VerticalAlignment { get; init; } = VerticalAlignment.Top;

    /// <summary>
    /// A multiplier on the font's line height (ascent + descent + line gap) giving the distance between
    /// baselines. Defaults to 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a positive finite number.</exception>
    public float LineSpacing
    {
        get => this.lineSpacing;
        init => this.lineSpacing = value > 0f && float.IsFinite(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Line spacing must be a positive finite number.");
    }

    /// <summary>
    /// The distance between tab stops, in multiples of the width of a space in the font. A tab advances to
    /// the next stop measured from the start of its line. Defaults to 4.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public float TabWidth
    {
        get => this.tabWidth;
        init => this.tabWidth = value >= 0f && float.IsFinite(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Tab width must be zero or a positive finite number.");
    }

    /// <summary>Whether pair kerning from the font is applied. Defaults to <see langword="true"/>.</summary>
    public bool Kerning { get; init; } = true;

    /// <summary>
    /// Faces tried in order for characters the font has no glyph for, at the same pixel size. A character no
    /// face covers is drawn with the font's missing-glyph box. Line metrics always come from the font itself.
    /// </summary>
    public IReadOnlyList<FontFace> FallbackFaces
    {
        get => this.fallbackFaces;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            foreach (FontFace face in value)
            {
                ArgumentNullException.ThrowIfNull(face, nameof(value));
            }

            this.fallbackFaces = value.ToArray();
        }
    }

    /// <summary>Returns a copy of these options with another <see cref="Origin"/>.</summary>
    public TextLayoutOptions WithOrigin(PointF origin) => new()
    {
        Origin = origin,
        WrappingWidth = this.WrappingWidth,
        HorizontalAlignment = this.HorizontalAlignment,
        VerticalAlignment = this.VerticalAlignment,
        LineSpacing = this.LineSpacing,
        TabWidth = this.TabWidth,
        Kerning = this.Kerning,
        FallbackFaces = this.FallbackFaces,
    };
}
