using EasyImageSharp.Drawing;

namespace EasyImageSharp.Processing;

/// <summary>Convenience overloads for the vector drawing operations (default options, colours, brushes, circles, clipping).</summary>
public static partial class ProcessingExtensions
{
    // ----- Brush fills -----

    /// <inheritdoc cref="IImageProcessingContext.Fill(Brush, DrawingOptions)"/>
    public static IImageProcessingContext Fill(this IImageProcessingContext context, Brush brush)
        => context.Fill(brush, DrawingOptions.Default);

    /// <inheritdoc cref="IImageProcessingContext.Fill(Brush, VectorPath, DrawingOptions)"/>
    public static IImageProcessingContext Fill(this IImageProcessingContext context, Brush brush, VectorPath path)
        => context.Fill(brush, path, DrawingOptions.Default);

    /// <summary>Fills the inside of <paramref name="path"/> with <paramref name="color"/> under the non-zero rule.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public static IImageProcessingContext Fill(this IImageProcessingContext context, Color color, VectorPath path)
        => context.Fill(new SolidBrush(color), path, DrawingOptions.Default);

    /// <summary>Fills the inside of <paramref name="path"/> with <paramref name="color"/>; see <see cref="IImageProcessingContext.Fill(Brush, VectorPath, DrawingOptions)"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public static IImageProcessingContext Fill(this IImageProcessingContext context, Color color, VectorPath path, DrawingOptions options)
        => context.Fill(new SolidBrush(color), path, options);

    /// <inheritdoc cref="IImageProcessingContext.FillRectangle(Brush, RectangleF, DrawingOptions)"/>
    public static IImageProcessingContext FillRectangle(this IImageProcessingContext context, Brush brush, RectangleF rectangle)
        => context.FillRectangle(brush, rectangle, DrawingOptions.Default);

    /// <inheritdoc cref="IImageProcessingContext.FillEllipse(Brush, RectangleF, DrawingOptions)"/>
    public static IImageProcessingContext FillEllipse(this IImageProcessingContext context, Brush brush, RectangleF bounds)
        => context.FillEllipse(brush, bounds, DrawingOptions.Default);

    /// <summary>Fills the disc of the given <paramref name="radius"/> around <paramref name="center"/> with <paramref name="brush"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="brush"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative or not finite.</exception>
    public static IImageProcessingContext FillCircle(this IImageProcessingContext context, Brush brush, PointF center, float radius)
        => context.FillEllipse(brush, CircleBounds(center, radius), DrawingOptions.Default);

    /// <summary>Fills the disc of the given <paramref name="radius"/> around <paramref name="center"/> with <paramref name="brush"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="brush"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative or not finite.</exception>
    public static IImageProcessingContext FillCircle(this IImageProcessingContext context, Brush brush, PointF center, float radius, DrawingOptions options)
        => context.FillEllipse(brush, CircleBounds(center, radius), options);

    /// <inheritdoc cref="IImageProcessingContext.FillPolygon(Brush, ReadOnlySpan{PointF}, DrawingOptions)"/>
    public static IImageProcessingContext FillPolygon(this IImageProcessingContext context, Brush brush, params PointF[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        return context.FillPolygon(brush, points, DrawingOptions.Default);
    }

    /// <inheritdoc cref="IImageProcessingContext.FillPolygon(Brush, ReadOnlySpan{PointF}, DrawingOptions)"/>
    public static IImageProcessingContext FillPolygon(this IImageProcessingContext context, Brush brush, PointF[] points, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(points);
        return context.FillPolygon(brush, points, options);
    }

    // ----- Path strokes -----

    /// <inheritdoc cref="IImageProcessingContext.Draw(Pen, VectorPath, DrawingOptions)"/>
    public static IImageProcessingContext Draw(this IImageProcessingContext context, Pen pen, VectorPath path)
        => context.Draw(pen, path, DrawingOptions.Default);

    /// <summary>Strokes <paramref name="path"/> with a solid pen of the given colour and thickness (mitered joins, butt caps).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="thickness"/> is not a positive finite number.</exception>
    public static IImageProcessingContext Draw(this IImageProcessingContext context, Color color, float thickness, VectorPath path)
        => context.Draw(new Pen(color, thickness), path, DrawingOptions.Default);

    /// <summary>Strokes <paramref name="path"/> with a solid pen of the given colour and thickness (mitered joins, butt caps).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="thickness"/> is not a positive finite number.</exception>
    public static IImageProcessingContext Draw(this IImageProcessingContext context, Color color, float thickness, VectorPath path, DrawingOptions options)
        => context.Draw(new Pen(color, thickness), path, options);

    /// <summary>Strokes <paramref name="path"/> with a band of the given thickness painted by <paramref name="brush"/> (mitered joins, butt caps).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="brush"/> or <paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="thickness"/> is not a positive finite number.</exception>
    public static IImageProcessingContext Draw(this IImageProcessingContext context, Brush brush, float thickness, VectorPath path)
        => context.Draw(new Pen(brush) { Thickness = thickness }, path, DrawingOptions.Default);

    /// <summary>Strokes <paramref name="path"/> with a band of the given thickness painted by <paramref name="brush"/> (mitered joins, butt caps).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="brush"/>, <paramref name="path"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="thickness"/> is not a positive finite number.</exception>
    public static IImageProcessingContext Draw(this IImageProcessingContext context, Brush brush, float thickness, VectorPath path, DrawingOptions options)
        => context.Draw(new Pen(brush) { Thickness = thickness }, path, options);

    // ----- Pen shapes -----

    /// <summary>Strokes the segment from <paramref name="a"/> to <paramref name="b"/> with <paramref name="pen"/>, including its caps and dashes.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static IImageProcessingContext DrawLine(this IImageProcessingContext context, Pen pen, PointF a, PointF b, DrawingOptions options)
        => context.DrawLines(pen, [a, b], options);

    /// <inheritdoc cref="IImageProcessingContext.DrawLines(Pen, ReadOnlySpan{PointF}, DrawingOptions)"/>
    public static IImageProcessingContext DrawLines(this IImageProcessingContext context, Pen pen, PointF[] points, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(points);
        return context.DrawLines(pen, points.AsSpan(), options);
    }

    /// <inheritdoc cref="IImageProcessingContext.DrawPolygon(Pen, ReadOnlySpan{PointF}, DrawingOptions)"/>
    public static IImageProcessingContext DrawPolygon(this IImageProcessingContext context, Pen pen, PointF[] points, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(points);
        return context.DrawPolygon(pen, points.AsSpan(), options);
    }

    /// <summary>Strokes the circle of the given <paramref name="radius"/> around <paramref name="center"/> with <paramref name="pen"/>, inside the radius.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative or not finite.</exception>
    public static IImageProcessingContext DrawCircle(this IImageProcessingContext context, Pen pen, PointF center, float radius, DrawingOptions options)
        => context.DrawEllipse(pen, CircleBounds(center, radius), options);

    // ----- Clipping -----

    /// <summary>
    /// Runs <paramref name="operations"/> so that they only change pixels inside <paramref name="path"/> (non-zero
    /// rule, anti-aliased edge); see <see cref="IImageProcessingContext.Clip(VectorPath, DrawingOptions, Action{IImageProcessingContext})"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="operations"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="operations"/> changed the image size or frame count.</exception>
    public static IImageProcessingContext Clip(this IImageProcessingContext context, VectorPath path, Action<IImageProcessingContext> operations)
        => context.Clip(path, DrawingOptions.Default, operations);

    /// <summary>
    /// Runs <paramref name="operations"/> so that they only change pixels inside <paramref name="rectangle"/>; at
    /// integer coordinates exactly the pixels it names. See
    /// <see cref="IImageProcessingContext.Clip(VectorPath, DrawingOptions, Action{IImageProcessingContext})"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="operations"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A component of <paramref name="rectangle"/> is not finite.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="operations"/> changed the image size or frame count.</exception>
    public static IImageProcessingContext Clip(this IImageProcessingContext context, RectangleF rectangle, Action<IImageProcessingContext> operations)
        => context.Clip(VectorPath.FromRectangle(rectangle), DrawingOptions.Default, operations);
}
