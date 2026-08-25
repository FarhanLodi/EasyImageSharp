using System.Numerics;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Applies <see cref="ColorMatrix"/> filters to frames.</summary>
internal static class ColorMatrixOps
{
    /// <summary>
    /// Transforms every pixel of <paramref name="region"/> (already clamped to the frame): straight RGBA is
    /// scaled to 0-1, multiplied by the matrix, clamped to 0-1 and rounded back to bytes.
    /// </summary>
    public static void Apply<TPixel>(ImageFrame<TPixel> frame, Rectangle region, ColorMatrix matrix)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (matrix.IsIdentity)
        {
            return;
        }

        RowProcessor.ProcessRows(frame, region, (row, _) =>
        {
            for (int i = 0; i < row.Length; i++)
            {
                Vector4 v = RowProcessor.ToUnitVector(row[i]);
                row[i] = RowProcessor.FromUnitVector(matrix.Transform(v));
            }
        });
    }
}
