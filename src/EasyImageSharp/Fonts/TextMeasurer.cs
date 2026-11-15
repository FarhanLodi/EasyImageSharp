using EasyImageSharp.Fonts.Layout;

namespace EasyImageSharp.Fonts;

/// <summary>The measured extent of a laid-out text, in pixels.</summary>
public sealed class TextMeasurement
{
    internal TextMeasurement(RectangleF bounds, RectangleF inkBounds, float lineHeight, IReadOnlyList<string> lines)
    {
        this.Bounds = bounds;
        this.InkBounds = inkBounds;
        this.LineHeight = lineHeight;
        this.Lines = lines;
    }

    /// <summary>
    /// The layout box, positioned as the text would be drawn: horizontally from the leftmost line start to the
    /// rightmost line end (advance widths, trailing spaces excluded), vertically from the first line's ascent
    /// to the last line's descent. An empty text measures as an empty rectangle at the origin.
    /// </summary>
    public RectangleF Bounds { get; }

    /// <summary>
    /// The union of the drawn glyphs' outline control boxes: the area ink can fall in (an outline adds its width
    /// on every side). Empty when the text draws nothing, for example when it is only spaces.
    /// </summary>
    public RectangleF InkBounds { get; }

    /// <summary>The width of the widest line: the same as <see cref="Bounds"/>' width.</summary>
    public float Advance => this.Bounds.Width;

    /// <summary>The distance between consecutive baselines.</summary>
    public float LineHeight { get; }

    /// <summary>The number of lines after hard breaks and wrapping (0 for an empty text).</summary>
    public int LineCount => this.Lines.Count;

    /// <summary>The text of each line after wrapping, including any spaces at a wrap point.</summary>
    public IReadOnlyList<string> Lines { get; }
}

/// <summary>Measures text laid out with a <see cref="Font"/>, exactly as the font text drawing operations lay it out.</summary>
public static class TextMeasurer
{
    /// <summary>Measures <paramref name="text"/> at the origin with the default layout (top-left aligned, no wrapping, kerning on).</summary>
    public static TextMeasurement Measure(string text, Font font) => Measure(text, font, TextLayoutOptions.Default);

    /// <summary>Measures <paramref name="text"/> laid out with <paramref name="options"/>.</summary>
    public static TextMeasurement Measure(string text, Font font, TextLayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(options);
        LaidOutText layout = TextLayout.Layout(text, font, options);
        string[] lines = layout.Lines.Select(l => text.Substring(l.TextStart, l.TextLength)).ToArray();
        return new TextMeasurement(layout.Bounds, layout.ComputeInkBounds(), layout.LineAdvance, lines);
    }
}
