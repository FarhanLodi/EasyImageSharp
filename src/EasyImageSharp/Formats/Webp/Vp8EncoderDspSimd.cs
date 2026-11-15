using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// 128-bit vector versions of the <see cref="Vp8EncoderDsp"/> primitives the mode decision runs hundreds of times
/// per macroblock: the forward 4x4 DCT, the sum of squared errors and the block quantiser. Like
/// <see cref="Vp8DspSimd"/>, every kernel evaluates the scalar reference's integer expressions lane by lane (the
/// transforms in 32-bit lanes, as the scalar code does), so results are bit-identical for every input.
/// </summary>
internal static class Vp8EncoderDspSimd
{
    private const int Bps = Vp8Dsp.Bps;

    /// <summary>The fixed-point precision of the quantiser's reciprocals.</summary>
    private const int QFix = 17;

    /// <summary>True when the kernels may run; they share the requirements of <see cref="Vp8DspSimd"/>.</summary>
    public static bool IsEnabled => Vp8DspSimd.IsEnabled;

    /// <summary>Forward 4x4 DCT of <c>src - pred</c>; bit-exact with <see cref="Vp8EncoderDsp.FTransformScalar"/>.</summary>
    public static void FTransform(byte[] src, int srcOff, byte[] pred, int predOff, short[] output, int outOff)
    {
        // Gathering the block row by row and regrouping the bytes by column gives column vectors whose lane i is
        // row i, which is the layout of the scalar code's first (horizontal) pass.
        Vector128<byte> byColumn = Vector128.Create((byte)0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15);
        Vector128<byte> source = Vp8DspSimd.Shuffle(Gather4x4(src, srcOff), byColumn);
        Vector128<byte> prediction = Vp8DspSimd.Shuffle(Gather4x4(pred, predOff), byColumn);
        Vector128<short> d01 = Vector128.WidenLower(source).AsInt16() - Vector128.WidenLower(prediction).AsInt16();
        Vector128<short> d23 = Vector128.WidenUpper(source).AsInt16() - Vector128.WidenUpper(prediction).AsInt16();
        Vector128<int> d0 = Vector128.WidenLower(d01);
        Vector128<int> d1 = Vector128.WidenUpper(d01);
        Vector128<int> d2 = Vector128.WidenLower(d23);
        Vector128<int> d3 = Vector128.WidenUpper(d23);

        Vector128<int> a0 = d0 + d3;
        Vector128<int> a1 = d1 + d2;
        Vector128<int> a2 = d1 - d2;
        Vector128<int> a3 = d0 - d3;
        Vector128<int> t0 = (a0 + a1) * 8;
        Vector128<int> t1 = Vector128.ShiftRightArithmetic((a2 * 2217) + (a3 * 5352) + Vector128.Create(1812), 9);
        Vector128<int> t2 = (a0 - a1) * 8;
        Vector128<int> t3 = Vector128.ShiftRightArithmetic((a3 * 2217) - (a2 * 5352) + Vector128.Create(937), 9);

        // tj lane i is tmp[4i + j]; transposed, tr lane i is tmp[4r + i] and lane i of the vertical pass is column i.
        Vp8DspSimd.Transpose4x4(ref t0, ref t1, ref t2, ref t3);
        a0 = t0 + t3;
        a1 = t1 + t2;
        a2 = t1 - t2;
        a3 = t0 - t3;
        Vector128<int> out0 = Vector128.ShiftRightArithmetic(a0 + a1 + Vector128.Create(7), 4);

        // (a3 != 0 ? 1 : 0) is 1 + (a3 == 0 ? -1 : 0), the all-ones comparison mask read as an integer.
        Vector128<int> out1 = Vector128.ShiftRightArithmetic((a2 * 2217) + (a3 * 5352) + Vector128.Create(12000), 16)
            + Vector128.Create(1) + Vector128.Equals(a3, Vector128<int>.Zero);
        Vector128<int> out2 = Vector128.ShiftRightArithmetic(a0 - a1 + Vector128.Create(7), 4);
        Vector128<int> out3 = Vector128.ShiftRightArithmetic((a3 * 2217) - (a2 * 5352) + Vector128.Create(51000), 16);

        ref short destination = ref MemoryMarshal.GetReference(output.AsSpan(outOff, 16));
        Vector128.Narrow(out0, out1).StoreUnsafe(ref destination);
        Vector128.Narrow(out2, out3).StoreUnsafe(ref destination, 8);
    }

    /// <summary>Sum of squared differences over a 4x4, 8x8 or 16x16 block of stride 32.</summary>
    public static int Sse(byte[] a, int aOff, byte[] b, int bOff, int size)
    {
        int length = ((size - 1) * Bps) + size;
        ref byte first = ref MemoryMarshal.GetReference(a.AsSpan(aOff, length));
        ref byte second = ref MemoryMarshal.GetReference(b.AsSpan(bOff, length));
        Vector128<int> sum = Vector128<int>.Zero;
        if (size == 16)
        {
            for (int y = 0; y < 16; y++)
            {
                nuint offset = (nuint)(y * Bps);
                sum += SquaredDifferences(Vector128.LoadUnsafe(ref first, offset), Vector128.LoadUnsafe(ref second, offset));
            }
        }
        else if (size == 8)
        {
            for (int y = 0; y < 8; y += 2)
            {
                sum += SquaredDifferences(LoadRows8(ref first, y * Bps), LoadRows8(ref second, y * Bps));
            }
        }
        else
        {
            sum = SquaredDifferences(Gather4x4(ref first), Gather4x4(ref second));
        }

        return Vector128.Sum(sum);
    }

    /// <summary>
    /// Quantises one block of sixteen raster-order coefficients in place and writes the zig-zag ordered levels;
    /// bit-exact with <see cref="Vp8EncoderDsp.QuantizeBlockScalar"/>. Returns the zig-zag index of the last
    /// non-zero level, or -1.
    /// </summary>
    public static int QuantizeBlock(
        short[] coefficients, int coeffOff, short[] levels, int levelOff,
        ushort[] q, int[] iq, int[] bias, int[] zeroThreshold, int[] sharpen)
    {
        ref short coeffs = ref MemoryMarshal.GetReference(coefficients.AsSpan(coeffOff, 16));
        ref ushort steps = ref MemoryMarshal.GetReference(q.AsSpan(0, 16));
        ref int reciprocals = ref MemoryMarshal.GetReference(iq.AsSpan(0, 16));
        ref int biases = ref MemoryMarshal.GetReference(bias.AsSpan(0, 16));
        ref int thresholds = ref MemoryMarshal.GetReference(zeroThreshold.AsSpan(0, 16));
        ref int sharpening = ref MemoryMarshal.GetReference(sharpen.AsSpan(0, 16));

        Vector128<short> c0 = Vector128.LoadUnsafe(ref coeffs);
        Vector128<short> c1 = Vector128.LoadUnsafe(ref coeffs, 8);
        Vector128<ushort> q0 = Vector128.LoadUnsafe(ref steps);
        Vector128<ushort> q1 = Vector128.LoadUnsafe(ref steps, 8);

        Vector128<int> l0 = QuantizeQuarter(Vector128.WidenLower(c0), Vector128.WidenLower(q0).AsInt32(), ref reciprocals, ref biases, ref thresholds, ref sharpening, 0, out Vector128<int> v0);
        Vector128<int> l1 = QuantizeQuarter(Vector128.WidenUpper(c0), Vector128.WidenUpper(q0).AsInt32(), ref reciprocals, ref biases, ref thresholds, ref sharpening, 4, out Vector128<int> v1);
        Vector128<int> l2 = QuantizeQuarter(Vector128.WidenLower(c1), Vector128.WidenLower(q1).AsInt32(), ref reciprocals, ref biases, ref thresholds, ref sharpening, 8, out Vector128<int> v2);
        Vector128<int> l3 = QuantizeQuarter(Vector128.WidenUpper(c1), Vector128.WidenUpper(q1).AsInt32(), ref reciprocals, ref biases, ref thresholds, ref sharpening, 12, out Vector128<int> v3);
        Vector128.Narrow(v0, v1).StoreUnsafe(ref coeffs);
        Vector128.Narrow(v2, v3).StoreUnsafe(ref coeffs, 8);

        // Raster to zig-zag order (0 1 4 8 5 2 3 6 | 9 12 13 10 7 11 14 15): each output half gathers 16-bit lanes
        // from both raster halves with two byte shuffles whose unused lanes select zero.
        Vector128<byte> raster0 = Vector128.Narrow(l0, l1).AsByte();
        Vector128<byte> raster1 = Vector128.Narrow(l2, l3).AsByte();
        Vector128<byte> low = Vp8DspSimd.Shuffle(raster0, Vector128.Create((byte)0, 1, 2, 3, 8, 9, 0xFF, 0xFF, 10, 11, 4, 5, 6, 7, 12, 13))
            | Vp8DspSimd.Shuffle(raster1, Vector128.Create((byte)0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF));
        Vector128<byte> high = Vp8DspSimd.Shuffle(raster1, Vector128.Create((byte)2, 3, 8, 9, 10, 11, 4, 5, 0xFF, 0xFF, 6, 7, 12, 13, 14, 15))
            | Vp8DspSimd.Shuffle(raster0, Vector128.Create((byte)0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 14, 15, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF));
        ref short levelOut = ref MemoryMarshal.GetReference(levels.AsSpan(levelOff, 16));
        low.AsInt16().StoreUnsafe(ref levelOut);
        high.AsInt16().StoreUnsafe(ref levelOut, 8);

        Vector128<short> zero = Vector128<short>.Zero;
        uint zeroLanes = Vector128.Narrow(Vector128.Equals(low.AsInt16(), zero), Vector128.Equals(high.AsInt16(), zero)).ExtractMostSignificantBits();
        uint nonZero = ~zeroLanes & 0xFFFF;
        return nonZero == 0 ? -1 : 31 - BitOperations.LeadingZeroCount(nonZero);
    }

    /// <summary>Quantises four coefficients; returns the signed levels and the dequantised values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> QuantizeQuarter(
        Vector128<int> value, Vector128<int> step,
        ref int reciprocals, ref int biases, ref int thresholds, ref int sharpening, int index,
        out Vector128<int> dequantized)
    {
        Vector128<int> negative = Vector128.LessThan(value, Vector128<int>.Zero);
        Vector128<int> magnitude = Vector128.Abs(value) + Vector128.LoadUnsafe(ref sharpening, (nuint)index);
        Vector128<int> keep = Vector128.GreaterThan(magnitude, Vector128.LoadUnsafe(ref thresholds, (nuint)index));
        Vector128<int> level = Vector128.ShiftRightArithmetic(
            (magnitude * Vector128.LoadUnsafe(ref reciprocals, (nuint)index)) + Vector128.LoadUnsafe(ref biases, (nuint)index), QFix);
        level = Vector128.Min(level, Vector128.Create(Vp8Cost.MaxLevel));
        level = Vector128.ConditionalSelect(negative, -level, level) & keep;
        dequantized = level * step;
        return level;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> SquaredDifferences(Vector128<byte> a, Vector128<byte> b)
    {
        Vector128<short> low = Vector128.WidenLower(a).AsInt16() - Vector128.WidenLower(b).AsInt16();
        Vector128<short> high = Vector128.WidenUpper(a).AsInt16() - Vector128.WidenUpper(b).AsInt16();
        if (Sse2.IsSupported)
        {
            return Sse2.MultiplyAddAdjacent(low, low) + Sse2.MultiplyAddAdjacent(high, high);
        }

        // A squared difference of two bytes is at most 65025, so the 16-bit product is exact read as unsigned.
        Vector128<ushort> lowSquares = (low * low).AsUInt16();
        Vector128<ushort> highSquares = (high * high).AsUInt16();
        return (Vector128.WidenLower(lowSquares) + Vector128.WidenUpper(lowSquares)
            + Vector128.WidenLower(highSquares) + Vector128.WidenUpper(highSquares)).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> LoadRows8(ref byte block, int offset)
        => Vector128.Create(
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref block, offset)),
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref block, offset + Bps))).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Gather4x4(byte[] buffer, int off)
        => Gather4x4(ref MemoryMarshal.GetReference(buffer.AsSpan(off, (3 * Bps) + 4)));

    /// <summary>The four 4-byte rows of a block, row r in bytes 4r to 4r + 3.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Gather4x4(ref byte block)
        => Vector128.Create(
            Unsafe.ReadUnaligned<uint>(ref block),
            Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref block, Bps)),
            Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref block, 2 * Bps)),
            Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref block, 3 * Bps))).AsByte();
}
