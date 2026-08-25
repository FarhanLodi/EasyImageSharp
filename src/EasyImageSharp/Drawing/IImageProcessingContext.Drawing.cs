using EasyImageSharp.Drawing;

namespace EasyImageSharp.Processing;

/// <summary>
/// Annotation drawing: anti-aliased shapes, strokes and bitmap-font text composited source-over onto every
/// frame. Coordinates are continuous: pixel (x, y) covers the unit square from (x, y) to (x + 1, y + 1),
/// so a rectangle at integer coordinates fills exactly the pixels it names.
/// </summary>
public partial interface IImageProcessingContext
{
    /// <summary>Fills the whole frame with <paramref name="color"/> (blended source-over when the colour or <see cref="DrawingOptions.BlendPercentage"/> is not opaque).</summary>
    IImageProcessingContext Fill(Color color, DrawingOptions options);

    /// <summary>Fills the area of <paramref name="rectangle"/>; fractional edges are anti-aliased.</summary>
    IImageProcessingContext FillRectangle(Color color, RectangleF rectangle, DrawingOptions options);

    /// <summary>
    /// Strokes the border of <paramref name="rectangle"/> with a band of the given <paramref name="thickness"/>
    /// lying inside its bounds, so at integer coordinates the outline occupies exactly the outermost pixels of
    /// the rectangle and never spills outside it. Thicknesses of at least half the smaller dimension fill it.
    /// </summary>
    IImageProcessingContext DrawRectangle(Color color, float thickness, RectangleF rectangle, DrawingOptions options);

    /// <summary>
    /// Strokes an open polyline through <paramref name="points"/>; the stroke is centred on the path with
    /// butt caps and mitered joins (bevelled at sharp angles). Fewer than two distinct points draw nothing.
    /// For a crisp one-pixel line at integer coordinates, place the path on pixel centres (add 0.5).
    /// </summary>
    IImageProcessingContext DrawLines(Color color, float thickness, ReadOnlySpan<PointF> points, DrawingOptions options);

    /// <summary>Strokes the closed polygon through <paramref name="points"/> (the last point connects back to the first) with a stroke centred on its edges.</summary>
    IImageProcessingContext DrawPolygon(Color color, float thickness, ReadOnlySpan<PointF> points, DrawingOptions options);

    /// <summary>Fills the polygon through <paramref name="points"/> using the non-zero winding rule; fewer than three points draw nothing.</summary>
    IImageProcessingContext FillPolygon(Color color, ReadOnlySpan<PointF> points, DrawingOptions options);

    /// <summary>
    /// Strokes the ellipse inscribed in <paramref name="bounds"/> with a band of the given
    /// <paramref name="thickness"/> lying inside the ellipse (between it and the ellipse inscribed in the
    /// bounds shrunk by the thickness on every side).
    /// </summary>
    IImageProcessingContext DrawEllipse(Color color, float thickness, RectangleF bounds, DrawingOptions options);

    /// <summary>Fills the ellipse inscribed in <paramref name="bounds"/>.</summary>
    IImageProcessingContext FillEllipse(Color color, RectangleF bounds, DrawingOptions options);

    /// <summary>
    /// Renders <paramref name="text"/> with the bitmap font in <paramref name="options"/>. <paramref name="location"/>
    /// is the top edge and the left/centre/right anchor (per <see cref="TextOptions.HorizontalAlignment"/>) of
    /// the text block, including the padding when a background is drawn. Text is never anti-aliased.
    /// </summary>
    IImageProcessingContext DrawText(string text, Color color, PointF location, TextOptions options, DrawingOptions drawingOptions);

    /// <summary>
    /// Draws a text label on a filled <paramref name="background"/> box just above <paramref name="anchor"/>
    /// (left aligned with it, its bottom on the anchor's top edge), or inside the anchor's top-left corner
    /// when it would not fit above; the box is shifted as needed to stay within the image. Scale, font and
    /// padding come from <paramref name="options"/>.
    /// </summary>
    IImageProcessingContext DrawLabel(string text, Color textColor, Color background, RectangleF anchor, TextOptions options, DrawingOptions drawingOptions);

    /// <summary>
    /// Draws each box with <see cref="DrawRectangle(Color, float, RectangleF, DrawingOptions)"/> and, when
    /// its label is not <see langword="null"/> or empty, a label above it (see
    /// <see cref="DrawLabel(string, Color, Color, RectangleF, TextOptions, DrawingOptions)"/>) on a box of the
    /// same colour with black or white text chosen for contrast. Font, scale and padding of the labels come
    /// from <paramref name="labelOptions"/>.
    /// </summary>
    IImageProcessingContext DrawBoundingBoxes(IEnumerable<(Rectangle Box, string? Label)> boxes, Color color, float thickness, TextOptions labelOptions, DrawingOptions options);
}
