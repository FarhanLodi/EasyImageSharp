using System.Numerics;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Extracts the planes the quality metrics work on, in double precision.</summary>
internal static class QualityPlanes
{
    /// <summary>BT.601 luma weights applied to the encoded (non-linear) R'G'B' values.</summary>
    internal const double LumaR = 0.299;
    internal const double LumaG = 0.587;
    internal const double LumaB = 0.114;

    /// <summary>
    /// The root frame as one plane (BT.601 luma) or three planes (R, G, B), each value in 0-1. 8-bit formats are
    /// read exactly (<c>v / 255</c>); wider formats through their normalised components, clamped to 0-1.
    /// </summary>
    public static double[][] Extract<TPixel>(ImageFrame<TPixel> frame, QualityChannels channels)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (channels is not (QualityChannels.Luma or QualityChannels.Rgb))
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Unknown channel selection.");
        }

        int width = frame.Width;
        int height = frame.Height;
        int planeCount = channels == QualityChannels.Luma ? 1 : 3;
        double[][] planes = new double[planeCount][];
        for (int p = 0; p < planeCount; p++)
        {
            planes[p] = new double[width * height];
        }

        ParallelRowIterator.IterateRows(width, height, (start, end) =>
        {
            var row = new double[width * 3];
            for (int y = start; y < end; y++)
            {
                ReadRgb(frame, y, row);
                int offset = y * width;
                if (planeCount == 1)
                {
                    double[] luma = planes[0];
                    for (int x = 0; x < width; x++)
                    {
                        luma[offset + x] = (LumaR * row[x * 3]) + (LumaG * row[(x * 3) + 1]) + (LumaB * row[(x * 3) + 2]);
                    }
                }
                else
                {
                    for (int x = 0; x < width; x++)
                    {
                        planes[0][offset + x] = row[x * 3];
                        planes[1][offset + x] = row[(x * 3) + 1];
                        planes[2][offset + x] = row[(x * 3) + 2];
                    }
                }
            }
        });

        return planes;
    }

    /// <summary>Reads one row as interleaved R, G, B in 0-1.</summary>
    public static void ReadRgb<TPixel>(ImageFrame<TPixel> frame, int y, Span<double> destination)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Span<TPixel> source = frame.GetRowSpan(y);
        if (PixelOps.IsHighPrecision<TPixel>())
        {
            for (int x = 0; x < source.Length; x++)
            {
                Vector4 v = source[x].ToScaledVector4();
                destination[x * 3] = Unit(v.X);
                destination[(x * 3) + 1] = Unit(v.Y);
                destination[(x * 3) + 2] = Unit(v.Z);
            }

            return;
        }

        for (int x = 0; x < source.Length; x++)
        {
            Rgba32 p = source[x].ToRgba32();
            destination[x * 3] = p.R / 255.0;
            destination[(x * 3) + 1] = p.G / 255.0;
            destination[(x * 3) + 2] = p.B / 255.0;
        }
    }

    /// <summary>Reads one row's alpha in 0-1.</summary>
    public static void ReadAlpha<TPixel>(ImageFrame<TPixel> frame, int y, Span<double> destination)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Span<TPixel> source = frame.GetRowSpan(y);
        for (int x = 0; x < source.Length; x++)
        {
            destination[x] = PixelOps.IsHighPrecision<TPixel>() ? Unit(source[x].ToScaledVector4().W) : source[x].ToRgba32().A / 255.0;
        }
    }

    /// <summary>The root frame as a BT.601 luma plane in 8-bit units (0-255), as the no-reference metrics report them.</summary>
    public static double[] Luma255<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        double[] luma = Extract(frame, QualityChannels.Luma)[0];
        for (int i = 0; i < luma.Length; i++)
        {
            luma[i] *= 255;
        }

        return luma;
    }

    /// <summary>Sums per-row partial results in row order, so the total does not depend on how rows were split across threads.</summary>
    public static double SumInOrder(double[] rowSums)
    {
        double total = 0;
        foreach (double value in rowSums)
        {
            total += value;
        }

        return total;
    }

    private static double Unit(float value) => float.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
}
