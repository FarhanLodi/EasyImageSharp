using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// Chroma upsampling for the JPEG decoder. The two common layouts, 2:1 horizontal (4:2:2) and 2:1 in both
/// directions (4:2:0), use the triangle filter popularised by libjpeg's "fancy upsampling" (jdsample.c):
/// each output sample is 3/4 of the nearest input sample plus 1/4 of the next nearest, with the image edges
/// replicated. Every other ratio falls back to sample replication.
/// </summary>
/// <remarks>
/// The 8-bit kernels have a 128-bit vector path for the interior of the row. It evaluates the same integer
/// expressions as the scalar loop on sixteen samples at once and interleaves the even and odd outputs by packing
/// each pair into one 16-bit lane (even in the low byte), so its output is identical byte for byte. The wide
/// (16-bit sample) kernels serve 12-bit and lossless frames and are scalar.
/// </remarks>
internal static class JpegUpsampler
{
    /// <summary>
    /// 2:1 horizontal triangle upsampling of one row: out[2i] = (3 in[i] + in[i-1] + 1) / 4 and
    /// out[2i+1] = (3 in[i] + in[i+1] + 2) / 4, with the first and last output samples copied from the edges.
    /// <paramref name="output"/> must hold twice as many samples as <paramref name="input"/>, which needs at least two.
    /// </summary>
    internal static void UpsampleH2V1(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int n = input.Length;
        if (n < 2 || output.Length < 2 * n)
        {
            throw new ArgumentException("Upsampling needs two input samples and room for twice as many outputs.", nameof(output));
        }

        int last = n - 1;
        output[0] = input[0];
        output[1] = (byte)(((3 * input[0]) + input[1] + 2) >> 2);
        int i = 1;
        if (SimdConfig.Vector128Enabled && BitConverter.IsLittleEndian)
        {
            i = UpsampleH2V1Vector(input, output, last);
        }

        int o = 2 * i;
        for (; i < last; i++)
        {
            int nearer = 3 * input[i];
            output[o++] = (byte)((nearer + input[i - 1] + 1) >> 2);
            output[o++] = (byte)((nearer + input[i + 1] + 2) >> 2);
        }

        output[o++] = (byte)(((3 * input[last]) + input[last - 1] + 1) >> 2);
        output[o] = input[last];
    }

    /// <summary>
    /// 2:1 triangle upsampling in both directions for one output row. <paramref name="nearRow"/> is the input
    /// row the output row belongs to and <paramref name="farRow"/> its vertical neighbour on the output row's
    /// side (the row above for even output rows, below for odd). Vertically each column sum is
    /// 3 near + far; horizontally the sums are blended 3:1 again, giving weights 9/16, 3/16, 3/16, 1/16.
    /// </summary>
    internal static void UpsampleH2V2(ReadOnlySpan<byte> nearRow, ReadOnlySpan<byte> farRow, Span<byte> output)
    {
        int n = nearRow.Length;
        if (n < 2 || farRow.Length < n || output.Length < 2 * n)
        {
            throw new ArgumentException("Upsampling needs two input samples and room for twice as many outputs.", nameof(output));
        }

        int current = (3 * nearRow[0]) + farRow[0];
        int next = (3 * nearRow[1]) + farRow[1];
        output[0] = (byte)(((current * 4) + 8) >> 4);
        output[1] = (byte)(((current * 3) + next + 7) >> 4);
        int i = 1;
        if (SimdConfig.Vector128Enabled && BitConverter.IsLittleEndian)
        {
            i = UpsampleH2V2Vector(nearRow, farRow, output, n - 1);
        }

        int previous = (3 * nearRow[i - 1]) + farRow[i - 1];
        current = (3 * nearRow[i]) + farRow[i];
        int o = 2 * i;
        for (; i < n - 1; i++)
        {
            next = (3 * nearRow[i + 1]) + farRow[i + 1];
            output[o++] = (byte)(((current * 3) + previous + 8) >> 4);
            output[o++] = (byte)(((current * 3) + next + 7) >> 4);
            previous = current;
            current = next;
        }

        output[o++] = (byte)(((current * 3) + previous + 8) >> 4);
        output[o] = (byte)(((current * 4) + 7) >> 4);
    }

    /// <summary>The 16-bit sample counterpart of <see cref="UpsampleH2V1(ReadOnlySpan{byte}, Span{byte})"/>.</summary>
    internal static void UpsampleH2V1(ReadOnlySpan<ushort> input, Span<ushort> output)
    {
        int n = input.Length;
        int last = n - 1;
        output[0] = input[0];
        output[1] = (ushort)(((3 * input[0]) + input[1] + 2) >> 2);
        int o = 2;
        for (int i = 1; i < last; i++)
        {
            int nearer = 3 * input[i];
            output[o++] = (ushort)((nearer + input[i - 1] + 1) >> 2);
            output[o++] = (ushort)((nearer + input[i + 1] + 2) >> 2);
        }

        output[o++] = (ushort)(((3 * input[last]) + input[last - 1] + 1) >> 2);
        output[o] = input[last];
    }

    /// <summary>The 16-bit sample counterpart of <see cref="UpsampleH2V2(ReadOnlySpan{byte}, ReadOnlySpan{byte}, Span{byte})"/>.</summary>
    internal static void UpsampleH2V2(ReadOnlySpan<ushort> nearRow, ReadOnlySpan<ushort> farRow, Span<ushort> output)
    {
        int n = nearRow.Length;
        int current = (3 * nearRow[0]) + farRow[0];
        int next = (3 * nearRow[1]) + farRow[1];
        output[0] = (ushort)(((current * 4) + 8) >> 4);
        output[1] = (ushort)(((current * 3) + next + 7) >> 4);
        int previous = current;
        current = next;
        int o = 2;
        for (int i = 1; i < n - 1; i++)
        {
            next = (3 * nearRow[i + 1]) + farRow[i + 1];
            output[o++] = (ushort)(((current * 3) + previous + 8) >> 4);
            output[o++] = (ushort)(((current * 3) + next + 7) >> 4);
            previous = current;
            current = next;
        }

        output[o++] = (ushort)(((current * 3) + previous + 8) >> 4);
        output[o] = (ushort)(((current * 4) + 7) >> 4);
    }

    /// <summary>
    /// Sample replication: output sample x takes input sample x * h / maxH, clamped to the row. Serves every ratio
    /// the triangle filter does not.
    /// </summary>
    internal static void Replicate(ReadOnlySpan<byte> row, int h, int maxH, Span<byte> output)
    {
        int lastIndex = row.Length - 1;
        for (int x = 0; x < output.Length; x++)
        {
            output[x] = row[Math.Min(x * h / maxH, lastIndex)];
        }
    }

    /// <summary>The 16-bit sample counterpart of <see cref="Replicate(ReadOnlySpan{byte}, int, int, Span{byte})"/>.</summary>
    internal static void Replicate(ReadOnlySpan<ushort> row, int h, int maxH, Span<ushort> output)
    {
        int lastIndex = row.Length - 1;
        for (int x = 0; x < output.Length; x++)
        {
            output[x] = row[Math.Min(x * h / maxH, lastIndex)];
        }
    }

    /// <summary>
    /// Vector interior of <see cref="UpsampleH2V1(ReadOnlySpan{byte}, Span{byte})"/>: handles inputs 1, 2, ... in
    /// blocks of sixteen while the block's right neighbour exists, and returns the first input not yet handled.
    /// </summary>
    private static int UpsampleH2V1Vector(ReadOnlySpan<byte> input, Span<byte> output, int last)
    {
        ref byte source = ref MemoryMarshal.GetReference(input);
        ref byte destination = ref MemoryMarshal.GetReference(output);
        Vector128<ushort> one = Vector128.Create((ushort)1);
        Vector128<ushort> two = Vector128.Create((ushort)2);
        int i = 1;

        // The loads reach input[i + 16] at most, the stores output[2i + 31]: both inside the spans.
        for (; i + 16 <= last; i += 16)
        {
            (Vector128<ushort> previousLow, Vector128<ushort> previousHigh) = Vector128.Widen(Vector128.LoadUnsafe(ref source, (nuint)(i - 1)));
            (Vector128<ushort> currentLow, Vector128<ushort> currentHigh) = Vector128.Widen(Vector128.LoadUnsafe(ref source, (nuint)i));
            (Vector128<ushort> nextLow, Vector128<ushort> nextHigh) = Vector128.Widen(Vector128.LoadUnsafe(ref source, (nuint)(i + 1)));

            Vector128<ushort> nearerLow = currentLow + currentLow + currentLow;
            Vector128<ushort> nearerHigh = currentHigh + currentHigh + currentHigh;
            Vector128<ushort> evenLow = (nearerLow + previousLow + one) >> 2;
            Vector128<ushort> evenHigh = (nearerHigh + previousHigh + one) >> 2;
            Vector128<ushort> oddLow = (nearerLow + nextLow + two) >> 2;
            Vector128<ushort> oddHigh = (nearerHigh + nextHigh + two) >> 2;

            ref ushort pairs = ref Unsafe.As<byte, ushort>(ref Unsafe.Add(ref destination, 2 * i));
            (evenLow | (oddLow << 8)).StoreUnsafe(ref pairs);
            (evenHigh | (oddHigh << 8)).StoreUnsafe(ref pairs, 8);
        }

        return i;
    }

    /// <summary>
    /// Vector interior of <see cref="UpsampleH2V2(ReadOnlySpan{byte}, ReadOnlySpan{byte}, Span{byte})"/>; the column
    /// sums are at most 1020, so the blended sums (at most 4087) fit 16-bit lanes.
    /// </summary>
    private static int UpsampleH2V2Vector(ReadOnlySpan<byte> nearRow, ReadOnlySpan<byte> farRow, Span<byte> output, int last)
    {
        ref byte near = ref MemoryMarshal.GetReference(nearRow);
        ref byte far = ref MemoryMarshal.GetReference(farRow);
        ref byte destination = ref MemoryMarshal.GetReference(output);
        Vector128<ushort> seven = Vector128.Create((ushort)7);
        Vector128<ushort> eight = Vector128.Create((ushort)8);
        int i = 1;
        for (; i + 16 <= last; i += 16)
        {
            (Vector128<ushort> previousLow, Vector128<ushort> previousHigh) = ColumnSums(ref near, ref far, i - 1);
            (Vector128<ushort> currentLow, Vector128<ushort> currentHigh) = ColumnSums(ref near, ref far, i);
            (Vector128<ushort> nextLow, Vector128<ushort> nextHigh) = ColumnSums(ref near, ref far, i + 1);

            Vector128<ushort> nearerLow = currentLow + currentLow + currentLow;
            Vector128<ushort> nearerHigh = currentHigh + currentHigh + currentHigh;
            Vector128<ushort> evenLow = (nearerLow + previousLow + eight) >> 4;
            Vector128<ushort> evenHigh = (nearerHigh + previousHigh + eight) >> 4;
            Vector128<ushort> oddLow = (nearerLow + nextLow + seven) >> 4;
            Vector128<ushort> oddHigh = (nearerHigh + nextHigh + seven) >> 4;

            ref ushort pairs = ref Unsafe.As<byte, ushort>(ref Unsafe.Add(ref destination, 2 * i));
            (evenLow | (oddLow << 8)).StoreUnsafe(ref pairs);
            (evenHigh | (oddHigh << 8)).StoreUnsafe(ref pairs, 8);
        }

        return i;

        static (Vector128<ushort> Low, Vector128<ushort> High) ColumnSums(ref byte near, ref byte far, int index)
        {
            (Vector128<ushort> nearLow, Vector128<ushort> nearHigh) = Vector128.Widen(Vector128.LoadUnsafe(ref near, (nuint)index));
            (Vector128<ushort> farLow, Vector128<ushort> farHigh) = Vector128.Widen(Vector128.LoadUnsafe(ref far, (nuint)index));
            return (nearLow + nearLow + nearLow + farLow, nearHigh + nearHigh + nearHigh + farHigh);
        }
    }
}
