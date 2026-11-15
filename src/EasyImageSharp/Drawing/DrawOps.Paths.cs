using System.Runtime.InteropServices;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Drawing;

/// <summary>Frame-level implementations of the brush, pen and vector path operations.</summary>
internal static partial class DrawOps
{
    /// <summary>
    /// The curve tolerance of dashed strokes relative to the drawing tolerance. The arc length lost to chords grows
    /// with the tolerance, so a twentieth of it keeps the accumulated dash drift around an ellipse of radius 50 near
    /// a hundredth of a pixel, for about four and a half times as many segments.
    /// </summary>
    private const double DashFlatteningScale = 0.05;

    /// <summary>Fills the whole frame (or the clip region) with a brush.</summary>
    public static void Fill<TPixel>(ImageFrame<TPixel> frame, Brush brush, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (brush is SolidBrush solid)
        {
            Fill(frame, solid.Color, options);
            return;
        }

        BrushShader? shader = brush.CreateShader();
        if (shader is null)
        {
            return;
        }

        var painter = new BrushPainter<TPixel>(frame, shader, options.BlendPercentage);
        if (painter.IsNoOp)
        {
            return;
        }

        if (options.ClipPath is not null)
        {
            FillClipped(frame, painter, options);
            return;
        }

        byte[] fullRow = new byte[frame.Width];
        fullRow.AsSpan().Fill(255);
        for (int y = 0; y < frame.Height; y++)
        {
            if ((y & 63) == 0)
            {
                OperationExecution.Checkpoint();
            }

            painter.Blend(y, 0, fullRow);
        }
    }

    /// <summary>Fills every figure of <paramref name="path"/> (open figures are closed) under the options' fill rule.</summary>
    public static void FillPath<TPixel>(ImageFrame<TPixel> frame, Brush brush, VectorPath path, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var rasterizer = new PolygonRasterizer();
        AddPathFill(rasterizer, path, options.CurveTolerance, frame.Width, frame.Height);
        Paint(frame, rasterizer, brush, options, options.FillRule);
    }

    /// <summary>Strokes every figure of <paramref name="path"/> with <paramref name="pen"/>, centred on the path.</summary>
    public static void StrokePath<TPixel>(ImageFrame<TPixel> frame, Pen pen, VectorPath path, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var rasterizer = new PolygonRasterizer();
        AddPathStroke(rasterizer, path, pen, options.CurveTolerance, frame.Width, frame.Height);
        Paint(frame, rasterizer, pen.EffectiveBrush, options, FillRule.NonZero);
    }

    public static void FillRectangle<TPixel>(ImageFrame<TPixel> frame, Brush brush, RectangleF rectangle, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        rectangle = Normalize(rectangle);
        var rasterizer = new PolygonRasterizer();
        ShapeBuilder.AddRectangle(rasterizer, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
        Paint(frame, rasterizer, brush, options, FillRule.NonZero);
    }

    public static void FillEllipse<TPixel>(ImageFrame<TPixel> frame, Brush brush, RectangleF bounds, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        bounds = Normalize(bounds);
        var rasterizer = new PolygonRasterizer();
        ShapeBuilder.AddEllipse(
            rasterizer,
            bounds.X + (bounds.Width / 2.0),
            bounds.Y + (bounds.Height / 2.0),
            bounds.Width / 2.0,
            bounds.Height / 2.0);
        Paint(frame, rasterizer, brush, options, FillRule.NonZero);
    }

    public static void FillPolygon<TPixel>(ImageFrame<TPixel> frame, Brush brush, ReadOnlySpan<PointF> points, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (points.Length < 3)
        {
            return;
        }

        var polygon = new PointD[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            polygon[i] = new PointD(points[i].X, points[i].Y);
        }

        var rasterizer = new PolygonRasterizer();
        rasterizer.AddPolygon(polygon);
        Paint(frame, rasterizer, brush, options, options.FillRule);
    }

    /// <summary>
    /// Strokes a rectangle's border inside its bounds with a pen. A pen with the default style draws the same ring
    /// as the colour overload; a styled pen strokes the rectangle inset by half the thickness, which covers the
    /// same band but honours joins and dashes. A rectangle no larger than the thickness is filled.
    /// </summary>
    public static void DrawRectangle<TPixel>(ImageFrame<TPixel> frame, Pen pen, RectangleF rectangle, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        rectangle = Normalize(rectangle);
        double t = pen.Thickness;
        if (!pen.HasDefaultStyle && rectangle.Width > t && rectangle.Height > t)
        {
            double h = t / 2;
            double left = rectangle.X + h, top = rectangle.Y + h;
            double right = (double)rectangle.X + rectangle.Width - h, bottom = (double)rectangle.Y + rectangle.Height - h;
            VectorPath inset = VectorPath.FromContours([[new(left, top), new(right, top), new(right, bottom), new(left, bottom)]]);
            StrokePath(frame, pen, inset, options);
            return;
        }

        var rasterizer = new PolygonRasterizer();
        ShapeBuilder.AddRectangle(rasterizer, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
        ShapeBuilder.AddRectangle(
            rasterizer,
            rectangle.X + t,
            rectangle.Y + t,
            rectangle.Width - (2.0 * t),
            rectangle.Height - (2.0 * t),
            hole: true);
        Paint(frame, rasterizer, pen.EffectiveBrush, options, FillRule.NonZero);
    }

    /// <summary>
    /// Strokes an ellipse inside its bounds with a pen: the colour overload's ring for a pen with the default style,
    /// otherwise the ellipse inscribed in the bounds inset by half the thickness, stroked centred.
    /// </summary>
    public static void DrawEllipse<TPixel>(ImageFrame<TPixel> frame, Pen pen, RectangleF bounds, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        bounds = Normalize(bounds);
        double t = pen.Thickness;
        double cx = bounds.X + (bounds.Width / 2.0);
        double cy = bounds.Y + (bounds.Height / 2.0);
        double rx = bounds.Width / 2.0;
        double ry = bounds.Height / 2.0;
        if (!pen.HasDefaultStyle && rx > t / 2 && ry > t / 2)
        {
            VectorPath ellipse = new PathBuilder()
                .AddEllipse(new PointF((float)cx, (float)cy), (float)(rx - (t / 2)), (float)(ry - (t / 2)))
                .Build();
            StrokePath(frame, pen, ellipse, options);
            return;
        }

        var rasterizer = new PolygonRasterizer();
        ShapeBuilder.AddEllipse(rasterizer, cx, cy, rx, ry);
        ShapeBuilder.AddEllipse(rasterizer, cx, cy, rx - t, ry - t, hole: true);
        Paint(frame, rasterizer, pen.EffectiveBrush, options, FillRule.NonZero);
    }

    /// <summary>
    /// Strokes a polyline or polygon with a pen: the colour overload's geometry for a pen with the default style,
    /// otherwise the pen's joins, caps and dashes.
    /// </summary>
    public static void DrawPath<TPixel>(ImageFrame<TPixel> frame, Pen pen, ReadOnlySpan<PointF> points, bool closed, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (points.Length < 2)
        {
            return;
        }

        if (!pen.HasDefaultStyle)
        {
            VectorPath path = closed ? VectorPath.FromPolygon(points) : VectorPath.FromLines(points);
            StrokePath(frame, pen, path, options);
            return;
        }

        var rasterizer = new PolygonRasterizer();
        ShapeBuilder.AddStroke(rasterizer, points, pen.Thickness, closed);
        Paint(frame, rasterizer, pen.EffectiveBrush, options, FillRule.NonZero);
    }

    /// <summary>Adds the flattened figures of <paramref name="path"/> as contours, keeping their direction.</summary>
    internal static void AddPathFill(PolygonRasterizer rasterizer, VectorPath path, double tolerance, int width, int height)
    {
        // Curves entirely outside the frame are replaced by their chords; that changes the winding number only
        // inside their control polygons, where no pixel is.
        var cull = new CullRect(-1, -1, width + 1, height + 1);
        foreach (FlatFigure figure in path.Flatten(tolerance, cull))
        {
            rasterizer.AddContour(CollectionsMarshal.AsSpan(figure.Points));
        }
    }

    /// <summary>Adds the stroke of <paramref name="path"/> as a union of polygons.</summary>
    internal static void AddPathStroke(PolygonRasterizer rasterizer, VectorPath path, Pen pen, double tolerance, int width, int height)
    {
        StrokeStyle style = pen.ToStrokeStyle(tolerance);

        // Nothing a piece of the stroke adds reaches further from the path than a miter tip (or a square cap's corner).
        double reach = (style.HalfWidth * Math.Max(style.MiterLimit, 1.5)) + 2;
        var cull = new CullRect(-reach, -reach, width + reach, height + reach);

        // Dashing measures arc length, so off-frame curves must keep their true length, and curves are flattened
        // more finely: inscribed chords are shorter than the curve, and the shortfall would accumulate along the
        // path and shift every later dash.
        List<FlatFigure> figures = style.Dashes is null
            ? path.Flatten(tolerance, cull)
            : path.Flatten(tolerance * DashFlatteningScale, null);
        Stroker.Stroke(rasterizer, figures, style, cull);
    }

    /// <summary>Paints the rasterised polygons with a brush; solid brushes take the single-colour path.</summary>
    internal static void Paint<TPixel>(ImageFrame<TPixel> frame, PolygonRasterizer rasterizer, Brush brush, DrawingOptions options, FillRule fillRule)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (rasterizer.IsEmpty)
        {
            return;
        }

        if (brush is SolidBrush solid)
        {
            Render(frame, rasterizer, solid.Color, options, fillRule);
            return;
        }

        BrushShader? shader = brush.CreateShader();
        if (shader is null)
        {
            return;
        }

        var painter = new BrushPainter<TPixel>(frame, shader, options.BlendPercentage);
        if (painter.IsNoOp)
        {
            return;
        }

        RenderCoverage(frame, rasterizer, options, fillRule, painter);
    }
}
