using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Forward 8x8 DCT for the encoder: the Arai/Agui/Nakajima factorisation in single precision (5 multiplications
/// per 1-D transform; the remaining scaling is folded into the quantisation divisors, as libjpeg's float DCT does),
/// followed by quantisation by multiplication with precomputed reciprocals straight into zigzag order.
/// </summary>
/// <remarks>
/// <para>
/// The transform has three implementations that produce bit-identical output: a scalar reference, a 256-bit
/// path (x86 AVX) that holds the whole block in eight registers, and a 128-bit path (SSE or Arm64 AdvSIMD) that
/// holds each row as two halves. The vector paths run the very same butterfly, operation for operation and in
/// the same order, on eight rows or columns at once; a register transpose before each pass lets the row pass
/// work across lanes exactly as the scalar row loop works along a row. IEEE single-precision addition and
/// multiplication are correctly rounded whatever the vector width, and .NET never contracts a multiply and an
/// add into a fused multiply-add on its own, so every lane computes exactly the float the scalar code does.
/// </para>
/// <para>
/// <see cref="Path.Auto"/> selects the vector paths through <see cref="SimdConfig"/>; with
/// <see cref="SimdConfig.ForceScalarFallback"/> set (or without the intrinsics) the scalar reference runs. The
/// widening of samples into the float block and the quantisation step have vector paths of their own, exact by
/// construction (integer-to-float conversion of small integers, multiplication by a power of two, and the same
/// multiply, add and truncation per lane as the scalar loop).
/// </para>
/// </remarks>
internal static class JpegForwardDct
{
    /// <summary>AA&amp;N output scale factors: 1 for k = 0, cos(k*PI/16) * sqrt(2) for k = 1..7.</summary>
    private static readonly double[] AanScaleFactor =
    {
        1.0, 1.387039845, 1.306562965, 1.175875602, 1.0, 0.785694958, 0.541196100, 0.275899379,
    };

    /// <summary>The transform implementation to run; <see cref="Auto"/> picks the widest one available.</summary>
    internal enum Path
    {
        /// <summary>The widest path the hardware and <see cref="SimdConfig"/> allow.</summary>
        Auto,

        /// <summary>The scalar reference.</summary>
        Scalar,

        /// <summary>Two 128-bit halves per row (SSE or Arm64 AdvSIMD).</summary>
        Vector128,

        /// <summary>One 256-bit register per row (x86 AVX).</summary>
        Vector256,
    }

    /// <summary>True when the hardware has the intrinsics of the 256-bit transform (x86 AVX).</summary>
    internal static bool IsVector256Supported => Avx.IsSupported;

    /// <summary>True when the hardware has the intrinsics of the 128-bit transform (SSE or Arm64 AdvSIMD).</summary>
    internal static bool IsVector128Supported => Sse.IsSupported || AdvSimd.Arm64.IsSupported;

    /// <summary>
    /// The path <see cref="Path.Auto"/> resolves to: the widest one the hardware supports and
    /// <see cref="SimdConfig"/> allows, the scalar reference when <see cref="SimdConfig.ForceScalarFallback"/> is set.
    /// </summary>
    internal static Path AutoPath
        => SimdConfig.Vector256Enabled && IsVector256Supported ? Path.Vector256
            : SimdConfig.Vector128Enabled && IsVector128Supported ? Path.Vector128
            : Path.Scalar;

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

    /// <summary>
    /// Widens an 8x8 block of fixed-point samples into <paramref name="block"/>, multiplying each by
    /// <paramref name="scale"/> (the reciprocal of the colour converter's sample scale, a power of two, so the
    /// conversion is exact).
    /// </summary>
    /// <param name="samples">The first sample of the block's top row.</param>
    /// <param name="stride">Distance between rows of <paramref name="samples"/>, in samples.</param>
    /// <param name="block">Receives the 64 widened samples in row-major order.</param>
    /// <param name="scale">Factor applied to every sample.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LoadBlock(ref short samples, int stride, Span<float> block, float scale)
        => LoadBlock(ref samples, stride, block, scale, Path.Auto);

    /// <summary>Widens a block through the given implementation; every path produces the same floats.</summary>
    internal static void LoadBlock(ref short samples, int stride, Span<float> block, float scale, Path path)
    {
        if (block.Length < 64)
        {
            throw new ArgumentException("Block must hold 64 samples.", nameof(block));
        }

        if (path == Path.Auto)
        {
            path = SimdConfig.Vector256Enabled ? Path.Vector256 : SimdConfig.Vector128Enabled ? Path.Vector128 : Path.Scalar;
        }

        ref float dst = ref MemoryMarshal.GetReference(block);
        if (path == Path.Vector256)
        {
            Vector256<float> factor = Vector256.Create(scale);
            for (int y = 0; y < 8; y++)
            {
                (Vector128<int> low, Vector128<int> high) = Vector128.Widen(Vector128.LoadUnsafe(ref samples, (nuint)(y * stride)));
                (Vector256.ConvertToSingle(Vector256.Create(low, high)) * factor).StoreUnsafe(ref dst, (nuint)(y * 8));
            }

            return;
        }

        if (path == Path.Vector128)
        {
            Vector128<float> factor = Vector128.Create(scale);
            for (int y = 0; y < 8; y++)
            {
                (Vector128<int> low, Vector128<int> high) = Vector128.Widen(Vector128.LoadUnsafe(ref samples, (nuint)(y * stride)));
                (Vector128.ConvertToSingle(low) * factor).StoreUnsafe(ref dst, (nuint)(y * 8));
                (Vector128.ConvertToSingle(high) * factor).StoreUnsafe(ref dst, (nuint)((y * 8) + 4));
            }

            return;
        }

        for (int y = 0; y < 8; y++)
        {
            ref short s = ref Unsafe.Add(ref samples, y * stride);
            ref float d = ref Unsafe.Add(ref dst, y * 8);
            for (int x = 0; x < 8; x++)
            {
                Unsafe.Add(ref d, x) = Unsafe.Add(ref s, x) * scale;
            }
        }
    }

    /// <summary>In-place AA&amp;N forward DCT; the output is scaled by 8 * scale[row] * scale[col] relative to the true DCT.</summary>
    public static void Transform(Span<float> block) => Transform(block, Path.Auto);

    /// <summary>In-place AA&amp;N forward DCT through the given implementation (tests compare them against each other).</summary>
    /// <exception cref="NotSupportedException">The requested vector path is not available on this machine.</exception>
    internal static void Transform(Span<float> block, Path path)
    {
        if (block.Length < 64)
        {
            throw new ArgumentException("Block must hold 64 samples.", nameof(block));
        }

        ref float d = ref MemoryMarshal.GetReference(block);
        if (path == Path.Auto)
        {
            path = AutoPath;
        }

        switch (path)
        {
            case Path.Vector256 when IsVector256Supported:
                TransformVector256(ref d);
                break;
            case Path.Vector128 when IsVector128Supported:
                TransformVector128(ref d);
                break;
            case Path.Scalar:
                TransformScalar(ref d);
                break;
            default:
                throw new NotSupportedException($"The {path} forward DCT is not available on this machine.");
        }
    }

    /// <summary>
    /// Multiplies each scaled coefficient by its reciprocal divisor, rounds half up and stores the result in
    /// zigzag order. Adding 16384.5 before truncating rounds without a call or a branch; the offset is large
    /// enough that the addition itself is exact for every coefficient a valid 8-bit block can produce.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Quantize(ReadOnlySpan<float> block, ReadOnlySpan<float> reciprocals, Span<short> zigzagOut)
        => Quantize(block, reciprocals, zigzagOut, Path.Auto);

    /// <summary>Quantises through the given implementation; every path produces the same coefficients.</summary>
    internal static void Quantize(ReadOnlySpan<float> block, ReadOnlySpan<float> reciprocals, Span<short> zigzagOut, Path path)
    {
        if (block.Length < 64 || reciprocals.Length < 64 || zigzagOut.Length < 64)
        {
            throw new ArgumentException("Block, reciprocal table and output must hold 64 entries.");
        }

        ref float b = ref MemoryMarshal.GetReference(block);
        ref float q = ref MemoryMarshal.GetReference(reciprocals);
        ref short o = ref MemoryMarshal.GetReference(zigzagOut);
        ref int zz = ref MemoryMarshal.GetArrayDataReference(JpegTables.ZigZag);
        if (path == Path.Auto)
        {
            path = SimdConfig.Vector256Enabled ? Path.Vector256 : SimdConfig.Vector128Enabled ? Path.Vector128 : Path.Scalar;
        }

        // An explicitly requested vector path always runs: the portable vector API is exact even where it is not
        // hardware accelerated, so tests can compare every path on any machine.
        if (path == Path.Scalar)
        {
            for (int k = 0; k < 64; k++)
            {
                int natural = Unsafe.Add(ref zz, k);
                float scaled = Unsafe.Add(ref b, natural) * Unsafe.Add(ref q, natural);
                Unsafe.Add(ref o, k) = (short)((int)(scaled + 16384.5f) - 16384);
            }

            return;
        }

        // Vector paths: the same multiply, offset and truncation eight (or four) lanes at a time into a natural
        // order scratch block, then the zigzag permutation as plain loads and stores.
        Span<short> natural64 = stackalloc short[64];
        ref short n = ref MemoryMarshal.GetReference(natural64);
        if (path == Path.Vector256)
        {
            Vector256<float> offset = Vector256.Create(16384.5f);
            Vector256<int> bias = Vector256.Create(16384);
            for (int i = 0; i < 64; i += 8)
            {
                Vector256<float> scaled = Vector256.LoadUnsafe(ref b, (nuint)i) * Vector256.LoadUnsafe(ref q, (nuint)i);
                Vector256<int> rounded = Vector256.ConvertToInt32(scaled + offset) - bias;
                Vector128.Narrow(rounded.GetLower(), rounded.GetUpper()).StoreUnsafe(ref n, (nuint)i);
            }
        }
        else
        {
            Vector128<float> offset = Vector128.Create(16384.5f);
            Vector128<int> bias = Vector128.Create(16384);
            for (int i = 0; i < 64; i += 8)
            {
                Vector128<int> low = Vector128.ConvertToInt32((Vector128.LoadUnsafe(ref b, (nuint)i) * Vector128.LoadUnsafe(ref q, (nuint)i)) + offset) - bias;
                Vector128<int> high = Vector128.ConvertToInt32(
                    (Vector128.LoadUnsafe(ref b, (nuint)(i + 4)) * Vector128.LoadUnsafe(ref q, (nuint)(i + 4))) + offset) - bias;
                Vector128.Narrow(low, high).StoreUnsafe(ref n, (nuint)i);
            }
        }

        for (int k = 0; k < 64; k++)
        {
            Unsafe.Add(ref o, k) = Unsafe.Add(ref n, Unsafe.Add(ref zz, k));
        }
    }

    // =====================================================================================================
    // Scalar reference
    // =====================================================================================================

    private static void TransformScalar(ref float d)
    {
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

    // =====================================================================================================
    // 256-bit path (x86 AVX): one register per row
    // =====================================================================================================

    private static void TransformVector256(ref float d)
    {
        Vector256<float> r0 = Vector256.LoadUnsafe(ref d, 0);
        Vector256<float> r1 = Vector256.LoadUnsafe(ref d, 8);
        Vector256<float> r2 = Vector256.LoadUnsafe(ref d, 16);
        Vector256<float> r3 = Vector256.LoadUnsafe(ref d, 24);
        Vector256<float> r4 = Vector256.LoadUnsafe(ref d, 32);
        Vector256<float> r5 = Vector256.LoadUnsafe(ref d, 40);
        Vector256<float> r6 = Vector256.LoadUnsafe(ref d, 48);
        Vector256<float> r7 = Vector256.LoadUnsafe(ref d, 56);

        // Row pass: after the transpose register k holds column k, so lane r of the butterfly runs along row r.
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Butterfly(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);

        // Column pass: transpose back so register k holds row k again, and lane c runs down column c.
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Butterfly(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);

        r0.StoreUnsafe(ref d, 0);
        r1.StoreUnsafe(ref d, 8);
        r2.StoreUnsafe(ref d, 16);
        r3.StoreUnsafe(ref d, 24);
        r4.StoreUnsafe(ref d, 32);
        r5.StoreUnsafe(ref d, 40);
        r6.StoreUnsafe(ref d, 48);
        r7.StoreUnsafe(ref d, 56);
    }

    /// <summary>The scalar butterfly, lane-wise: inputs d0..d7 in v0..v7, outputs 0..7 written back in place.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Butterfly(
        ref Vector256<float> v0, ref Vector256<float> v1, ref Vector256<float> v2, ref Vector256<float> v3,
        ref Vector256<float> v4, ref Vector256<float> v5, ref Vector256<float> v6, ref Vector256<float> v7)
    {
        Vector256<float> tmp0 = v0 + v7;
        Vector256<float> tmp7 = v0 - v7;
        Vector256<float> tmp1 = v1 + v6;
        Vector256<float> tmp6 = v1 - v6;
        Vector256<float> tmp2 = v2 + v5;
        Vector256<float> tmp5 = v2 - v5;
        Vector256<float> tmp3 = v3 + v4;
        Vector256<float> tmp4 = v3 - v4;

        Vector256<float> tmp10 = tmp0 + tmp3;
        Vector256<float> tmp13 = tmp0 - tmp3;
        Vector256<float> tmp11 = tmp1 + tmp2;
        Vector256<float> tmp12 = tmp1 - tmp2;

        v0 = tmp10 + tmp11;
        v4 = tmp10 - tmp11;

        Vector256<float> z1 = (tmp12 + tmp13) * Vector256.Create(0.707106781f);
        v2 = tmp13 + z1;
        v6 = tmp13 - z1;

        tmp10 = tmp4 + tmp5;
        tmp11 = tmp5 + tmp6;
        tmp12 = tmp6 + tmp7;

        Vector256<float> z5 = (tmp10 - tmp12) * Vector256.Create(0.382683433f);
        Vector256<float> z2 = (Vector256.Create(0.541196100f) * tmp10) + z5;
        Vector256<float> z4 = (Vector256.Create(1.306562965f) * tmp12) + z5;
        Vector256<float> z3 = tmp11 * Vector256.Create(0.707106781f);

        Vector256<float> z11 = tmp7 + z3;
        Vector256<float> z13 = tmp7 - z3;

        v5 = z13 + z2;
        v3 = z13 - z2;
        v1 = z11 + z4;
        v7 = z11 - z4;
    }

    /// <summary>Transposes the 8x8 matrix held one row per register (unpack, shuffle, then swap 128-bit halves).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(
        ref Vector256<float> r0, ref Vector256<float> r1, ref Vector256<float> r2, ref Vector256<float> r3,
        ref Vector256<float> r4, ref Vector256<float> r5, ref Vector256<float> r6, ref Vector256<float> r7)
    {
        // Within each 128-bit lane: interleave row pairs ...
        Vector256<float> t0 = Avx.UnpackLow(r0, r1);
        Vector256<float> t1 = Avx.UnpackHigh(r0, r1);
        Vector256<float> t2 = Avx.UnpackLow(r2, r3);
        Vector256<float> t3 = Avx.UnpackHigh(r2, r3);
        Vector256<float> t4 = Avx.UnpackLow(r4, r5);
        Vector256<float> t5 = Avx.UnpackHigh(r4, r5);
        Vector256<float> t6 = Avx.UnpackLow(r6, r7);
        Vector256<float> t7 = Avx.UnpackHigh(r6, r7);

        // ... then gather four rows' worth of one column per lane (columns k and k + 4 side by side) ...
        Vector256<float> s0 = Avx.Shuffle(t0, t2, 0x44);
        Vector256<float> s1 = Avx.Shuffle(t0, t2, 0xEE);
        Vector256<float> s2 = Avx.Shuffle(t1, t3, 0x44);
        Vector256<float> s3 = Avx.Shuffle(t1, t3, 0xEE);
        Vector256<float> s4 = Avx.Shuffle(t4, t6, 0x44);
        Vector256<float> s5 = Avx.Shuffle(t4, t6, 0xEE);
        Vector256<float> s6 = Avx.Shuffle(t5, t7, 0x44);
        Vector256<float> s7 = Avx.Shuffle(t5, t7, 0xEE);

        // ... and finally pair the top four rows with the bottom four across the 128-bit lanes.
        r0 = Avx.Permute2x128(s0, s4, 0x20);
        r1 = Avx.Permute2x128(s1, s5, 0x20);
        r2 = Avx.Permute2x128(s2, s6, 0x20);
        r3 = Avx.Permute2x128(s3, s7, 0x20);
        r4 = Avx.Permute2x128(s0, s4, 0x31);
        r5 = Avx.Permute2x128(s1, s5, 0x31);
        r6 = Avx.Permute2x128(s2, s6, 0x31);
        r7 = Avx.Permute2x128(s3, s7, 0x31);
    }

    // =====================================================================================================
    // 128-bit path (SSE or Arm64 AdvSIMD): each row as a left (columns 0-3) and a right (columns 4-7) half
    // =====================================================================================================

    private static void TransformVector128(ref float d)
    {
        // lN is the left half of row N, hN the right half.
        Vector128<float> l0 = Vector128.LoadUnsafe(ref d, 0);
        Vector128<float> h0 = Vector128.LoadUnsafe(ref d, 4);
        Vector128<float> l1 = Vector128.LoadUnsafe(ref d, 8);
        Vector128<float> h1 = Vector128.LoadUnsafe(ref d, 12);
        Vector128<float> l2 = Vector128.LoadUnsafe(ref d, 16);
        Vector128<float> h2 = Vector128.LoadUnsafe(ref d, 20);
        Vector128<float> l3 = Vector128.LoadUnsafe(ref d, 24);
        Vector128<float> h3 = Vector128.LoadUnsafe(ref d, 28);
        Vector128<float> l4 = Vector128.LoadUnsafe(ref d, 32);
        Vector128<float> h4 = Vector128.LoadUnsafe(ref d, 36);
        Vector128<float> l5 = Vector128.LoadUnsafe(ref d, 40);
        Vector128<float> h5 = Vector128.LoadUnsafe(ref d, 44);
        Vector128<float> l6 = Vector128.LoadUnsafe(ref d, 48);
        Vector128<float> h6 = Vector128.LoadUnsafe(ref d, 52);
        Vector128<float> l7 = Vector128.LoadUnsafe(ref d, 56);
        Vector128<float> h7 = Vector128.LoadUnsafe(ref d, 60);

        Transpose(
            ref l0, ref h0, ref l1, ref h1, ref l2, ref h2, ref l3, ref h3,
            ref l4, ref h4, ref l5, ref h5, ref l6, ref h6, ref l7, ref h7);
        Butterfly(ref l0, ref l1, ref l2, ref l3, ref l4, ref l5, ref l6, ref l7);
        Butterfly(ref h0, ref h1, ref h2, ref h3, ref h4, ref h5, ref h6, ref h7);
        Transpose(
            ref l0, ref h0, ref l1, ref h1, ref l2, ref h2, ref l3, ref h3,
            ref l4, ref h4, ref l5, ref h5, ref l6, ref h6, ref l7, ref h7);
        Butterfly(ref l0, ref l1, ref l2, ref l3, ref l4, ref l5, ref l6, ref l7);
        Butterfly(ref h0, ref h1, ref h2, ref h3, ref h4, ref h5, ref h6, ref h7);

        l0.StoreUnsafe(ref d, 0);
        h0.StoreUnsafe(ref d, 4);
        l1.StoreUnsafe(ref d, 8);
        h1.StoreUnsafe(ref d, 12);
        l2.StoreUnsafe(ref d, 16);
        h2.StoreUnsafe(ref d, 20);
        l3.StoreUnsafe(ref d, 24);
        h3.StoreUnsafe(ref d, 28);
        l4.StoreUnsafe(ref d, 32);
        h4.StoreUnsafe(ref d, 36);
        l5.StoreUnsafe(ref d, 40);
        h5.StoreUnsafe(ref d, 44);
        l6.StoreUnsafe(ref d, 48);
        h6.StoreUnsafe(ref d, 52);
        l7.StoreUnsafe(ref d, 56);
        h7.StoreUnsafe(ref d, 60);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Butterfly(
        ref Vector128<float> v0, ref Vector128<float> v1, ref Vector128<float> v2, ref Vector128<float> v3,
        ref Vector128<float> v4, ref Vector128<float> v5, ref Vector128<float> v6, ref Vector128<float> v7)
    {
        Vector128<float> tmp0 = v0 + v7;
        Vector128<float> tmp7 = v0 - v7;
        Vector128<float> tmp1 = v1 + v6;
        Vector128<float> tmp6 = v1 - v6;
        Vector128<float> tmp2 = v2 + v5;
        Vector128<float> tmp5 = v2 - v5;
        Vector128<float> tmp3 = v3 + v4;
        Vector128<float> tmp4 = v3 - v4;

        Vector128<float> tmp10 = tmp0 + tmp3;
        Vector128<float> tmp13 = tmp0 - tmp3;
        Vector128<float> tmp11 = tmp1 + tmp2;
        Vector128<float> tmp12 = tmp1 - tmp2;

        v0 = tmp10 + tmp11;
        v4 = tmp10 - tmp11;

        Vector128<float> z1 = (tmp12 + tmp13) * Vector128.Create(0.707106781f);
        v2 = tmp13 + z1;
        v6 = tmp13 - z1;

        tmp10 = tmp4 + tmp5;
        tmp11 = tmp5 + tmp6;
        tmp12 = tmp6 + tmp7;

        Vector128<float> z5 = (tmp10 - tmp12) * Vector128.Create(0.382683433f);
        Vector128<float> z2 = (Vector128.Create(0.541196100f) * tmp10) + z5;
        Vector128<float> z4 = (Vector128.Create(1.306562965f) * tmp12) + z5;
        Vector128<float> z3 = tmp11 * Vector128.Create(0.707106781f);

        Vector128<float> z11 = tmp7 + z3;
        Vector128<float> z13 = tmp7 - z3;

        v5 = z13 + z2;
        v3 = z13 - z2;
        v1 = z11 + z4;
        v7 = z11 - z4;
    }

    /// <summary>
    /// Transposes the 8x8 matrix held as half rows: the four 4x4 quadrants are transposed in place and the two
    /// off-diagonal ones swap places.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(
        ref Vector128<float> l0, ref Vector128<float> h0, ref Vector128<float> l1, ref Vector128<float> h1,
        ref Vector128<float> l2, ref Vector128<float> h2, ref Vector128<float> l3, ref Vector128<float> h3,
        ref Vector128<float> l4, ref Vector128<float> h4, ref Vector128<float> l5, ref Vector128<float> h5,
        ref Vector128<float> l6, ref Vector128<float> h6, ref Vector128<float> l7, ref Vector128<float> h7)
    {
        // The diagonal quadrants stay where they are once transposed ...
        Transpose4x4(ref l0, ref l1, ref l2, ref l3);
        Transpose4x4(ref h4, ref h5, ref h6, ref h7);

        // ... the off-diagonal ones (right halves of rows 0-3, left halves of rows 4-7) trade places.
        Transpose4x4(ref h0, ref h1, ref h2, ref h3);
        Transpose4x4(ref l4, ref l5, ref l6, ref l7);
        (h0, l4) = (l4, h0);
        (h1, l5) = (l5, h1);
        (h2, l6) = (l6, h2);
        (h3, l7) = (l7, h3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose4x4(ref Vector128<float> a0, ref Vector128<float> a1, ref Vector128<float> a2, ref Vector128<float> a3)
    {
        Vector128<float> t0 = InterleaveLow32(a0, a1);  // a0[0] a1[0] a0[1] a1[1]
        Vector128<float> t1 = InterleaveHigh32(a0, a1); // a0[2] a1[2] a0[3] a1[3]
        Vector128<float> t2 = InterleaveLow32(a2, a3);
        Vector128<float> t3 = InterleaveHigh32(a2, a3);
        a0 = InterleaveLow64(t0, t2);
        a1 = InterleaveHigh64(t0, t2);
        a2 = InterleaveLow64(t1, t3);
        a3 = InterleaveHigh64(t1, t3);
    }

    /// <summary>a[0] b[0] a[1] b[1].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> InterleaveLow32(Vector128<float> a, Vector128<float> b)
        => Sse.IsSupported ? Sse.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    /// <summary>a[2] b[2] a[3] b[3].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> InterleaveHigh32(Vector128<float> a, Vector128<float> b)
        => Sse.IsSupported ? Sse.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    /// <summary>a[0] a[1] b[0] b[1].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> InterleaveLow64(Vector128<float> a, Vector128<float> b)
        => Sse.IsSupported ? Sse.MoveLowToHigh(a, b) : AdvSimd.Arm64.ZipLow(a.AsDouble(), b.AsDouble()).AsSingle();

    /// <summary>a[2] a[3] b[2] b[3].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> InterleaveHigh64(Vector128<float> a, Vector128<float> b)
        => Sse.IsSupported ? Sse.MoveHighToLow(b, a) : AdvSimd.Arm64.ZipHigh(a.AsDouble(), b.AsDouble()).AsSingle();
}
