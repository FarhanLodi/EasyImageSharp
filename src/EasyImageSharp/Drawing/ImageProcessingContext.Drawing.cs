using EasyImageSharp.Drawing;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Drawing operations; see <c>IImageProcessingContext.Drawing.cs</c> for the contract.</summary>
internal sealed partial class ImageProcessingContext<TPixel>
{
    public IImageProcessingContext Fill(Color color, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return this.PerFrame(frame =>
        {
            DrawOps.Fill(frame, color, options);
            return frame;
        });
    }

    public IImageProcessingContext FillRectangle(Color color, RectangleF rectangle, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireFinite(rectangle, nameof(rectangle));
        return this.PerFrame(frame =>
        {
            DrawOps.FillRectangle(frame, color, rectangle, options);
            return frame;
        });
    }

    public IImageProcessingContext DrawRectangle(Color color, float thickness, RectangleF rectangle, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireThickness(thickness);
        RequireFinite(rectangle, nameof(rectangle));
        return this.PerFrame(frame =>
        {
            DrawOps.DrawRectangle(frame, color, thickness, rectangle, options);
            return frame;
        });
    }

    public IImageProcessingContext DrawLines(Color color, float thickness, ReadOnlySpan<PointF> points, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireThickness(thickness);
        PointF[] path = CopyPath(points);
        return this.PerFrame(frame =>
        {
            DrawOps.DrawPath(frame, color, thickness, path, closed: false, options);
            return frame;
        });
    }

    public IImageProcessingContext DrawPolygon(Color color, float thickness, ReadOnlySpan<PointF> points, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireThickness(thickness);
        PointF[] path = CopyPath(points);
        return this.PerFrame(frame =>
        {
            DrawOps.DrawPath(frame, color, thickness, path, closed: true, options);
            return frame;
        });
    }

    public IImageProcessingContext FillPolygon(Color color, ReadOnlySpan<PointF> points, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PointF[] polygon = CopyPath(points);
        return this.PerFrame(frame =>
        {
            DrawOps.FillPolygon(frame, color, polygon, options);
            return frame;
        });
    }

    public IImageProcessingContext DrawEllipse(Color color, float thickness, RectangleF bounds, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireThickness(thickness);
        RequireFinite(bounds, nameof(bounds));
        return this.PerFrame(frame =>
        {
            DrawOps.DrawEllipse(frame, color, thickness, bounds, options);
            return frame;
        });
    }

    public IImageProcessingContext FillEllipse(Color color, RectangleF bounds, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireFinite(bounds, nameof(bounds));
        return this.PerFrame(frame =>
        {
            DrawOps.FillEllipse(frame, color, bounds, options);
            return frame;
        });
    }

    public IImageProcessingContext DrawText(string text, Color color, PointF location, TextOptions options, DrawingOptions drawingOptions)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(drawingOptions);
        RequireFinite(location, nameof(location));
        return this.PerFrame(frame =>
        {
            DrawOps.DrawText(frame, text, color, location, options, drawingOptions);
            return frame;
        });
    }

    public IImageProcessingContext DrawLabel(string text, Color textColor, Color background, RectangleF anchor, TextOptions options, DrawingOptions drawingOptions)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(drawingOptions);
        RequireFinite(anchor, nameof(anchor));
        return this.PerFrame(frame =>
        {
            DrawOps.DrawLabel(frame, text, textColor, background, anchor, options, drawingOptions);
            return frame;
        });
    }

    public IImageProcessingContext DrawBoundingBoxes(IEnumerable<(Rectangle Box, string? Label)> boxes, Color color, float thickness, TextOptions labelOptions, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        ArgumentNullException.ThrowIfNull(labelOptions);
        ArgumentNullException.ThrowIfNull(options);
        RequireThickness(thickness);
        (Rectangle Box, string? Label)[] items = boxes as (Rectangle, string?)[] ?? boxes.ToArray();
        if (items.Length == 0)
        {
            return this;
        }

        // Black text on light colours, white on dark ones.
        Color textColor = PixelOps.Luminance8(color.ToRgba32()) > 140 ? Color.Black : Color.White;
        return this.PerFrame(frame =>
        {
            foreach ((Rectangle box, string? label) in items)
            {
                DrawOps.DrawRectangle(frame, color, thickness, box, options);
                if (!string.IsNullOrEmpty(label))
                {
                    DrawOps.DrawLabel(frame, label, textColor, color, box, labelOptions, options);
                }
            }

            return frame;
        });
    }

    // ----- Helpers -----

    private static void RequireThickness(float thickness)
    {
        if (!(thickness > 0f) || !float.IsFinite(thickness))
        {
            throw new ArgumentOutOfRangeException(nameof(thickness), thickness, "Thickness must be a positive finite number.");
        }
    }

    private static void RequireFinite(RectangleF rectangle, string parameterName)
    {
        if (!float.IsFinite(rectangle.X) || !float.IsFinite(rectangle.Y) || !float.IsFinite(rectangle.Width) || !float.IsFinite(rectangle.Height))
        {
            throw new ArgumentException("Rectangle components must be finite numbers.", parameterName);
        }
    }

    private static void RequireFinite(PointF point, string parameterName)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
        {
            throw new ArgumentException("Point coordinates must be finite numbers.", parameterName);
        }
    }

    private static PointF[] CopyPath(ReadOnlySpan<PointF> points)
    {
        foreach (PointF p in points)
        {
            RequireFinite(p, "points");
        }

        return points.ToArray();
    }
}
