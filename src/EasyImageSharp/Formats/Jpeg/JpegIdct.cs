using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// The decoder's full-size 8x8 inverse DCT: the Arai-Agui-Nakajima factorisation used by libjpeg's floating-point
/// <c>jidctflt.c</c>, which needs five multiplications and 29 additions per one-dimensional pass instead of the 64
/// multiply-adds of a matrix product.
/// </summary>
/// <remarks>
/// <para>
/// The factorisation leaves every coefficient scaled by <c>s(u) s(v)</c> (<see cref="AanScale"/>), so the input
/// block must be dequantized with a table built by <see cref="BuildDequantTable"/>, which also folds in the 1/8 the
/// two passes leave on the output. The result is then the plain inverse DCT of ITU-T T.81 A.3.3, not yet level
/// shifted.
/// </para>
/// <para>
/// The vector paths hold the whole block in registers: the column pass runs on all eight columns at once (one
/// vector per row), an in-register transpose turns rows into columns, the same butterfly runs again, and a second
/// transpose restores the row order. They perform exactly the operations of the scalar path, element for element
/// and in the same order, so all paths produce identical results.
/// </para>
/// <para>
/// When the CPU has fused multiply-add (FMA3 on x86, any Arm64 core) three of the five multiplications of a pass
/// are fused with the addition that follows them. Fusing is decided once per process and applies to every path,
/// the scalar one included (through <see cref="MathF.FusedMultiplyAdd"/>), so scalar and vector output stay
/// bit-identical on any one machine. Fused and unfused arithmetic differ only in the last bits of an intermediate
/// float; after rounding to integer samples the two agree to within one sample level, which the test suite pins
/// through <see cref="Transform(Span{float}, IdctPath, bool)"/> without touching any process-wide state.
/// </para>
/// </remarks>
internal static class JpegIdct
{
    /// <summary>2 cos(4 pi / 16) = sqrt(2).</summary>
    private const float C1414 = 1.414213562f;

    /// <summary>2 cos(2 pi / 16).</summary>
    private const float C1847 = 1.847759065f;

    /// <summary>2 (cos(2 pi / 16) - cos(6 pi / 16)).</summary>
    private const float C1082 = 1.082392200f;

    /// <summary>2 (cos(2 pi / 16) + cos(6 pi / 16)).</summary>
    private const float C2613 = 2.613125930f;

    /// <summary>The AAN output scale of frequency k: 1 for k = 0, otherwise sqrt(2) cos(k pi / 16).</summary>
    private static readonly double[] AanScale = CreateAanScale();

    /// <summary>The implementations of the transform, selectable explicitly by tests.</summary>
    internal enum IdctPath
    {
        /// <summary>One element at a time.</summary>
        Scalar,

        /// <summary>Four columns per register, the block as sixteen half rows.</summary>
        Vector128,

        /// <summary>Eight columns per register, the block as eight rows (x86 AVX).</summary>
        Vector256,
    }

    /// <summary>Whether the transform fuses multiply-add pairs: true when the hardware can do so in every path.</summary>
    internal static bool UsesFma { get; } = Fma.IsSupported || AdvSimd.IsSupported;

    /// <summary>
    /// Builds the dequantization multipliers the transform expects: <c>quant[i] * s(row) * s(col) / 8</c> in natural
    /// order. A block dequantized with them and passed through <see cref="Transform(Span{float})"/> yields the inverse DCT.
    /// </summary>
    public static float[] BuildDequantTable(ReadOnlySpan<ushort> quant)
    {
        var table = new float[64];
        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                int i = (row * 8) + col;
                table[i] = (float)(quant[i] * AanScale[row] * AanScale[col] * 0.125);
            }
        }

        return table;
    }

    /// <summary>
    /// Transforms <paramref name="block"/> (64 coefficients in natural order, dequantized with
    /// <see cref="BuildDequantTable"/>) in place into 64 samples in row-major order, not level shifted.
    /// </summary>
    public static void Transform(Span<float> block)
    {
        if (block.Length < 64)
        {
            throw new ArgumentException("An IDCT block holds 64 coefficients.", nameof(block));
        }

        if (UsesFma)
        {
            Transform<Fused>(block);
        }
        else
        {
            Transform<Unfused>(block);
        }
    }

    /// <summary>Whether <paramref name="path"/> can run on this machine, fused or not.</summary>
    internal static bool IsSupported(IdctPath path, bool fused)
    {
        bool fusable = path switch
        {
            IdctPath.Scalar => true, // MathF.FusedMultiplyAdd is exact, if slow, without hardware support.
            IdctPath.Vector128 => Fma.IsSupported || AdvSimd.IsSupported,
            _ => Fma.IsSupported,
        };

        bool available = path switch
        {
            IdctPath.Scalar => true,
            IdctPath.Vector128 => Vector128.IsHardwareAccelerated,
            _ => Vector256.IsHardwareAccelerated && Avx.IsSupported,
        };

        return available && (fusable || !fused);
    }

    /// <summary>
    /// Runs one specific implementation, fused or not, regardless of <see cref="SimdConfig"/>; for tests that
    /// compare the paths with each other. The caller checks <see cref="IsSupported"/> first.
    /// </summary>
    internal static void Transform(Span<float> block, IdctPath path, bool fused)
    {
        if (block.Length < 64 || !IsSupported(path, fused))
        {
            throw new ArgumentException("The block is too short or the path is not supported here.", nameof(block));
        }

        if (fused)
        {
            TransformWith<Fused>(block, path);
        }
        else
        {
            TransformWith<Unfused>(block, path);
        }
    }

    /// <summary>
    /// Transforms <paramref name="block"/> like <see cref="Transform(Span{float})"/> and writes the samples, level shifted by 128,
    /// rounded and clamped to 0-255, as eight rows of eight bytes <paramref name="stride"/> apart. The block is
    /// clobbered.
    /// </summary>
    public static void TransformToBytes(Span<float> block, Span<byte> destination, int stride)
    {
        if (block.Length < 64)
        {
            throw new ArgumentException("An IDCT block holds 64 coefficients.", nameof(block));
        }

        if ((7 * stride) + 8 > destination.Length || stride < 8)
        {
            throw new ArgumentException("The destination is too small for an 8x8 block at this stride.", nameof(destination));
        }

        if (SimdConfig.Vector256Enabled && Avx.IsSupported)
        {
            if (UsesFma)
            {
                Transform256ToBytes<Fused>(block, destination, stride);
            }
            else
            {
                Transform256ToBytes<Unfused>(block, destination, stride);
            }

            return;
        }

        Transform(block);
        StoreBytes(block, destination, stride);
    }

    /// <summary>
    /// Transforms <paramref name="block"/> like <see cref="Transform(Span{float})"/> and writes the samples, level shifted by
    /// <c>(maxSample + 1) / 2</c>, rounded and clamped to 0-<paramref name="maxSample"/>, as eight rows of eight
    /// 16-bit samples <paramref name="stride"/> apart. Used for 12-bit frames. The block is clobbered.
    /// </summary>
    public static void TransformToWide(Span<float> block, Span<ushort> destination, int stride, int maxSample)
    {
        Transform(block);
        StoreWide(block, destination, stride, maxSample);
    }

    /// <summary>Level-shifts, rounds and clamps a transformed block into bytes; rounding is <c>(int)(x + 128.5)</c>.</summary>
    internal static void StoreBytes(ReadOnlySpan<float> block, Span<byte> destination, int stride)
    {
        if (SimdConfig.Vector128Enabled)
        {
            ref float source = ref MemoryMarshal.GetReference(block);
            Vector128<float> bias = Vector128.Create(128.5f);
            Vector128<float> high = Vector128.Create(255f);
            for (int y = 0; y < 8; y++)
            {
                Vector128<int> left = Vector128.ConvertToInt32(
                    Vector128.Min(Vector128.Max(Vector128.LoadUnsafe(ref source, (nuint)(y * 8)) + bias, Vector128<float>.Zero), high));
                Vector128<int> right = Vector128.ConvertToInt32(
                    Vector128.Min(Vector128.Max(Vector128.LoadUnsafe(ref source, (nuint)((y * 8) + 4)) + bias, Vector128<float>.Zero), high));
                Vector128<ushort> words = Vector128.Narrow(left, right).AsUInt16();
                ulong packed = Vector128.Narrow(words, words).AsUInt64().ToScalar();
                MemoryMarshal.Write(destination.Slice(y * stride, 8), in packed);
            }

            return;
        }

        for (int y = 0; y < 8; y++)
        {
            Span<byte> row = destination.Slice(y * stride, 8);
            for (int x = 0; x < 8; x++)
            {
                row[x] = (byte)(int)Math.Clamp(block[(y * 8) + x] + 128.5f, 0f, 255f);
            }
        }
    }

    /// <summary>Level-shifts, rounds and clamps a transformed block into 16-bit samples of the given maximum.</summary>
    internal static void StoreWide(ReadOnlySpan<float> block, Span<ushort> destination, int stride, int maxSample)
    {
        float bias = ((maxSample + 1) >> 1) + 0.5f;
        float high = maxSample;
        if (SimdConfig.Vector128Enabled)
        {
            ref float source = ref MemoryMarshal.GetReference(block);
            Vector128<float> biasVector = Vector128.Create(bias);
            Vector128<float> highVector = Vector128.Create(high);
            for (int y = 0; y < 8; y++)
            {
                Vector128<int> left = Vector128.ConvertToInt32(Vector128.Min(
                    Vector128.Max(Vector128.LoadUnsafe(ref source, (nuint)(y * 8)) + biasVector, Vector128<float>.Zero), highVector));
                Vector128<int> right = Vector128.ConvertToInt32(Vector128.Min(
                    Vector128.Max(Vector128.LoadUnsafe(ref source, (nuint)((y * 8) + 4)) + biasVector, Vector128<float>.Zero), highVector));
                Vector128.Narrow(left, right).AsUInt16().CopyTo(destination.Slice(y * stride, 8));
            }

            return;
        }

        for (int y = 0; y < 8; y++)
        {
            Span<ushort> row = destination.Slice(y * stride, 8);
            for (int x = 0; x < 8; x++)
            {
                row[x] = (ushort)(int)Math.Clamp(block[(y * 8) + x] + bias, 0f, high);
            }
        }
    }

    private static void Transform<TOps>(Span<float> block)
        where TOps : struct, IMultiplyOps
    {
        if (SimdConfig.Vector256Enabled && Avx.IsSupported)
        {
            Transform256<TOps>(block);
        }
        else if (SimdConfig.Vector128Enabled)
        {
            Transform128<TOps>(block);
        }
        else
        {
            TransformScalar<TOps>(block);
        }
    }

    private static void TransformWith<TOps>(Span<float> block, IdctPath path)
        where TOps : struct, IMultiplyOps
    {
        switch (path)
        {
            case IdctPath.Vector256:
                Transform256<TOps>(block);
                break;
            case IdctPath.Vector128:
                Transform128<TOps>(block);
                break;
            default:
                TransformScalar<TOps>(block);
                break;
        }
    }

    // ----- 256-bit path: one row per register -----

    private static void Transform256<TOps>(Span<float> block)
        where TOps : struct, IMultiplyOps
    {
        ref float b = ref MemoryMarshal.GetReference(block);
        Vector256<float> r0 = Vector256.LoadUnsafe(ref b, 0);
        Vector256<float> r1 = Vector256.LoadUnsafe(ref b, 8);
        Vector256<float> r2 = Vector256.LoadUnsafe(ref b, 16);
        Vector256<float> r3 = Vector256.LoadUnsafe(ref b, 24);
        Vector256<float> r4 = Vector256.LoadUnsafe(ref b, 32);
        Vector256<float> r5 = Vector256.LoadUnsafe(ref b, 40);
        Vector256<float> r6 = Vector256.LoadUnsafe(ref b, 48);
        Vector256<float> r7 = Vector256.LoadUnsafe(ref b, 56);

        Pass256<TOps>(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Transpose256(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Pass256<TOps>(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Transpose256(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);

        r0.StoreUnsafe(ref b, 0);
        r1.StoreUnsafe(ref b, 8);
        r2.StoreUnsafe(ref b, 16);
        r3.StoreUnsafe(ref b, 24);
        r4.StoreUnsafe(ref b, 32);
        r5.StoreUnsafe(ref b, 40);
        r6.StoreUnsafe(ref b, 48);
        r7.StoreUnsafe(ref b, 56);
    }

    /// <summary>The 256-bit transform followed by level shift, rounding, clamping and narrowing, all in registers.</summary>
    private static void Transform256ToBytes<TOps>(Span<float> block, Span<byte> destination, int stride)
        where TOps : struct, IMultiplyOps
    {
        ref float b = ref MemoryMarshal.GetReference(block);
        Vector256<float> r0 = Vector256.LoadUnsafe(ref b, 0);
        Vector256<float> r1 = Vector256.LoadUnsafe(ref b, 8);
        Vector256<float> r2 = Vector256.LoadUnsafe(ref b, 16);
        Vector256<float> r3 = Vector256.LoadUnsafe(ref b, 24);
        Vector256<float> r4 = Vector256.LoadUnsafe(ref b, 32);
        Vector256<float> r5 = Vector256.LoadUnsafe(ref b, 40);
        Vector256<float> r6 = Vector256.LoadUnsafe(ref b, 48);
        Vector256<float> r7 = Vector256.LoadUnsafe(ref b, 56);

        Pass256<TOps>(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Transpose256(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Pass256<TOps>(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Transpose256(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);

        Vector256<byte> rows0123 = Vector256.Narrow(
            Vector256.Narrow(ToSample(r0), ToSample(r1)).AsUInt16(),
            Vector256.Narrow(ToSample(r2), ToSample(r3)).AsUInt16());
        Vector256<byte> rows4567 = Vector256.Narrow(
            Vector256.Narrow(ToSample(r4), ToSample(r5)).AsUInt16(),
            Vector256.Narrow(ToSample(r6), ToSample(r7)).AsUInt16());

        ref byte d = ref MemoryMarshal.GetReference(destination);
        Vector256<ulong> low = rows0123.AsUInt64();
        Vector256<ulong> high = rows4567.AsUInt64();
        Unsafe.WriteUnaligned(ref d, low.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, stride), low.GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 2 * stride), low.GetElement(2));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 3 * stride), low.GetElement(3));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 4 * stride), high.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 5 * stride), high.GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 6 * stride), high.GetElement(2));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 7 * stride), high.GetElement(3));

        static Vector256<int> ToSample(Vector256<float> value)
            => Vector256.ConvertToInt32(Vector256.Min(
                Vector256.Max(value + Vector256.Create(128.5f), Vector256<float>.Zero), Vector256.Create(255f)));
    }

    /// <summary>One AAN pass over eight vectors: lane i of every vector together forms one 8-point column.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pass256<TOps>(
        ref Vector256<float> r0, ref Vector256<float> r1, ref Vector256<float> r2, ref Vector256<float> r3,
        ref Vector256<float> r4, ref Vector256<float> r5, ref Vector256<float> r6, ref Vector256<float> r7)
        where TOps : struct, IMultiplyOps
    {
        // Even part.
        Vector256<float> tmp10 = r0 + r4;
        Vector256<float> tmp11 = r0 - r4;
        Vector256<float> tmp13 = r2 + r6;
        Vector256<float> tmp12 = TOps.MultiplySubtract(r2 - r6, Vector256.Create(C1414), tmp13);
        Vector256<float> e0 = tmp10 + tmp13;
        Vector256<float> e3 = tmp10 - tmp13;
        Vector256<float> e1 = tmp11 + tmp12;
        Vector256<float> e2 = tmp11 - tmp12;

        // Odd part.
        Vector256<float> z13 = r5 + r3;
        Vector256<float> z10 = r5 - r3;
        Vector256<float> z11 = r1 + r7;
        Vector256<float> z12 = r1 - r7;
        Vector256<float> o7 = z11 + z13;
        Vector256<float> t11 = (z11 - z13) * Vector256.Create(C1414);
        Vector256<float> z5 = (z10 + z12) * Vector256.Create(C1847);
        Vector256<float> t10 = TOps.SubtractMultiply(z5, z12, Vector256.Create(C1082));
        Vector256<float> t12 = TOps.SubtractMultiply(z5, z10, Vector256.Create(C2613));
        Vector256<float> o6 = t12 - o7;
        Vector256<float> o5 = t11 - o6;
        Vector256<float> o4 = t10 - o5;

        r0 = e0 + o7;
        r7 = e0 - o7;
        r1 = e1 + o6;
        r6 = e1 - o6;
        r2 = e2 + o5;
        r5 = e2 - o5;
        r3 = e3 + o4;
        r4 = e3 - o4;
    }

    /// <summary>Transposes an 8x8 matrix held as eight 256-bit rows (AVX unpack, shuffle and lane permute).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose256(
        ref Vector256<float> r0, ref Vector256<float> r1, ref Vector256<float> r2, ref Vector256<float> r3,
        ref Vector256<float> r4, ref Vector256<float> r5, ref Vector256<float> r6, ref Vector256<float> r7)
    {
        Vector256<float> t0 = Avx.UnpackLow(r0, r1);
        Vector256<float> t1 = Avx.UnpackHigh(r0, r1);
        Vector256<float> t2 = Avx.UnpackLow(r2, r3);
        Vector256<float> t3 = Avx.UnpackHigh(r2, r3);
        Vector256<float> t4 = Avx.UnpackLow(r4, r5);
        Vector256<float> t5 = Avx.UnpackHigh(r4, r5);
        Vector256<float> t6 = Avx.UnpackLow(r6, r7);
        Vector256<float> t7 = Avx.UnpackHigh(r6, r7);

        Vector256<float> s0 = Avx.Shuffle(t0, t2, 0x44);
        Vector256<float> s1 = Avx.Shuffle(t0, t2, 0xEE);
        Vector256<float> s2 = Avx.Shuffle(t1, t3, 0x44);
        Vector256<float> s3 = Avx.Shuffle(t1, t3, 0xEE);
        Vector256<float> s4 = Avx.Shuffle(t4, t6, 0x44);
        Vector256<float> s5 = Avx.Shuffle(t4, t6, 0xEE);
        Vector256<float> s6 = Avx.Shuffle(t5, t7, 0x44);
        Vector256<float> s7 = Avx.Shuffle(t5, t7, 0xEE);

        r0 = Avx.Permute2x128(s0, s4, 0x20);
        r1 = Avx.Permute2x128(s1, s5, 0x20);
        r2 = Avx.Permute2x128(s2, s6, 0x20);
        r3 = Avx.Permute2x128(s3, s7, 0x20);
        r4 = Avx.Permute2x128(s0, s4, 0x31);
        r5 = Avx.Permute2x128(s1, s5, 0x31);
        r6 = Avx.Permute2x128(s2, s6, 0x31);
        r7 = Avx.Permute2x128(s3, s7, 0x31);
    }

    // ----- 128-bit path: each row is a left and a right half -----

    private static void Transform128<TOps>(Span<float> block)
        where TOps : struct, IMultiplyOps
    {
        ref float b = ref MemoryMarshal.GetReference(block);
        Vector128<float> l0 = Vector128.LoadUnsafe(ref b, 0);
        Vector128<float> h0 = Vector128.LoadUnsafe(ref b, 4);
        Vector128<float> l1 = Vector128.LoadUnsafe(ref b, 8);
        Vector128<float> h1 = Vector128.LoadUnsafe(ref b, 12);
        Vector128<float> l2 = Vector128.LoadUnsafe(ref b, 16);
        Vector128<float> h2 = Vector128.LoadUnsafe(ref b, 20);
        Vector128<float> l3 = Vector128.LoadUnsafe(ref b, 24);
        Vector128<float> h3 = Vector128.LoadUnsafe(ref b, 28);
        Vector128<float> l4 = Vector128.LoadUnsafe(ref b, 32);
        Vector128<float> h4 = Vector128.LoadUnsafe(ref b, 36);
        Vector128<float> l5 = Vector128.LoadUnsafe(ref b, 40);
        Vector128<float> h5 = Vector128.LoadUnsafe(ref b, 44);
        Vector128<float> l6 = Vector128.LoadUnsafe(ref b, 48);
        Vector128<float> h6 = Vector128.LoadUnsafe(ref b, 52);
        Vector128<float> l7 = Vector128.LoadUnsafe(ref b, 56);
        Vector128<float> h7 = Vector128.LoadUnsafe(ref b, 60);

        for (int pass = 0; pass < 2; pass++)
        {
            Pass128<TOps>(ref l0, ref l1, ref l2, ref l3, ref l4, ref l5, ref l6, ref l7);
            Pass128<TOps>(ref h0, ref h1, ref h2, ref h3, ref h4, ref h5, ref h6, ref h7);

            // [A B; C D] transposed is [A' C'; B' D'], where A = rows 0-3 left, B = rows 0-3 right,
            // C = rows 4-7 left and D = rows 4-7 right.
            if (Sse.IsSupported || AdvSimd.Arm64.IsSupported)
            {
                Transpose4x4(ref l0, ref l1, ref l2, ref l3);
                Transpose4x4(ref h0, ref h1, ref h2, ref h3);
                Transpose4x4(ref l4, ref l5, ref l6, ref l7);
                Transpose4x4(ref h4, ref h5, ref h6, ref h7);
                (h0, l4) = (l4, h0);
                (h1, l5) = (l5, h1);
                (h2, l6) = (l6, h2);
                (h3, l7) = (l7, h3);
            }
            else
            {
                // No two-source shuffle to build the transpose from: go through memory.
                l0.StoreUnsafe(ref b, 0);
                h0.StoreUnsafe(ref b, 4);
                l1.StoreUnsafe(ref b, 8);
                h1.StoreUnsafe(ref b, 12);
                l2.StoreUnsafe(ref b, 16);
                h2.StoreUnsafe(ref b, 20);
                l3.StoreUnsafe(ref b, 24);
                h3.StoreUnsafe(ref b, 28);
                l4.StoreUnsafe(ref b, 32);
                h4.StoreUnsafe(ref b, 36);
                l5.StoreUnsafe(ref b, 40);
                h5.StoreUnsafe(ref b, 44);
                l6.StoreUnsafe(ref b, 48);
                h6.StoreUnsafe(ref b, 52);
                l7.StoreUnsafe(ref b, 56);
                h7.StoreUnsafe(ref b, 60);
                TransposeInPlace(block);
                l0 = Vector128.LoadUnsafe(ref b, 0);
                h0 = Vector128.LoadUnsafe(ref b, 4);
                l1 = Vector128.LoadUnsafe(ref b, 8);
                h1 = Vector128.LoadUnsafe(ref b, 12);
                l2 = Vector128.LoadUnsafe(ref b, 16);
                h2 = Vector128.LoadUnsafe(ref b, 20);
                l3 = Vector128.LoadUnsafe(ref b, 24);
                h3 = Vector128.LoadUnsafe(ref b, 28);
                l4 = Vector128.LoadUnsafe(ref b, 32);
                h4 = Vector128.LoadUnsafe(ref b, 36);
                l5 = Vector128.LoadUnsafe(ref b, 40);
                h5 = Vector128.LoadUnsafe(ref b, 44);
                l6 = Vector128.LoadUnsafe(ref b, 48);
                h6 = Vector128.LoadUnsafe(ref b, 52);
                l7 = Vector128.LoadUnsafe(ref b, 56);
                h7 = Vector128.LoadUnsafe(ref b, 60);
            }
        }

        l0.StoreUnsafe(ref b, 0);
        h0.StoreUnsafe(ref b, 4);
        l1.StoreUnsafe(ref b, 8);
        h1.StoreUnsafe(ref b, 12);
        l2.StoreUnsafe(ref b, 16);
        h2.StoreUnsafe(ref b, 20);
        l3.StoreUnsafe(ref b, 24);
        h3.StoreUnsafe(ref b, 28);
        l4.StoreUnsafe(ref b, 32);
        h4.StoreUnsafe(ref b, 36);
        l5.StoreUnsafe(ref b, 40);
        h5.StoreUnsafe(ref b, 44);
        l6.StoreUnsafe(ref b, 48);
        h6.StoreUnsafe(ref b, 52);
        l7.StoreUnsafe(ref b, 56);
        h7.StoreUnsafe(ref b, 60);
    }

    /// <summary>The <see cref="Pass256{TOps}"/> butterfly on four columns.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pass128<TOps>(
        ref Vector128<float> r0, ref Vector128<float> r1, ref Vector128<float> r2, ref Vector128<float> r3,
        ref Vector128<float> r4, ref Vector128<float> r5, ref Vector128<float> r6, ref Vector128<float> r7)
        where TOps : struct, IMultiplyOps
    {
        Vector128<float> tmp10 = r0 + r4;
        Vector128<float> tmp11 = r0 - r4;
        Vector128<float> tmp13 = r2 + r6;
        Vector128<float> tmp12 = TOps.MultiplySubtract(r2 - r6, Vector128.Create(C1414), tmp13);
        Vector128<float> e0 = tmp10 + tmp13;
        Vector128<float> e3 = tmp10 - tmp13;
        Vector128<float> e1 = tmp11 + tmp12;
        Vector128<float> e2 = tmp11 - tmp12;

        Vector128<float> z13 = r5 + r3;
        Vector128<float> z10 = r5 - r3;
        Vector128<float> z11 = r1 + r7;
        Vector128<float> z12 = r1 - r7;
        Vector128<float> o7 = z11 + z13;
        Vector128<float> t11 = (z11 - z13) * Vector128.Create(C1414);
        Vector128<float> z5 = (z10 + z12) * Vector128.Create(C1847);
        Vector128<float> t10 = TOps.SubtractMultiply(z5, z12, Vector128.Create(C1082));
        Vector128<float> t12 = TOps.SubtractMultiply(z5, z10, Vector128.Create(C2613));
        Vector128<float> o6 = t12 - o7;
        Vector128<float> o5 = t11 - o6;
        Vector128<float> o4 = t10 - o5;

        r0 = e0 + o7;
        r7 = e0 - o7;
        r1 = e1 + o6;
        r6 = e1 - o6;
        r2 = e2 + o5;
        r5 = e2 - o5;
        r3 = e3 + o4;
        r4 = e3 - o4;
    }

    /// <summary>Transposes a 4x4 matrix held as four 128-bit rows (SSE unpack and move, or Arm64 zips).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose4x4(ref Vector128<float> a, ref Vector128<float> b, ref Vector128<float> c, ref Vector128<float> d)
    {
        if (Sse.IsSupported)
        {
            Vector128<float> t0 = Sse.UnpackLow(a, b);
            Vector128<float> t1 = Sse.UnpackHigh(a, b);
            Vector128<float> t2 = Sse.UnpackLow(c, d);
            Vector128<float> t3 = Sse.UnpackHigh(c, d);
            a = Sse.MoveLowToHigh(t0, t2);
            b = Sse.MoveHighToLow(t2, t0);
            c = Sse.MoveLowToHigh(t1, t3);
            d = Sse.MoveHighToLow(t3, t1);
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            Vector128<double> t0 = AdvSimd.Arm64.ZipLow(a, b).AsDouble();
            Vector128<double> t1 = AdvSimd.Arm64.ZipHigh(a, b).AsDouble();
            Vector128<double> t2 = AdvSimd.Arm64.ZipLow(c, d).AsDouble();
            Vector128<double> t3 = AdvSimd.Arm64.ZipHigh(c, d).AsDouble();
            a = AdvSimd.Arm64.ZipLow(t0, t2).AsSingle();
            b = AdvSimd.Arm64.ZipHigh(t0, t2).AsSingle();
            c = AdvSimd.Arm64.ZipLow(t1, t3).AsSingle();
            d = AdvSimd.Arm64.ZipHigh(t1, t3).AsSingle();
        }
        else
        {
            throw new PlatformNotSupportedException();
        }
    }

    // ----- Scalar reference path -----

    private static void TransformScalar<TOps>(Span<float> block)
        where TOps : struct, IMultiplyOps
    {
        // Column pass, then row pass: the same order, and the same operations per element, as the vector paths.
        for (int column = 0; column < 8; column++)
        {
            PassScalar<TOps>(block, column, 8);
        }

        for (int row = 0; row < 8; row++)
        {
            PassScalar<TOps>(block, row * 8, 1);
        }
    }

    /// <summary>One AAN butterfly over the eight elements <c>start, start + step, ..., start + 7 step</c>.</summary>
    private static void PassScalar<TOps>(Span<float> data, int start, int step)
        where TOps : struct, IMultiplyOps
    {
        float r0 = data[start];
        float r1 = data[start + step];
        float r2 = data[start + (2 * step)];
        float r3 = data[start + (3 * step)];
        float r4 = data[start + (4 * step)];
        float r5 = data[start + (5 * step)];
        float r6 = data[start + (6 * step)];
        float r7 = data[start + (7 * step)];

        float tmp10 = r0 + r4;
        float tmp11 = r0 - r4;
        float tmp13 = r2 + r6;
        float tmp12 = TOps.MultiplySubtract(r2 - r6, C1414, tmp13);
        float e0 = tmp10 + tmp13;
        float e3 = tmp10 - tmp13;
        float e1 = tmp11 + tmp12;
        float e2 = tmp11 - tmp12;

        float z13 = r5 + r3;
        float z10 = r5 - r3;
        float z11 = r1 + r7;
        float z12 = r1 - r7;
        float o7 = z11 + z13;
        float t11 = (z11 - z13) * C1414;
        float z5 = (z10 + z12) * C1847;
        float t10 = TOps.SubtractMultiply(z5, z12, C1082);
        float t12 = TOps.SubtractMultiply(z5, z10, C2613);
        float o6 = t12 - o7;
        float o5 = t11 - o6;
        float o4 = t10 - o5;

        data[start] = e0 + o7;
        data[start + (7 * step)] = e0 - o7;
        data[start + step] = e1 + o6;
        data[start + (6 * step)] = e1 - o6;
        data[start + (2 * step)] = e2 + o5;
        data[start + (5 * step)] = e2 - o5;
        data[start + (3 * step)] = e3 + o4;
        data[start + (4 * step)] = e3 - o4;
    }

    private static void TransposeInPlace(Span<float> block)
    {
        for (int y = 0; y < 8; y++)
        {
            for (int x = y + 1; x < 8; x++)
            {
                (block[(y * 8) + x], block[(x * 8) + y]) = (block[(x * 8) + y], block[(y * 8) + x]);
            }
        }
    }

    private static double[] CreateAanScale()
    {
        var scale = new double[8];
        scale[0] = 1.0;
        for (int k = 1; k < 8; k++)
        {
            scale[k] = Math.Sqrt(2.0) * Math.Cos(k * Math.PI / 16.0);
        }

        return scale;
    }

    /// <summary>
    /// The two multiply-then-add shapes of the butterfly, in a fused and an unfused flavour. Making the flavour a
    /// type parameter lets the JIT compile each path once per flavour with no per-operation branch.
    /// </summary>
    private interface IMultiplyOps
    {
        /// <summary>Computes <c>a * b - c</c>.</summary>
        static abstract float MultiplySubtract(float a, float b, float c);

        /// <summary>Computes <c>a - b * c</c>.</summary>
        static abstract float SubtractMultiply(float a, float b, float c);

        static abstract Vector128<float> MultiplySubtract(Vector128<float> a, Vector128<float> b, Vector128<float> c);

        static abstract Vector128<float> SubtractMultiply(Vector128<float> a, Vector128<float> b, Vector128<float> c);

        static abstract Vector256<float> MultiplySubtract(Vector256<float> a, Vector256<float> b, Vector256<float> c);

        static abstract Vector256<float> SubtractMultiply(Vector256<float> a, Vector256<float> b, Vector256<float> c);
    }

    /// <summary>Separate multiplication and addition, each rounded.</summary>
    private readonly struct Unfused : IMultiplyOps
    {
        public static float MultiplySubtract(float a, float b, float c) => (a * b) - c;

        public static float SubtractMultiply(float a, float b, float c) => a - (b * c);

        public static Vector128<float> MultiplySubtract(Vector128<float> a, Vector128<float> b, Vector128<float> c) => (a * b) - c;

        public static Vector128<float> SubtractMultiply(Vector128<float> a, Vector128<float> b, Vector128<float> c) => a - (b * c);

        public static Vector256<float> MultiplySubtract(Vector256<float> a, Vector256<float> b, Vector256<float> c) => (a * b) - c;

        public static Vector256<float> SubtractMultiply(Vector256<float> a, Vector256<float> b, Vector256<float> c) => a - (b * c);
    }

    /// <summary>
    /// Fused multiply-add: the exact product and sum, rounded once. Every flavour computes the same exact value
    /// (negating an operand is exact), so they round identically.
    /// </summary>
    private readonly struct Fused : IMultiplyOps
    {
        public static float MultiplySubtract(float a, float b, float c) => MathF.FusedMultiplyAdd(a, b, -c);

        public static float SubtractMultiply(float a, float b, float c) => MathF.FusedMultiplyAdd(-b, c, a);

        public static Vector128<float> MultiplySubtract(Vector128<float> a, Vector128<float> b, Vector128<float> c)
            => Fma.IsSupported ? Fma.MultiplySubtract(a, b, c) : AdvSimd.FusedMultiplyAdd(-c, a, b);

        public static Vector128<float> SubtractMultiply(Vector128<float> a, Vector128<float> b, Vector128<float> c)
            => Fma.IsSupported ? Fma.MultiplyAddNegated(b, c, a) : AdvSimd.FusedMultiplySubtract(a, b, c);

        public static Vector256<float> MultiplySubtract(Vector256<float> a, Vector256<float> b, Vector256<float> c)
            => Fma.MultiplySubtract(a, b, c);

        public static Vector256<float> SubtractMultiply(Vector256<float> a, Vector256<float> b, Vector256<float> c)
            => Fma.MultiplyAddNegated(b, c, a);
    }
}
