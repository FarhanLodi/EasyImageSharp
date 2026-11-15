using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Drawing;

/// <summary>Frame-level implementations of the drawing operations.</summary>
internal static partial class DrawOps
{
    public static void Fill<TPixel>(ImageFrame<TPixel> frame, Color color, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var painter = new FramePainter<TPixel>(frame, color, options.BlendPercentage);
        if (painter.IsNoOp)
        {
            return;
        }

        if (options.ClipPath is not null)
        {
            FillClipped(frame, painter, options);
            return;
        }

        if (painter.IsOpaque)
        {
            frame.PixelSpan.Fill(TPixel.FromRgba32(color.ToRgba32()));
            return;
        }

        byte[] fullRow = new byte[frame.Width];
        fullRow.AsSpan().Fill(255);
        for (int y = 0; y < frame.Height; y++)
        {
            painter.Blend(y, 0, fullRow);
        }
    }

    public static void FillRectangle<TPixel>(ImageFrame<TPixel> frame, Color color, RectangleF rectangle, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        rectangle = Normalize(rectangle);
        var rasterizer = new PolygonRasterizer();
        ShapeBuilder.AddRectangle(rasterizer, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
        Render(frame, rasterizer, color, options);
    }

    /// <summary>Strokes the rectangle's border on the inside of its bounds: the ring between the rectangle and the rectangle inset by the thickness.</summary>
    public static void DrawRectangle<TPixel>(ImageFrame<TPixel> frame, Color color, float thickness, RectangleF rectangle, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        rectangle = Normalize(rectangle);
        var rasterizer = new PolygonRasterizer();
        ShapeBuilder.AddRectangle(rasterizer, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
        ShapeBuilder.AddRectangle(
            rasterizer,
            rectangle.X + thickness,
            rectangle.Y + thickness,
            rectangle.Width - (2.0 * thickness),
            rectangle.Height - (2.0 * thickness),
            hole: true);
        Render(frame, rasterizer, color, options);
    }

    public static void FillEllipse<TPixel>(ImageFrame<TPixel> frame, Color color, RectangleF bounds, DrawingOptions options)
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
        Render(frame, rasterizer, color, options);
    }

    /// <summary>Strokes the ellipse on the inside of its bounds: the ring between the inscribed ellipse and the ellipse inscribed in the bounds inset by the thickness.</summary>
    public static void DrawEllipse<TPixel>(ImageFrame<TPixel> frame, Color color, float thickness, RectangleF bounds, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        bounds = Normalize(bounds);
        double cx = bounds.X + (bounds.Width / 2.0);
        double cy = bounds.Y + (bounds.Height / 2.0);
        double rx = bounds.Width / 2.0;
        double ry = bounds.Height / 2.0;
        var rasterizer = new PolygonRasterizer();
        ShapeBuilder.AddEllipse(rasterizer, cx, cy, rx, ry);
        ShapeBuilder.AddEllipse(rasterizer, cx, cy, rx - thickness, ry - thickness, hole: true);
        Render(frame, rasterizer, color, options);
    }

    public static void FillPolygon<TPixel>(ImageFrame<TPixel> frame, Color color, ReadOnlySpan<PointF> points, DrawingOptions options)
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
        Render(frame, rasterizer, color, options, options.FillRule);
    }

    public static void DrawPath<TPixel>(ImageFrame<TPixel> frame, Color color, float thickness, ReadOnlySpan<PointF> points, bool closed, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (points.Length < 2)
        {
            return;
        }

        var rasterizer = new PolygonRasterizer();
        ShapeBuilder.AddStroke(rasterizer, points, thickness, closed);
        Render(frame, rasterizer, color, options);
    }

    public static void DrawText<TPixel>(ImageFrame<TPixel> frame, string text, Color color, PointF location, TextOptions options, DrawingOptions drawingOptions)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (text.Length == 0)
        {
            return;
        }

        List<string> lines = BitmapFont.SplitLines(text);
        BitmapFont font = options.Font;
        int scale = options.Scale;
        int glyphWidth = font.GlyphWidth * scale;
        int glyphHeight = font.GlyphHeight * scale;
        int columns = 0;
        foreach (string line in lines)
        {
            columns = Math.Max(columns, line.Length);
        }

        int blockWidth = columns * glyphWidth;
        int blockHeight = lines.Count * glyphHeight;
        int padding = options.Background is null ? 0 : options.Padding;
        int boxWidth = blockWidth + (2 * padding);
        int boxHeight = blockHeight + (2 * padding);

        int anchorX = Point.RoundToInt(location.X);
        int boxY = Point.RoundToInt(location.Y);
        int boxX = options.HorizontalAlignment switch
        {
            HorizontalAlignment.Center => anchorX - (boxWidth / 2),
            HorizontalAlignment.Right => anchorX - boxWidth,
            _ => anchorX,
        };

        if (options.Background is Color background)
        {
            FillRectangle(frame, background, new RectangleF(boxX, boxY, boxWidth, boxHeight), drawingOptions);
        }

        var painter = new FramePainter<TPixel>(frame, color, drawingOptions.BlendPercentage);
        if (painter.IsNoOp || blockWidth == 0)
        {
            return;
        }

        CoverageMask? clip = CreateClipMask(frame, drawingOptions);
        if (clip is { IsEmpty: true })
        {
            return;
        }

        var sink = new ClippedSink<FramePainter<TPixel>>(painter, clip);

        int textLeft = boxX + padding;
        int textTop = boxY + padding;
        byte[] rowCoverage = new byte[blockWidth];
        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            string line = lines[lineIndex];
            if (line.Length == 0)
            {
                continue;
            }

            int lineWidth = line.Length * glyphWidth;
            int lineX = options.HorizontalAlignment switch
            {
                HorizontalAlignment.Center => textLeft + ((blockWidth - lineWidth) / 2),
                HorizontalAlignment.Right => textLeft + blockWidth - lineWidth,
                _ => textLeft,
            };

            int clipStart = Math.Max(0, lineX);
            int clipEnd = Math.Min(frame.Width, lineX + lineWidth);
            if (clipStart >= clipEnd)
            {
                continue;
            }

            int lineTop = textTop + (lineIndex * glyphHeight);
            for (int row = 0; row < font.GlyphHeight; row++)
            {
                int rowTop = lineTop + (row * scale);
                if (rowTop >= frame.Height || rowTop + scale <= 0)
                {
                    continue;
                }

                Span<byte> coverage = rowCoverage.AsSpan(0, lineWidth);
                coverage.Clear();
                bool any = false;
                for (int c = 0; c < line.Length; c++)
                {
                    byte bits = font.GetGlyph(line[c])[row];
                    if (bits == 0)
                    {
                        continue;
                    }

                    any = true;
                    int glyphLeft = c * glyphWidth;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        if ((bits & (0x80 >> bit)) != 0)
                        {
                            coverage.Slice(glyphLeft + (bit * scale), scale).Fill(255);
                        }
                    }
                }

                if (!any)
                {
                    continue;
                }

                ReadOnlySpan<byte> visible = coverage.Slice(clipStart - lineX, clipEnd - clipStart);
                for (int sy = 0; sy < scale; sy++)
                {
                    int y = rowTop + sy;
                    if ((uint)y < (uint)frame.Height)
                    {
                        sink.Blend(y, clipStart, visible);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Draws a text label with a filled background above the anchor rectangle (its bottom edge on the
    /// anchor's top edge, left aligned with it), or inside the anchor's top-left corner when there is no room
    /// above; the box is shifted as needed to stay inside the frame.
    /// </summary>
    public static void DrawLabel<TPixel>(ImageFrame<TPixel> frame, string text, Color textColor, Color background, RectangleF anchor, TextOptions options, DrawingOptions drawingOptions)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (text.Length == 0)
        {
            return;
        }

        anchor = Normalize(anchor);
        var labelOptions = new TextOptions
        {
            Font = options.Font,
            Scale = options.Scale,
            Padding = options.Padding,
            Background = background,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        Size box = labelOptions.Measure(text);
        int x = Point.RoundToInt(anchor.X);
        int anchorTop = Point.RoundToInt(anchor.Y);
        int y = anchorTop - box.Height;
        if (y < 0)
        {
            y = anchorTop;
        }

        x = Math.Max(0, Math.Min(x, frame.Width - box.Width));
        y = Math.Max(0, Math.Min(y, frame.Height - box.Height));
        DrawText(frame, text, textColor, new PointF(x, y), labelOptions, drawingOptions);
    }

    private static void Render<TPixel>(ImageFrame<TPixel> frame, PolygonRasterizer rasterizer, Color color, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
        => Render(frame, rasterizer, color, options, FillRule.NonZero);

    /// <summary>Paints the rasterised polygons in one colour under <paramref name="fillRule"/>, honouring the clip path of <paramref name="options"/>.</summary>
    internal static void Render<TPixel>(ImageFrame<TPixel> frame, PolygonRasterizer rasterizer, Color color, DrawingOptions options, FillRule fillRule)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (rasterizer.IsEmpty)
        {
            return;
        }

        var painter = new FramePainter<TPixel>(frame, color, options.BlendPercentage);
        if (painter.IsNoOp)
        {
            return;
        }

        RenderCoverage(frame, rasterizer, options, fillRule, painter);
    }

    /// <summary>
    /// Rasterises into <paramref name="sink"/>, multiplying by the clip mask of <paramref name="options"/> when it
    /// has a clip path. Without one this is exactly a whole-frame rasterisation.
    /// </summary>
    internal static void RenderCoverage<TPixel, TSink>(ImageFrame<TPixel> frame, PolygonRasterizer rasterizer, DrawingOptions options, FillRule fillRule, TSink sink)
        where TPixel : unmanaged, IPixel<TPixel>
        where TSink : struct, ICoverageSink
    {
        if (options.ClipPath is null)
        {
            rasterizer.Rasterize(frame.Width, frame.Height, options.Antialias, fillRule, ref sink);
            return;
        }

        CoverageMask? mask = CreateClipMask(frame, options);
        if (mask is null || mask.IsEmpty)
        {
            return;
        }

        var clipped = new ClippedSink<TSink>(sink, mask);
        rasterizer.Rasterize(mask.Bounds, options.Antialias, fillRule, ref clipped);
    }

    /// <summary>The coverage mask of the clip path of <paramref name="options"/> for this frame, or <see langword="null"/> without one.</summary>
    internal static CoverageMask? CreateClipMask<TPixel>(ImageFrame<TPixel> frame, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
        => options.GetClipMask(frame.Width, frame.Height);

    /// <summary>Fills the clip region of a whole-frame fill: every row of the mask, as coverage.</summary>
    private static void FillClipped<TPixel, TSink>(ImageFrame<TPixel> frame, TSink sink, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
        where TSink : struct, ICoverageSink
    {
        CoverageMask? mask = CreateClipMask(frame, options);
        if (mask is null || mask.IsEmpty)
        {
            return;
        }

        Rectangle r = mask.Bounds;
        for (int y = r.Y; y < r.Y + r.Height; y++)
        {
            sink.Blend(y, r.X, mask.GetRow(y));
        }
    }

    /// <summary>Flips rectangles with negative width or height so they describe the same area with positive size.</summary>
    internal static RectangleF Normalize(RectangleF rectangle)
    {
        float x = rectangle.X;
        float y = rectangle.Y;
        float w = rectangle.Width;
        float h = rectangle.Height;
        if (w < 0)
        {
            x += w;
            w = -w;
        }

        if (h < 0)
        {
            y += h;
            h = -h;
        }

        return new RectangleF(x, y, w, h);
    }
}
