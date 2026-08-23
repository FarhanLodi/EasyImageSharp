using System.Numerics;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Artistic and radial effects applied in place to single frames.</summary>
internal static class EffectOps
{
    // ----- Pixelate -----

    /// <summary>
    /// Replaces every <paramref name="size"/> x <paramref name="size"/> block of the region (anchored at the
    /// region's top-left corner) with the block's average colour. Blocks at the right/bottom edge are clipped.
    /// </summary>
    public static void Pixelate<TPixel>(ImageFrame<TPixel> frame, Rectangle region, int size)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (region.Width <= 0 || region.Height <= 0 || size <= 1)
        {
            return;
        }

        int width = region.Width;
        int height = region.Height;
        Rgba32[] pixels = RowProcessor.ReadRegion(frame, region);
        int blocksY = (height + size - 1) / size;
        ParallelRowIterator.IterateRows(width, blocksY, (start, end) =>
        {
            for (int by = start; by < end; by++)
            {
                int y0 = by * size;
                int y1 = Math.Min(height, y0 + size);
                for (int x0 = 0; x0 < width; x0 += size)
                {
                    int x1 = Math.Min(width, x0 + size);
                    Vector4 sum = Vector4.Zero;
                    for (int y = y0; y < y1; y++)
                    {
                        int row = y * width;
                        for (int x = x0; x < x1; x++)
                        {
                            Rgba32 p = pixels[row + x];
                            sum += new Vector4(p.R, p.G, p.B, p.A);
                        }
                    }

                    sum /= (x1 - x0) * (y1 - y0);
                    var average = new Rgba32(
                        RowProcessor.ClampToByte(sum.X),
                        RowProcessor.ClampToByte(sum.Y),
                        RowProcessor.ClampToByte(sum.Z),
                        RowProcessor.ClampToByte(sum.W));
                    for (int y = y0; y < y1; y++)
                    {
                        pixels.AsSpan((y * width) + x0, x1 - x0).Fill(average);
                    }
                }
            }
        });

        RowProcessor.WriteRegion(frame, region, pixels);
    }

    // ----- Oil paint -----

    /// <summary>
    /// Oil painting effect: within a square brush of radius <paramref name="brushSize"/> the intensities are
    /// quantised into <paramref name="levels"/> bins; each pixel takes the mean colour of the most populated bin.
    /// </summary>
    public static void OilPaint<TPixel>(ImageFrame<TPixel> frame, Rectangle region, int levels, int brushSize)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            return;
        }

        int width = region.Width;
        int height = region.Height;
        Rgba32[] source = RowProcessor.ReadRegion(frame, region);
        var intensity = new byte[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            Rgba32 p = source[i];
            intensity[i] = (byte)Math.Min(levels - 1, ((p.R + p.G + p.B) * levels) / (3 * 256));
        }

        var result = new Rgba32[source.Length];
        int radius = brushSize;
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            var counts = new int[levels];
            var sums = new Vector4[levels];
            for (int y = start; y < end; y++)
            {
                int y0 = Math.Max(0, y - radius);
                int y1 = Math.Min(height - 1, y + radius);
                for (int x = 0; x < width; x++)
                {
                    int x0 = Math.Max(0, x - radius);
                    int x1 = Math.Min(width - 1, x + radius);
                    Array.Clear(counts);
                    Array.Clear(sums);
                    for (int sy = y0; sy <= y1; sy++)
                    {
                        int row = sy * width;
                        for (int sx = x0; sx <= x1; sx++)
                        {
                            int bin = intensity[row + sx];
                            Rgba32 p = source[row + sx];
                            counts[bin]++;
                            sums[bin] += new Vector4(p.R, p.G, p.B, 0f);
                        }
                    }

                    int best = 0;
                    for (int i = 1; i < levels; i++)
                    {
                        if (counts[i] > counts[best])
                        {
                            best = i;
                        }
                    }

                    Vector4 mean = sums[best] / counts[best];
                    result[(y * width) + x] = new Rgba32(
                        RowProcessor.ClampToByte(mean.X),
                        RowProcessor.ClampToByte(mean.Y),
                        RowProcessor.ClampToByte(mean.Z),
                        source[(y * width) + x].A);
                }
            }
        });

        RowProcessor.WriteRegion(frame, region, result);
    }

    // ----- Vignette / glow -----

    /// <summary>
    /// Blends <paramref name="color"/> over the region with a weight that grows quadratically from 0 at the
    /// region's centre to 1 at the corners of the ellipse with radii <paramref name="radiusX"/> /
    /// <paramref name="radiusY"/> (weight = ((dx/rx)² + (dy/ry)²) / 2, clamped), scaled by the blend percentage.
    /// Non-positive radii default to the half-extent of the pixel centres, so the corner pixels get weight 1.
    /// </summary>
    public static void Vignette<TPixel>(ImageFrame<TPixel> frame, Rectangle region, Rgba32 color, float radiusX, float radiusY, GraphicsOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            return;
        }

        float centerX = region.X + ((region.Width - 1) * 0.5f);
        float centerY = region.Y + ((region.Height - 1) * 0.5f);
        float rx = radiusX > 0 ? radiusX : Math.Max(0.5f, (region.Width - 1) * 0.5f);
        float ry = radiusY > 0 ? radiusY : Math.Max(0.5f, (region.Height - 1) * 0.5f);
        float invRx2 = 1f / (rx * rx);
        float invRy2 = 1f / (ry * ry);
        RadialBlend(frame, region, color, options, (x, y) =>
        {
            float dx = x - centerX;
            float dy = y - centerY;
            float d2 = ((dx * dx * invRx2) + (dy * dy * invRy2)) * 0.5f;
            return Math.Clamp(d2, 0f, 1f);
        });
    }

    /// <summary>
    /// Blends <paramref name="color"/> over the region with a weight that falls off linearly from 1 at the
    /// region's centre to 0 at <paramref name="radius"/>, scaled by the blend percentage.
    /// </summary>
    public static void Glow<TPixel>(ImageFrame<TPixel> frame, Rectangle region, Rgba32 color, float radius, GraphicsOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            return;
        }

        float centerX = region.X + ((region.Width - 1) * 0.5f);
        float centerY = region.Y + ((region.Height - 1) * 0.5f);
        float r = radius > 0 ? radius : Math.Max(0.5f, (Math.Min(region.Width, region.Height) - 1) * 0.5f);
        RadialBlend(frame, region, color, options, (x, y) =>
        {
            float dx = x - centerX;
            float dy = y - centerY;
            float d = MathF.Sqrt((dx * dx) + (dy * dy)) / r;
            return Math.Clamp(1f - d, 0f, 1f);
        });
    }

    private static void RadialBlend<TPixel>(ImageFrame<TPixel> frame, Rectangle region, Rgba32 color, GraphicsOptions options, Func<int, int, float> weightAt)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        float blend = options.BlendPercentage;
        if (blend <= 0f || color.A == 0)
        {
            return;
        }

        PixelColorBlendingMode colorMode = options.ColorBlendingMode;
        PixelAlphaCompositionMode alphaMode = options.AlphaCompositionMode;
        Vector4 source = RowProcessor.ToUnitVector(color);
        RowProcessor.ProcessRows(frame, region, (row, y) =>
        {
            for (int i = 0; i < row.Length; i++)
            {
                float weight = weightAt(region.X + i, y) * blend;
                if (weight <= 0f)
                {
                    continue;
                }

                Vector4 s = source;
                s.W *= weight;
                row[i] = RowProcessor.FromUnitVector(PixelBlender.BlendUnit(RowProcessor.ToUnitVector(row[i]), s, colorMode, alphaMode));
            }
        });
    }

    // ----- Swizzle -----

    /// <summary>Builds a new frame by copying every source pixel to the position given by <paramref name="swizzler"/>.</summary>
    public static ImageFrame<TPixel> Swizzle<TPixel>(ImageFrame<TPixel> source, ISwizzler swizzler)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Size size = swizzler.DestinationSize;
        Guard.MustBePositive(size.Width, nameof(swizzler));
        Guard.MustBePositive(size.Height, nameof(swizzler));
        var dest = new ImageFrame<TPixel>(size.Width, size.Height);
        for (int y = 0; y < source.Height; y++)
        {
            Span<TPixel> row = source.GetRowSpan(y);
            for (int x = 0; x < source.Width; x++)
            {
                Point target = swizzler.Transform(new Point(x, y));
                if ((uint)target.X < (uint)size.Width && (uint)target.Y < (uint)size.Height)
                {
                    dest[target.X, target.Y] = row[x];
                }
            }
        }

        return dest;
    }
}
