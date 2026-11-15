using System.Numerics;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Frame-level application of a <see cref="ColorLut"/>.</summary>
internal static class LutOps
{
    public static void Apply<TPixel>(ImageFrame<TPixel> frame, ColorLut lut, bool tetrahedral, float amount)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (PixelOps.IsHighPrecision<TPixel>())
        {
            // Wide formats: normalised components in, normalised components out, no 8-bit step anywhere.
            int width = frame.Width;
            ParallelRowIterator.IterateRows(width, frame.Height, (start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    Span<TPixel> row = frame.GetRowSpan(y);
                    for (int x = 0; x < width; x++)
                    {
                        Vector4 v = row[x].ToScaledVector4();
                        Vector3 rgb = new(v.X, v.Y, v.Z);
                        Vector3 mapped = lut.Map(rgb, tetrahedral);
                        if (amount < 1f)
                        {
                            mapped = Vector3.Lerp(rgb, mapped, amount);
                        }

                        row[x] = TPixel.FromScaledVector4(new Vector4(mapped, v.W));
                    }
                }
            });
            return;
        }

        RowProcessor.ProcessRows(frame, RowProcessor.Bounds(frame), (row, _) =>
        {
            for (int x = 0; x < row.Length; x++)
            {
                Rgba32 p = row[x];
                Vector3 rgb = new Vector3(p.R, p.G, p.B) * (1f / 255f);
                Vector3 mapped = lut.Map(rgb, tetrahedral);
                if (amount < 1f)
                {
                    mapped = Vector3.Lerp(rgb, mapped, amount);
                }

                mapped = Vector3.Clamp(mapped, Vector3.Zero, Vector3.One) * 255f;
                row[x] = new Rgba32((byte)(mapped.X + 0.5f), (byte)(mapped.Y + 0.5f), (byte)(mapped.Z + 0.5f), p.A);
            }
        });
    }
}
