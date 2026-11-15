using System.Numerics;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Moves a frame's colour channels into interleaved single-precision RGB (0-1) and back, keeping alpha.</summary>
internal static class DenoisePlanes
{
    public static float[] ReadRgb<TPixel>(ImageFrame<TPixel> frame)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        float[] rgb = new float[width * frame.Height * 3];
        bool wide = PixelOps.IsHighPrecision<TPixel>();
        ParallelRowIterator.IterateRows(width, frame.Height, (start, end) =>
        {
            var row = new Rgba32[width];
            for (int y = start; y < end; y++)
            {
                Span<TPixel> source = frame.GetRowSpan(y);
                int o = y * width * 3;
                if (wide)
                {
                    for (int x = 0; x < width; x++)
                    {
                        Vector4 v = source[x].ToScaledVector4();
                        rgb[o + (x * 3)] = v.X;
                        rgb[o + (x * 3) + 1] = v.Y;
                        rgb[o + (x * 3) + 2] = v.Z;
                    }

                    continue;
                }

                PixelOps.ToRgba32<TPixel>(source, row);
                for (int x = 0; x < width; x++)
                {
                    rgb[o + (x * 3)] = row[x].R / 255f;
                    rgb[o + (x * 3) + 1] = row[x].G / 255f;
                    rgb[o + (x * 3) + 2] = row[x].B / 255f;
                }
            }
        });

        return rgb;
    }

    /// <summary>Writes filtered colour back; 8-bit formats are rounded and clamped, every format keeps its alpha.</summary>
    public static void WriteRgb<TPixel>(ImageFrame<TPixel> frame, float[] rgb)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int width = frame.Width;
        bool wide = PixelOps.IsHighPrecision<TPixel>();
        ParallelRowIterator.IterateRows(width, frame.Height, (start, end) =>
        {
            var row = new Rgba32[width];
            for (int y = start; y < end; y++)
            {
                Span<TPixel> target = frame.GetRowSpan(y);
                int o = y * width * 3;
                if (wide)
                {
                    for (int x = 0; x < width; x++)
                    {
                        float alpha = target[x].ToScaledVector4().W;
                        target[x] = TPixel.FromScaledVector4(new Vector4(rgb[o + (x * 3)], rgb[o + (x * 3) + 1], rgb[o + (x * 3) + 2], alpha));
                    }

                    continue;
                }

                PixelOps.ToRgba32<TPixel>(target, row);
                for (int x = 0; x < width; x++)
                {
                    row[x] = new Rgba32(
                        RowProcessor.ClampToByte(rgb[o + (x * 3)] * 255f),
                        RowProcessor.ClampToByte(rgb[o + (x * 3) + 1] * 255f),
                        RowProcessor.ClampToByte(rgb[o + (x * 3) + 2] * 255f),
                        row[x].A);
                }

                PixelOps.FromRgba32<TPixel>(row, target);
            }
        });
    }
}
