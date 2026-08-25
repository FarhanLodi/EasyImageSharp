namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// The pixel-level primitives of the VP8 encoder: the forward 4x4 DCT and Walsh-Hadamard transforms that
/// pair with the inverse transforms in <see cref="Vp8Dsp"/>, and the sum-of-squared-error metrics the mode
/// decision scores with. The forward transforms follow the integer approximation given in RFC 6386's
/// reference encoder, which round-trips through the normative inverse transform to within a fraction of a
/// quantizer step.
/// </summary>
internal static class Vp8EncoderDsp
{
    private const int Bps = Vp8Dsp.Bps;

    /// <summary>
    /// Forward 4x4 DCT of the difference between <paramref name="src"/> and <paramref name="pred"/>,
    /// producing sixteen coefficients in raster order.
    /// </summary>
    public static void FTransform(byte[] src, int srcOff, byte[] pred, int predOff, short[] output, int outOff)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++, srcOff += Bps, predOff += Bps)
        {
            int d0 = src[srcOff + 0] - pred[predOff + 0];
            int d1 = src[srcOff + 1] - pred[predOff + 1];
            int d2 = src[srcOff + 2] - pred[predOff + 2];
            int d3 = src[srcOff + 3] - pred[predOff + 3];
            int a0 = d0 + d3;
            int a1 = d1 + d2;
            int a2 = d1 - d2;
            int a3 = d0 - d3;
            tmp[0 + (i * 4)] = (a0 + a1) * 8;
            tmp[1 + (i * 4)] = ((a2 * 2217) + (a3 * 5352) + 1812) >> 9;
            tmp[2 + (i * 4)] = (a0 - a1) * 8;
            tmp[3 + (i * 4)] = ((a3 * 2217) - (a2 * 5352) + 937) >> 9;
        }

        for (int i = 0; i < 4; i++)
        {
            int a0 = tmp[0 + i] + tmp[12 + i];
            int a1 = tmp[4 + i] + tmp[8 + i];
            int a2 = tmp[4 + i] - tmp[8 + i];
            int a3 = tmp[0 + i] - tmp[12 + i];
            output[outOff + 0 + i] = (short)((a0 + a1 + 7) >> 4);
            output[outOff + 4 + i] = (short)((((a2 * 2217) + (a3 * 5352) + 12000) >> 16) + (a3 != 0 ? 1 : 0));
            output[outOff + 8 + i] = (short)((a0 - a1 + 7) >> 4);
            output[outOff + 12 + i] = (short)(((a3 * 2217) - (a2 * 5352) + 51000) >> 16);
        }
    }

    /// <summary>
    /// Forward Walsh-Hadamard transform of the sixteen luma DC coefficients, which sit at
    /// <c>input[16 * n]</c> for sub-block <c>n</c>. It is the inverse of <see cref="Vp8Dsp.TransformWht"/>.
    /// </summary>
    public static void FTransformWht(short[] input, short[] output)
    {
        Span<int> tmp = stackalloc int[16];
        int inOff = 0;
        for (int i = 0; i < 4; i++, inOff += 64)
        {
            int a0 = input[inOff + (0 * 16)] + input[inOff + (2 * 16)];
            int a1 = input[inOff + (1 * 16)] + input[inOff + (3 * 16)];
            int a2 = input[inOff + (1 * 16)] - input[inOff + (3 * 16)];
            int a3 = input[inOff + (0 * 16)] - input[inOff + (2 * 16)];
            tmp[0 + (i * 4)] = a0 + a1;
            tmp[1 + (i * 4)] = a3 + a2;
            tmp[2 + (i * 4)] = a3 - a2;
            tmp[3 + (i * 4)] = a0 - a1;
        }

        for (int i = 0; i < 4; i++)
        {
            int a0 = tmp[0 + i] + tmp[8 + i];
            int a1 = tmp[4 + i] + tmp[12 + i];
            int a2 = tmp[4 + i] - tmp[12 + i];
            int a3 = tmp[0 + i] - tmp[8 + i];
            output[0 + i] = (short)((a0 + a1) >> 1);
            output[4 + i] = (short)((a3 + a2) >> 1);
            output[8 + i] = (short)((a3 - a2) >> 1);
            output[12 + i] = (short)((a0 - a1) >> 1);
        }
    }

    /// <summary>Sum of squared differences over a <paramref name="size"/> square block of stride 32.</summary>
    public static int Sse(byte[] a, int aOff, byte[] b, int bOff, int size)
    {
        int sum = 0;
        for (int y = 0; y < size; y++)
        {
            int ra = aOff + (y * Bps);
            int rb = bOff + (y * Bps);
            for (int x = 0; x < size; x++)
            {
                int d = a[ra + x] - b[rb + x];
                sum += d * d;
            }
        }

        return sum;
    }
}
