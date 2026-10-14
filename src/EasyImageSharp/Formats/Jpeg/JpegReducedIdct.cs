using System.Numerics;
using System.Runtime.InteropServices;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>Direct reduced reconstruction from an 8x8 coefficient block, without an 8x8 sample buffer.</summary>
internal static class JpegReducedIdct
{
    private static readonly double[] Basis2 = BuildBasis(2);
    private static readonly double[] Basis4 = BuildBasis(4);
    private static readonly Vector4[] Columns4 = Enumerable.Range(0, 8)
        .Select(u => new Vector4((float)Basis4[u], (float)Basis4[8 + u], (float)Basis4[16 + u], (float)Basis4[24 + u])).ToArray();
    private static readonly Vector2[] Columns2 = Enumerable.Range(0, 8)
        .Select(u => new Vector2((float)Basis2[u], (float)Basis2[8 + u])).ToArray();

    internal static void Transform(Span<float> coefficients, Span<float> scratch, int size)
    {
        if (size == 1)
        {
            coefficients[0] *= 0.125f;
            return;
        }

        if (size == 4)
        {
            // Four output positions are independent lanes; the second pass operates on
            // entire horizontal rows. No full-resolution sample block is reconstructed.
            Span<Vector4> rows = MemoryMarshal.Cast<float, Vector4>(scratch[..32]);
            for (int v = 0; v < 8; v++)
            {
                Vector4 sum = Vector4.Zero;
                for (int u = 0; u < 8; u++)
                {
                    sum += coefficients[v * 8 + u] * Columns4[u];
                }

                rows[v] = sum;
            }

            Span<Vector4> output = MemoryMarshal.Cast<float, Vector4>(coefficients[..16]);
            for (int y = 0; y < 4; y++)
            {
                Vector4 sum = Vector4.Zero;
                for (int v = 0; v < 8; v++)
                {
                    sum += rows[v] * (float)Basis4[y * 8 + v];
                }

                output[y] = sum;
            }

            return;
        }

        Span<Vector2> smallRows = MemoryMarshal.Cast<float, Vector2>(scratch[..16]);
        for (int v = 0; v < 8; v++)
        {
            Vector2 sum = Vector2.Zero;
            for (int u = 0; u < 8; u++)
            {
                sum += coefficients[v * 8 + u] * Columns2[u];
            }

            smallRows[v] = sum;
        }

        Span<Vector2> smallOutput = MemoryMarshal.Cast<float, Vector2>(coefficients[..4]);
        for (int y = 0; y < 2; y++)
        {
            Vector2 sum = Vector2.Zero;
            for (int v = 0; v < 8; v++)
            {
                sum += smallRows[v] * (float)Basis2[y * 8 + v];
            }

            smallOutput[y] = sum;
        }
    }

    private static double[] BuildBasis(int size)
    {
        // Average the continuous IDCT's original sample positions before rounding/clamping.
        // Keeping all frequencies avoids the different response of frequency truncation.
        var basis = new double[size * 8];
        int scale = 8 / size;
        for (int x = 0; x < size; x++)
        {
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                for (int k = 0; k < scale; k++)
                {
                    sum += Math.Cos(((2 * (x * scale + k) + 1) * u * Math.PI) / 16);
                }

                basis[x * 8 + u] = (u == 0 ? 1 / Math.Sqrt(2) : 1) * sum / (2 * scale);
            }
        }

        return basis;
    }
}
