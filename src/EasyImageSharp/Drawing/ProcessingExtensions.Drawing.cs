using EasyImageSharp.Drawing;

namespace EasyImageSharp.Processing;

/// <summary>Convenience overloads for the drawing operations (default options, pens, circles, lines).</summary>
public static partial class ProcessingExtensions
{
    // ----- Fill -----

    /// <summary>Fills the whole frame with <paramref name="color"/>.</summary>
    public static IImageProcessingContext Fill(this IImageProcessingContext context, Color color)
        => context.Fill(color, DrawingOptions.Default);

    /// <summary>Fills the pixels of <paramref name="bounds"/> with <paramref name="color"/>.</summary>
    public static IImageProcessingContext Fill(this IImageProcessingContext context, Color color, Rectangle bounds)
        => context.FillRectangle(color, bounds, DrawingOptions.Default);

    /// <summary>Fills the pixels of <paramref name="bounds"/> with <paramref name="color"/>.</summary>
    public static IImageProcessingContext Fill(this IImageProcessingContext context, Color color, Rectangle bounds, DrawingOptions options)
        => context.FillRectangle(color, bounds, options);

    // ----- Rectangles -----

    /// <inheritdoc cref="IImageProcessingContext.FillRectangle(Color, RectangleF, DrawingOptions)"/>
    public static IImageProcessingContext FillRectangle(this IImageProcessingContext context, Color color, RectangleF rectangle)
        => context.FillRectangle(color, rectangle, DrawingOptions.Default);

    /// <inheritdoc cref="IImageProcessingContext.DrawRectangle(Color, float, RectangleF, DrawingOptions)"/>
    public static IImageProcessingContext DrawRectangle(this IImageProcessingContext context, Color color, float thickness, RectangleF rectangle)
        => context.DrawRectangle(color, thickness, rectangle, DrawingOptions.Default);

    /// <inheritdoc cref="IImageProcessingContext.DrawRectangle(Color, float, RectangleF, DrawingOptions)"/>
    public static IImageProcessingContext DrawRectangle(this IImageProcessingContext context, Pen pen, RectangleF rectangle)
        => context.DrawRectangle(pen.Color, pen.Thickness, rectangle, DrawingOptions.Default);

    /// <inheritdoc cref="IImageProcessingContext.DrawRectangle(Color, float, RectangleF, DrawingOptions)"/>
    public static IImageProcessingContext DrawRectangle(this IImageProcessingContext context, Pen pen, RectangleF rectangle, DrawingOptions options)
        => context.DrawRectangle(pen.Color, pen.Thickness, rectangle, options);

    // ----- Lines and polygons -----

    /// <summary>Strokes the segment from <paramref name="a"/> to <paramref name="b"/>; the stroke is centred on the segment with butt caps.</summary>
    public static IImageProcessingContext DrawLine(this IImageProcessingContext context, Color color, float thickness, PointF a, PointF b)
        => context.DrawLines(color, thickness, [a, b], DrawingOptions.Default);

    /// <summary>Strokes the segment from <paramref name="a"/> to <paramref name="b"/>; the stroke is centred on the segment with butt caps.</summary>
    public static IImageProcessingContext DrawLine(this IImageProcessingContext context, Color color, float thickness, PointF a, PointF b, DrawingOptions options)
        => context.DrawLines(color, thickness, [a, b], options);

    /// <summary>Strokes the segment from <paramref name="a"/> to <paramref name="b"/> with <paramref name="pen"/>.</summary>
    public static IImageProcessingContext DrawLine(this IImageProcessingContext context, Pen pen, PointF a, PointF b)
        => context.DrawLines(pen.Color, pen.Thickness, [a, b], DrawingOptions.Default);

    /// <inheritdoc cref="IImageProcessingContext.DrawLines(Color, float, ReadOnlySpan{PointF}, DrawingOptions)"/>
    public static IImageProcessingContext DrawLines(this IImageProcessingContext context, Color color, float thickness, params PointF[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        return context.DrawLines(color, thickness, points, DrawingOptions.Default);
    }

    /// <inheritdoc cref="IImageProcessingContext.DrawLines(Color, float, ReadOnlySpan{PointF}, DrawingOptions)"/>
    public static IImageProcessingContext DrawLines(this IImageProcessingContext context, Color color, float thickness, PointF[] points, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(points);
        return context.DrawLines(color, thickness, points, options);
    }

    /// <inheritdoc cref="IImageProcessingContext.DrawLines(Color, float, ReadOnlySpan{PointF}, DrawingOptions)"/>
    public static IImageProcessingContext DrawLines(this IImageProcessingContext context, Pen pen, params PointF[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        return context.DrawLines(pen.Color, pen.Thickness, points, DrawingOptions.Default);
    }

    /// <inheritdoc cref="IImageProcessingContext.DrawPolygon(Color, float, ReadOnlySpan{PointF}, DrawingOptions)"/>
    public static IImageProcessingContext DrawPolygon(this IImageProcessingContext context, Color color, float thickness, params PointF[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        return context.DrawPolygon(color, thickness, points, DrawingOptions.Default);
    }

    /// <inheritdoc cref="IImageProcessingContext.DrawPolygon(Color, float, ReadOnlySpan{PointF}, DrawingOptions)"/>
    public static IImageProcessingContext DrawPolygon(this IImageProcessingContext context, Color color, float thickness, PointF[] points, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(points);
        return context.DrawPolygon(color, thickness, points, options);
    }

    /// <inheritdoc cref="IImageProcessingContext.DrawPolygon(Color, float, ReadOnlySpan{PointF}, DrawingOptions)"/>
    public static IImageProcessingContext DrawPolygon(this IImageProcessingContext context, Pen pen, params PointF[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        return context.DrawPolygon(pen.Color, pen.Thickness, points, DrawingOptions.Default);
    }

    /// <inheritdoc cref="IImageProcessingContext.FillPolygon(Color, ReadOnlySpan{PointF}, DrawingOptions)"/>
    public static IImageProcessingContext FillPolygon(this IImageProcessingContext context, Color color, params PointF[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        return context.FillPolygon(color, points, DrawingOptions.Default);
    }

    /// <inheritdoc cref="IImageProcessingContext.FillPolygon(Color, ReadOnlySpan{PointF}, DrawingOptions)"/>
    public static IImageProcessingContext FillPolygon(this IImageProcessingContext context, Color color, PointF[] points, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(points);
        return context.FillPolygon(color, points, options);
    }

    // ----- Ellipses and circles -----

    /// <inheritdoc cref="IImageProcessingContext.DrawEllipse(Color, float, RectangleF, DrawingOptions)"/>
    public static IImageProcessingContext DrawEllipse(this IImageProcessingContext context, Color color, float thickness, RectangleF bounds)
        => context.DrawEllipse(color, thickness, bounds, DrawingOptions.Default);

    /// <inheritdoc cref="IImageProcessingContext.DrawEllipse(Color, float, RectangleF, DrawingOptions)"/>
    public static IImageProcessingContext DrawEllipse(this IImageProcessingContext context, Pen pen, RectangleF bounds)
        => context.DrawEllipse(pen.Color, pen.Thickness, bounds, DrawingOptions.Default);

    /// <inheritdoc cref="IImageProcessingContext.FillEllipse(Color, RectangleF, DrawingOptions)"/>
    public static IImageProcessingContext FillEllipse(this IImageProcessingContext context, Color color, RectangleF bounds)
        => context.FillEllipse(color, bounds, DrawingOptions.Default);

    /// <summary>
    /// Strokes the circle of the given <paramref name="radius"/> around <paramref name="center"/> with a band of
    /// the given <paramref name="thickness"/> lying inside the radius.
    /// </summary>
    public static IImageProcessingContext DrawCircle(this IImageProcessingContext context, Color color, float thickness, PointF center, float radius)
        => context.DrawEllipse(color, thickness, CircleBounds(center, radius), DrawingOptions.Default);

    /// <summary>
    /// Strokes the circle of the given <paramref name="radius"/> around <paramref name="center"/> with a band of
    /// the given <paramref name="thickness"/> lying inside the radius.
    /// </summary>
    public static IImageProcessingContext DrawCircle(this IImageProcessingContext context, Color color, float thickness, PointF center, float radius, DrawingOptions options)
        => context.DrawEllipse(color, thickness, CircleBounds(center, radius), options);

    /// <summary>Strokes the circle of the given <paramref name="radius"/> around <paramref name="center"/> with <paramref name="pen"/>, inside the radius.</summary>
    public static IImageProcessingContext DrawCircle(this IImageProcessingContext context, Pen pen, PointF center, float radius)
        => context.DrawEllipse(pen.Color, pen.Thickness, CircleBounds(center, radius), DrawingOptions.Default);

    /// <summary>Fills the disc of the given <paramref name="radius"/> around <paramref name="center"/>.</summary>
    public static IImageProcessingContext FillCircle(this IImageProcessingContext context, Color color, PointF center, float radius)
        => context.FillEllipse(color, CircleBounds(center, radius), DrawingOptions.Default);

    /// <summary>Fills the disc of the given <paramref name="radius"/> around <paramref name="center"/>.</summary>
    public static IImageProcessingContext FillCircle(this IImageProcessingContext context, Color color, PointF center, float radius, DrawingOptions options)
        => context.FillEllipse(color, CircleBounds(center, radius), options);

    // ----- Text -----

    /// <summary>Renders <paramref name="text"/> with the default bitmap font at scale 1, its top-left corner at <paramref name="location"/>.</summary>
    public static IImageProcessingContext DrawText(this IImageProcessingContext context, string text, Color color, PointF location)
        => context.DrawText(text, color, location, TextOptions.Default, DrawingOptions.Default);

    /// <inheritdoc cref="IImageProcessingContext.DrawText(string, Color, PointF, TextOptions, DrawingOptions)"/>
    public static IImageProcessingContext DrawText(this IImageProcessingContext context, string text, Color color, PointF location, TextOptions options)
        => context.DrawText(text, color, location, options, DrawingOptions.Default);

    /// <inheritdoc cref="IImageProcessingContext.DrawLabel(string, Color, Color, RectangleF, TextOptions, DrawingOptions)"/>
    public static IImageProcessingContext DrawLabel(this IImageProcessingContext context, string text, Color textColor, Color background, RectangleF anchor)
        => context.DrawLabel(text, textColor, background, anchor, TextOptions.Default, DrawingOptions.Default);

    /// <inheritdoc cref="IImageProcessingContext.DrawLabel(string, Color, Color, RectangleF, TextOptions, DrawingOptions)"/>
    public static IImageProcessingContext DrawLabel(this IImageProcessingContext context, string text, Color textColor, Color background, RectangleF anchor, TextOptions options)
        => context.DrawLabel(text, textColor, background, anchor, options, DrawingOptions.Default);

    /// <inheritdoc cref="IImageProcessingContext.DrawBoundingBoxes(IEnumerable{ValueTuple{Rectangle, string}}, Color, float, TextOptions, DrawingOptions)"/>
    public static IImageProcessingContext DrawBoundingBoxes(this IImageProcessingContext context, IEnumerable<(Rectangle Box, string? Label)> boxes, Color color, float thickness)
        => context.DrawBoundingBoxes(boxes, color, thickness, TextOptions.Default, DrawingOptions.Default);

    /// <inheritdoc cref="IImageProcessingContext.DrawBoundingBoxes(IEnumerable{ValueTuple{Rectangle, string}}, Color, float, TextOptions, DrawingOptions)"/>
    public static IImageProcessingContext DrawBoundingBoxes(this IImageProcessingContext context, IEnumerable<(Rectangle Box, string? Label)> boxes, Color color, float thickness, TextOptions labelOptions)
        => context.DrawBoundingBoxes(boxes, color, thickness, labelOptions, DrawingOptions.Default);

    /// <inheritdoc cref="IImageProcessingContext.DrawBoundingBoxes(IEnumerable{ValueTuple{Rectangle, string}}, Color, float, TextOptions, DrawingOptions)"/>
    public static IImageProcessingContext DrawBoundingBoxes(this IImageProcessingContext context, IEnumerable<(Rectangle Box, string? Label)> boxes, Color color, float thickness, DrawingOptions options)
        => context.DrawBoundingBoxes(boxes, color, thickness, TextOptions.Default, options);

    /// <summary>Draws unlabelled boxes with <see cref="IImageProcessingContext.DrawRectangle(Color, float, RectangleF, DrawingOptions)"/>.</summary>
    public static IImageProcessingContext DrawBoundingBoxes(this IImageProcessingContext context, IEnumerable<Rectangle> boxes, Color color, float thickness)
        => context.DrawBoundingBoxes(boxes, color, thickness, DrawingOptions.Default);

    /// <summary>Draws unlabelled boxes with <see cref="IImageProcessingContext.DrawRectangle(Color, float, RectangleF, DrawingOptions)"/>.</summary>
    public static IImageProcessingContext DrawBoundingBoxes(this IImageProcessingContext context, IEnumerable<Rectangle> boxes, Color color, float thickness, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        return context.DrawBoundingBoxes(boxes.Select(static b => (b, (string?)null)), color, thickness, TextOptions.Default, options);
    }

    private static RectangleF CircleBounds(PointF center, float radius)
    {
        if (!(radius >= 0f) || !float.IsFinite(radius))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "Radius must be a non-negative finite number.");
        }

        return new RectangleF(center.X - radius, center.Y - radius, 2f * radius, 2f * radius);
    }
}
