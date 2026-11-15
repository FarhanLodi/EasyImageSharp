using System.Buffers;
using EasyImageSharp.Drawing;
using EasyImageSharp.Fonts.Layout;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Fonts.Rendering;

/// <summary>
/// Composites laid-out text into a frame. The coverage of every glyph is first summed (saturating) into one
/// mask, so overlapping glyphs and translucent colours blend exactly once, and the mask is then blended
/// source-over with the same painter as the shape drawing operations. An outline is a second mask of the glyphs
/// grown by the outline width, drawn first in its own colour.
/// </summary>
internal static class TextRenderer
{
    public static void Draw<TPixel>(ImageFrame<TPixel> frame, LaidOutText layout, Color color, Pen? outline, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (layout.Glyphs.Length == 0)
        {
            return;
        }

        if (outline is Pen pen)
        {
            // An outline wider than the em is clamped to it; it bounds the halo's rendering cost per glyph.
            DrawLayer(frame, layout, pen.Color, Math.Min(pen.Thickness, layout.SizeInPixels), options);
        }

        DrawLayer(frame, layout, color, 0f, options);
    }

    private static void DrawLayer<TPixel>(ImageFrame<TPixel> frame, LaidOutText layout, Color color, float outlineWidth, DrawingOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var painter = new FramePainter<TPixel>(frame, color, options.BlendPercentage);
        if (painter.IsNoOp)
        {
            return;
        }

        // Render (or fetch) every glyph and find the clipped extent of the whole layer.
        var placed = new (GlyphBitmap Bitmap, int X, int Y)[layout.Glyphs.Length];
        int count = 0;
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (PositionedGlyph glyph in layout.Glyphs)
        {
            OperationExecution.Checkpoint();
            if (!float.IsFinite(glyph.X) || !float.IsFinite(glyph.Baseline) || MathF.Abs(glyph.X) > 1e8f || MathF.Abs(glyph.Baseline) > 1e8f)
            {
                continue;
            }

            int quarter = (int)MathF.Round(glyph.X * GlyphCache.SubpixelSteps);
            int penX = (int)Math.Floor((double)quarter / GlyphCache.SubpixelSteps);
            int subpixel = quarter - (penX * GlyphCache.SubpixelSteps);
            int baseline = (int)MathF.Round(glyph.Baseline);
            GlyphBitmap bitmap = GlyphCache.Get(glyph.Face, glyph.Glyph, layout.SizeInPixels, subpixel, outlineWidth);
            if (bitmap.IsEmpty)
            {
                continue;
            }

            int x = penX + bitmap.Left;
            int y = baseline + bitmap.Top;
            if (x >= frame.Width || y >= frame.Height || x + bitmap.Width <= 0 || y + bitmap.Height <= 0)
            {
                continue;
            }

            placed[count++] = (bitmap, x, y);
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x + bitmap.Width);
            bottom = Math.Max(bottom, y + bitmap.Height);
        }

        if (count == 0)
        {
            return;
        }

        left = Math.Max(left, 0);
        top = Math.Max(top, 0);
        right = Math.Min(right, frame.Width);
        bottom = Math.Min(bottom, frame.Height);
        int width = right - left;
        int height = bottom - top;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int maskLength = checked(width * height);
        byte[] mask = ArrayPool<byte>.Shared.Rent(maskLength);
        try
        {
            Array.Clear(mask, 0, maskLength);
            for (int i = 0; i < count; i++)
            {
                (GlyphBitmap bitmap, int x, int y) = placed[i];
                int x0 = Math.Max(x, left);
                int x1 = Math.Min(x + bitmap.Width, right);
                int y0 = Math.Max(y, top);
                int y1 = Math.Min(y + bitmap.Height, bottom);
                for (int row = y0; row < y1; row++)
                {
                    ReadOnlySpan<byte> source = bitmap.Coverage.AsSpan(((row - y) * bitmap.Width) + (x0 - x), x1 - x0);
                    Span<byte> target = mask.AsSpan(((row - top) * width) + (x0 - left), x1 - x0);
                    for (int k = 0; k < source.Length; k++)
                    {
                        int sum = target[k] + source[k];
                        target[k] = sum > 255 ? (byte)255 : (byte)sum;
                    }
                }
            }

            bool antialias = options.Antialias;
            for (int row = 0; row < height; row++)
            {
                Span<byte> line = mask.AsSpan(row * width, width);
                if (!antialias)
                {
                    for (int k = 0; k < line.Length; k++)
                    {
                        line[k] = line[k] >= 128 ? (byte)255 : (byte)0;
                    }
                }

                // Blend only the stretch of the row that has any coverage.
                int first = line.IndexOfAnyExcept((byte)0);
                if (first < 0)
                {
                    continue;
                }

                int last = line.LastIndexOfAnyExcept((byte)0);
                painter.Blend(top + row, left + first, line.Slice(first, last - first + 1));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(mask);
        }
    }
}
