using System.Runtime.InteropServices;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>
/// Conversions between frames and the single-channel byte planes the document operators work on:
/// luminance planes, binary masks (dark = foreground) and grey/binary write-back. <see cref="L8"/> frames use
/// direct byte copies; other formats go through <see cref="Rgba32"/> row conversions in parallel.
/// </summary>
internal static class DocumentPlanes
{
    /// <summary>The luminance below which a pixel counts as ink (foreground) in binary operations.</summary>
    public const byte InkThreshold = 128;

    /// <summary>Extracts the BT.709 luminance of every pixel as a row-major byte plane.</summary>
    public static byte[] Luminance<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        var plane = new byte[width * height];
        if (typeof(TPixel) == typeof(L8))
        {
            MemoryMarshal.AsBytes(frame.PixelSpan).CopyTo(plane);
            return plane;
        }

        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            var row = new Rgba32[width];
            for (int y = start; y < end; y++)
            {
                PixelOps.ToRgba32<TPixel>(frame.GetRowSpan(y), row);
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    plane[offset + x] = PixelOps.Luminance8(row[x]);
                }
            }
        });
        return plane;
    }

    /// <summary>Extracts a dark-pixel mask (1 = luminance below <see cref="InkThreshold"/>).</summary>
    public static byte[] InkMask<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
        => InkMask(Luminance(frame));

    public static byte[] InkMask(byte[] luminance)
    {
        var mask = new byte[luminance.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            mask[i] = luminance[i] < InkThreshold ? (byte)1 : (byte)0;
        }

        return mask;
    }

    /// <summary>Writes a grey plane back into the frame, keeping each pixel's alpha.</summary>
    public static void WriteGray<TPixel>(ImageFrame<TPixel> frame, byte[] plane)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        if (typeof(TPixel) == typeof(L8))
        {
            plane.AsSpan(0, width * height).CopyTo(MemoryMarshal.AsBytes(frame.PixelSpan));
            return;
        }

        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            var row = new Rgba32[width];
            for (int y = start; y < end; y++)
            {
                Span<TPixel> pixels = frame.GetRowSpan(y);
                PixelOps.ToRgba32<TPixel>(pixels, row);
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    byte v = plane[offset + x];
                    row[x] = new Rgba32(v, v, v, row[x].A);
                }

                PixelOps.FromRgba32<TPixel>(row, pixels);
            }
        });
    }

    /// <summary>Writes a mask back as black (1) / white (0) opaque pixels.</summary>
    public static void WriteBinary<TPixel>(ImageFrame<TPixel> frame, byte[] mask)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        TPixel white = TPixel.FromRgba32(Rgba32.White);
        TPixel black = TPixel.FromRgba32(Rgba32.Black);
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                Span<TPixel> pixels = frame.GetRowSpan(y);
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    pixels[x] = mask[offset + x] != 0 ? black : white;
                }
            }
        });
    }

    /// <summary>Paints every pixel whose mask entry is non-zero with <paramref name="color"/>, leaving the rest untouched.</summary>
    public static void PaintWhere<TPixel>(ImageFrame<TPixel> frame, byte[] mask, Rgba32 color)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        TPixel paint = TPixel.FromRgba32(color);
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                Span<TPixel> pixels = frame.GetRowSpan(y);
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (mask[offset + x] != 0)
                    {
                        pixels[x] = paint;
                    }
                }
            }
        });
    }

    /// <summary>Applies a per-channel lookup table to R, G and B, keeping alpha.</summary>
    public static void ApplyLut<TPixel>(ImageFrame<TPixel> frame, byte[] lut)
        where TPixel : unmanaged, IPixel<TPixel>
        => ApplyLut(frame, lut, lut, lut);

    public static void ApplyLut<TPixel>(ImageFrame<TPixel> frame, byte[] lutR, byte[] lutG, byte[] lutB)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        int height = frame.Height;
        if (typeof(TPixel) == typeof(L8))
        {
            Span<byte> bytes = MemoryMarshal.AsBytes(frame.PixelSpan);
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = lutR[bytes[i]];
            }

            return;
        }

        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            var row = new Rgba32[width];
            for (int y = start; y < end; y++)
            {
                Span<TPixel> pixels = frame.GetRowSpan(y);
                PixelOps.ToRgba32<TPixel>(pixels, row);
                for (int x = 0; x < width; x++)
                {
                    Rgba32 p = row[x];
                    row[x] = new Rgba32(lutR[p.R], lutG[p.G], lutB[p.B], p.A);
                }

                PixelOps.FromRgba32<TPixel>(row, pixels);
            }
        });
    }

    /// <summary>Histogram of a byte plane.</summary>
    public static int[] Histogram(byte[] plane)
    {
        var histogram = new int[256];
        foreach (byte v in plane)
        {
            histogram[v]++;
        }

        return histogram;
    }

    /// <summary>The value at which the cumulative histogram reaches <paramref name="percentile"/> (0-100).</summary>
    public static int Percentile(int[] histogram, long total, double percentile)
    {
        double target = total * Math.Clamp(percentile, 0, 100) / 100.0;
        long cumulative = 0;
        for (int i = 0; i < 256; i++)
        {
            cumulative += histogram[i];
            if (cumulative >= target)
            {
                return i;
            }
        }

        return 255;
    }

    /// <summary>
    /// Box-averages a plane down by an integer factor (partial edge blocks average what is inside).
    /// </summary>
    public static byte[] Downscale(byte[] plane, int width, int height, int factor, out int outWidth, out int outHeight)
    {
        if (factor <= 1)
        {
            outWidth = width;
            outHeight = height;
            return plane;
        }

        int ow = (width + factor - 1) / factor;
        int oh = (height + factor - 1) / factor;
        var result = new byte[ow * oh];
        int localOw = ow;
        ParallelRowIterator.IterateRows(ow, oh, (start, end) =>
        {
            for (int oy = start; oy < end; oy++)
            {
                int y0 = oy * factor;
                int y1 = Math.Min(height, y0 + factor);
                for (int ox = 0; ox < localOw; ox++)
                {
                    int x0 = ox * factor;
                    int x1 = Math.Min(width, x0 + factor);
                    int sum = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int row = y * width;
                        for (int x = x0; x < x1; x++)
                        {
                            sum += plane[row + x];
                        }
                    }

                    int count = (y1 - y0) * (x1 - x0);
                    result[(oy * localOw) + ox] = (byte)((sum + (count / 2)) / count);
                }
            }
        });

        outWidth = ow;
        outHeight = oh;
        return result;
    }

    /// <summary>Picks a downscale factor so that the larger dimension is at most <paramref name="maxDimension"/>.</summary>
    public static int DownscaleFactor(int width, int height, int maxDimension)
        => Math.Max(1, (Math.Max(width, height) + maxDimension - 1) / maxDimension);

    /// <summary>
    /// Rotates a plane clockwise by <paramref name="degrees"/> about its centre with nearest-neighbour sampling
    /// onto a canvas of the same size, filling uncovered pixels with <paramref name="fill"/>.
    /// </summary>
    public static byte[] RotateNearest(byte[] plane, int width, int height, double degrees, byte fill)
    {
        double radians = degrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double cx = width / 2.0;
        double cy = height / 2.0;
        var result = new byte[plane.Length];
        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                double ty = y + 0.5 - cy;
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    double tx = x + 0.5 - cx;
                    int sx = (int)Math.Floor((cos * tx) + (sin * ty) + cx);
                    int sy = (int)Math.Floor((-sin * tx) + (cos * ty) + cy);
                    result[offset + x] = (uint)sx < (uint)width && (uint)sy < (uint)height ? plane[(sy * width) + sx] : fill;
                }
            }
        });
        return result;
    }

    /// <summary>Otsu threshold on a byte plane.</summary>
    public static int OtsuThreshold(byte[] plane) => FilterOps.ComputeOtsuThreshold(Histogram(plane), plane.Length);
}
