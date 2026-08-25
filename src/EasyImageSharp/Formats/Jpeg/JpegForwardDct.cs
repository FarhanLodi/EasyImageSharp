using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Forward 8x8 DCT for the encoder: the Arai/Agui/Nakajima factorisation in single precision (5 multiplications
/// per 1-D transform; the remaining scaling is folded into the quantisation divisors, as libjpeg's float DCT does),
/// followed by quantisation by multiplication with precomputed reciprocals straight into zigzag order.
/// </summary>
internal static class JpegForwardDct
{
    /// <summary>AA&amp;N output scale factors: 1 for k = 0, cos(k*PI/16) * sqrt(2) for k = 1..7.</summary>
    private static readonly double[] AanScaleFactor =
    {
        1.0, 1.387039845, 1.306562965, 1.175875602, 1.0, 0.785694958, 0.541196100, 0.275899379,
    };

    /// <summary>
    /// Builds the fused reciprocal quantisation table for an 8-bit quantisation table given in natural order:
    /// <c>1 / (quant[i] * scale[row] * scale[col] * 8)</c>, so that <c>round(dct[i] * table[i])</c> is the quantised
    /// coefficient of the true (orthonormal-scaled) DCT.
    /// </summary>
    public static float[] CreateReciprocalTable(ReadOnlySpan<ushort> quantNatural)
    {
        var table = new float[64];
        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                int i = (row * 8) + col;
                table[i] = (float)(1.0 / (quantNatural[i] * AanScaleFactor[row] * AanScaleFactor[col] * 8.0));
            }
        }

        return table;
    }

    /// <summary>
    /// Transforms one block of level-shifted samples (natural order, modified in place) and writes the quantised
    /// coefficients in zigzag order.
    /// </summary>
    /// <param name="block">64 level-shifted samples in row-major order; overwritten with scaled DCT output.</param>
    /// <param name="reciprocals">Table from <see cref="CreateReciprocalTable"/> in natural order.</param>
    /// <param name="zigzagOut">Receives the 64 quantised coefficients in zigzag scan order.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TransformAndQuantize(Span<float> block, ReadOnlySpan<float> reciprocals, Span<short> zigzagOut)
    {
        Transform(block);
        Quantize(block, reciprocals, zigzagOut);
    }

    /// <summary>In-place AA&amp;N forward DCT; the output is scaled by 8 * scale[row] * scale[col] relative to the true DCT.</summary>
    public static void Transform(Span<float> block)
    {
        if (block.Length < 64)
        {
            throw new ArgumentException("Block must hold 64 samples.", nameof(block));
        }

        ref float d = ref MemoryMarshal.GetReference(block);

        // Pass 1: rows.
        for (int r = 0; r < 64; r += 8)
        {
            ref float row = ref Unsafe.Add(ref d, r);
            float d0 = row;
            float d1 = Unsafe.Add(ref row, 1);
            float d2 = Unsafe.Add(ref row, 2);
            float d3 = Unsafe.Add(ref row, 3);
            float d4 = Unsafe.Add(ref row, 4);
            float d5 = Unsafe.Add(ref row, 5);
            float d6 = Unsafe.Add(ref row, 6);
            float d7 = Unsafe.Add(ref row, 7);

            float tmp0 = d0 + d7;
            float tmp7 = d0 - d7;
            float tmp1 = d1 + d6;
            float tmp6 = d1 - d6;
            float tmp2 = d2 + d5;
            float tmp5 = d2 - d5;
            float tmp3 = d3 + d4;
            float tmp4 = d3 - d4;

            // Even part.
            float tmp10 = tmp0 + tmp3;
            float tmp13 = tmp0 - tmp3;
            float tmp11 = tmp1 + tmp2;
            float tmp12 = tmp1 - tmp2;

            row = tmp10 + tmp11;
            Unsafe.Add(ref row, 4) = tmp10 - tmp11;

            float z1 = (tmp12 + tmp13) * 0.707106781f; // c4
            Unsafe.Add(ref row, 2) = tmp13 + z1;
            Unsafe.Add(ref row, 6) = tmp13 - z1;

            // Odd part.
            tmp10 = tmp4 + tmp5;
            tmp11 = tmp5 + tmp6;
            tmp12 = tmp6 + tmp7;

            float z5 = (tmp10 - tmp12) * 0.382683433f; // c6
            float z2 = (0.541196100f * tmp10) + z5;    // c2 - c6
            float z4 = (1.306562965f * tmp12) + z5;    // c2 + c6
            float z3 = tmp11 * 0.707106781f;           // c4

            float z11 = tmp7 + z3;
            float z13 = tmp7 - z3;

            Unsafe.Add(ref row, 5) = z13 + z2;
            Unsafe.Add(ref row, 3) = z13 - z2;
            Unsafe.Add(ref row, 1) = z11 + z4;
            Unsafe.Add(ref row, 7) = z11 - z4;
        }

        // Pass 2: columns.
        for (int c = 0; c < 8; c++)
        {
            ref float col = ref Unsafe.Add(ref d, c);
            float d0 = col;
            float d1 = Unsafe.Add(ref col, 8);
            float d2 = Unsafe.Add(ref col, 16);
            float d3 = Unsafe.Add(ref col, 24);
            float d4 = Unsafe.Add(ref col, 32);
            float d5 = Unsafe.Add(ref col, 40);
            float d6 = Unsafe.Add(ref col, 48);
            float d7 = Unsafe.Add(ref col, 56);

            float tmp0 = d0 + d7;
            float tmp7 = d0 - d7;
            float tmp1 = d1 + d6;
            float tmp6 = d1 - d6;
            float tmp2 = d2 + d5;
            float tmp5 = d2 - d5;
            float tmp3 = d3 + d4;
            float tmp4 = d3 - d4;

            float tmp10 = tmp0 + tmp3;
            float tmp13 = tmp0 - tmp3;
            float tmp11 = tmp1 + tmp2;
            float tmp12 = tmp1 - tmp2;

            col = tmp10 + tmp11;
            Unsafe.Add(ref col, 32) = tmp10 - tmp11;

            float z1 = (tmp12 + tmp13) * 0.707106781f;
            Unsafe.Add(ref col, 16) = tmp13 + z1;
            Unsafe.Add(ref col, 48) = tmp13 - z1;

            tmp10 = tmp4 + tmp5;
            tmp11 = tmp5 + tmp6;
            tmp12 = tmp6 + tmp7;

            float z5 = (tmp10 - tmp12) * 0.382683433f;
            float z2 = (0.541196100f * tmp10) + z5;
            float z4 = (1.306562965f * tmp12) + z5;
            float z3 = tmp11 * 0.707106781f;

            float z11 = tmp7 + z3;
            float z13 = tmp7 - z3;

            Unsafe.Add(ref col, 40) = z13 + z2;
            Unsafe.Add(ref col, 24) = z13 - z2;
            Unsafe.Add(ref col, 8) = z11 + z4;
            Unsafe.Add(ref col, 56) = z11 - z4;
        }
    }

    /// <summary>
    /// Multiplies each scaled coefficient by its reciprocal divisor, rounds half up and stores the result in
    /// zigzag order. Adding 16384.5 before truncating rounds without a call or a branch; the offset is large
    /// enough that the addition itself is exact for every coefficient a valid 8-bit block can produce.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Quantize(ReadOnlySpan<float> block, ReadOnlySpan<float> reciprocals, Span<short> zigzagOut)
    {
        if (block.Length < 64 || reciprocals.Length < 64 || zigzagOut.Length < 64)
        {
            throw new ArgumentException("Block, reciprocal table and output must hold 64 entries.");
        }

        ref float b = ref MemoryMarshal.GetReference(block);
        ref float q = ref MemoryMarshal.GetReference(reciprocals);
        ref short o = ref MemoryMarshal.GetReference(zigzagOut);
        ref int zz = ref MemoryMarshal.GetArrayDataReference(JpegTables.ZigZag);
        for (int k = 0; k < 64; k++)
        {
            int natural = Unsafe.Add(ref zz, k);
            float scaled = Unsafe.Add(ref b, natural) * Unsafe.Add(ref q, natural);
            Unsafe.Add(ref o, k) = (short)((int)(scaled + 16384.5f) - 16384);
        }
    }
}
