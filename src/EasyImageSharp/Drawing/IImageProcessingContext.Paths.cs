using EasyImageSharp.Drawing;

namespace EasyImageSharp.Processing;

/// <summary>
/// Vector drawing: brushes (solid, linear, radial and conic gradients, image patterns), <see cref="VectorPath"/>
/// filling under the non-zero or even-odd rule, stroking with pens that have joins, caps and dashes, and
/// clipping. Like the other drawing operations these apply to every frame, composite source-over, and use
/// continuous coordinates in which pixel (x, y) covers the unit square from (x, y) to (x + 1, y + 1).
/// </summary>
public partial interface IImageProcessingContext
{
    /// <summary>
    /// Paints the whole frame with <paramref name="brush"/>, or only the inside of
    /// <see cref="DrawingOptions.ClipPath"/> when the options have one.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="brush"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    IImageProcessingContext Fill(Brush brush, DrawingOptions options)
        => throw new NotSupportedException("This processing context does not support vector drawing.");

    /// <summary>
    /// Fills the inside of <paramref name="path"/> with <paramref name="brush"/>. Every figure counts as closed (an
    /// open figure is closed by a straight line), and <see cref="DrawingOptions.FillRule"/> decides the inside of
    /// overlapping or self-crossing figures. Curves are approximated to within
    /// <see cref="DrawingOptions.CurveTolerance"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    IImageProcessingContext Fill(Brush brush, VectorPath path, DrawingOptions options)
        => throw new NotSupportedException("This processing context does not support vector drawing.");

    /// <summary>
    /// Strokes every figure of <paramref name="path"/> with <paramref name="pen"/>: a band of the pen's thickness
    /// centred on the path, with the pen's joins at the path's corners, its caps on the ends of open figures and of
    /// every dash, and round joins inside curves. Overlapping parts of the stroke are painted once. A figure
    /// with no length draws a dot with round caps, a square with square caps and nothing with butt caps.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The pen's dash pattern would cut the path into more than a million dashes.</exception>
    IImageProcessingContext Draw(Pen pen, VectorPath path, DrawingOptions options)
        => throw new NotSupportedException("This processing context does not support vector drawing.");

    /// <summary>Fills the area of <paramref name="rectangle"/> with <paramref name="brush"/>; fractional edges are anti-aliased.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="brush"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A component of <paramref name="rectangle"/> is not finite.</exception>
    IImageProcessingContext FillRectangle(Brush brush, RectangleF rectangle, DrawingOptions options)
        => throw new NotSupportedException("This processing context does not support vector drawing.");

    /// <summary>Fills the ellipse inscribed in <paramref name="bounds"/> with <paramref name="brush"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="brush"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A component of <paramref name="bounds"/> is not finite.</exception>
    IImageProcessingContext FillEllipse(Brush brush, RectangleF bounds, DrawingOptions options)
        => throw new NotSupportedException("This processing context does not support vector drawing.");

    /// <summary>
    /// Fills the polygon through <paramref name="points"/> with <paramref name="brush"/> under
    /// <see cref="DrawingOptions.FillRule"/>; fewer than three points draw nothing.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="brush"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A point is not finite.</exception>
    IImageProcessingContext FillPolygon(Brush brush, ReadOnlySpan<PointF> points, DrawingOptions options)
        => throw new NotSupportedException("This processing context does not support vector drawing.");

    /// <summary>
    /// Strokes the border of <paramref name="rectangle"/> inside its bounds with <paramref name="pen"/>. With the
    /// pen's default style this draws exactly what <see cref="DrawRectangle(Color, float, RectangleF, DrawingOptions)"/>
    /// draws, painted with the pen's brush; a pen with other joins or a dash pattern strokes the rectangle inset by
    /// half the thickness, which lays the band in the same place.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A component of <paramref name="rectangle"/> is not finite, or the pen's dash pattern would cut the outline into
    /// more than a million dashes.
    /// </exception>
    IImageProcessingContext DrawRectangle(Pen pen, RectangleF rectangle, DrawingOptions options)
        => throw new NotSupportedException("This processing context does not support vector drawing.");

    /// <summary>
    /// Strokes the ellipse inscribed in <paramref name="bounds"/> inside the bounds with <paramref name="pen"/>.
    /// With the pen's default style this draws the band of
    /// <see cref="DrawEllipse(Color, float, RectangleF, DrawingOptions)"/>; a styled pen strokes the ellipse
    /// inscribed in the bounds inset by half the thickness.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A component of <paramref name="bounds"/> is not finite, or the pen's dash pattern would cut the outline into
    /// more than a million dashes.
    /// </exception>
    IImageProcessingContext DrawEllipse(Pen pen, RectangleF bounds, DrawingOptions options)
        => throw new NotSupportedException("This processing context does not support vector drawing.");

    /// <summary>
    /// Strokes the open polyline through <paramref name="points"/> with <paramref name="pen"/>, centred on the path,
    /// with the pen's joins, caps and dashes. With the pen's default style the result is exactly that of
    /// <see cref="DrawLines(Color, float, ReadOnlySpan{PointF}, DrawingOptions)"/>. Fewer than two points draw nothing.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A point is not finite, or the pen's dash pattern would cut the path into more than a million dashes.
    /// </exception>
    IImageProcessingContext DrawLines(Pen pen, ReadOnlySpan<PointF> points, DrawingOptions options)
        => throw new NotSupportedException("This processing context does not support vector drawing.");

    /// <summary>
    /// Strokes the closed polygon through <paramref name="points"/> with <paramref name="pen"/>, centred on its
    /// edges, with the pen's joins and dashes. With the pen's default style the result is exactly that of
    /// <see cref="DrawPolygon(Color, float, ReadOnlySpan{PointF}, DrawingOptions)"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A point is not finite, or the pen's dash pattern would cut the path into more than a million dashes.
    /// </exception>
    IImageProcessingContext DrawPolygon(Pen pen, ReadOnlySpan<PointF> points, DrawingOptions options)
        => throw new NotSupportedException("This processing context does not support vector drawing.");

    /// <summary>
    /// Runs <paramref name="operations"/> so that they only change pixels inside <paramref name="path"/>: they are
    /// applied to a copy of the image, and the copy is blended back through the path's coverage mask (filled with
    /// <see cref="DrawingOptions.ClipFillRule"/>, anti-aliased when <see cref="DrawingOptions.Antialias"/> is set;
    /// the options' own clip path, if any, further limits the region). Any operation may run inside, including
    /// filters such as blurs, which see the whole unclipped image as their input.
    /// </summary>
    /// <remarks>
    /// The copy costs one extra image of memory for the duration of the call. Operations inside must keep the
    /// image's size and frame count.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="operations"/> changed the image size or frame count.</exception>
    IImageProcessingContext Clip(VectorPath path, DrawingOptions options, Action<IImageProcessingContext> operations)
        => throw new NotSupportedException("This processing context does not support vector drawing.");
}
