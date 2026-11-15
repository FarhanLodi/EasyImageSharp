using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace EasyImageSharp.Processing;

/// <summary>
/// Vector kernels for the grey-level morphology of <see cref="MorphologyOps"/>. Minimum and maximum are exact, so
/// any evaluation order that covers the same in-bounds neighbourhood gives the same bytes; these kernels only
/// choose orders that vectorise:
/// <list type="bullet">
/// <item>Rows: the identity-padded row is reduced by doubling (windows of 2, 4, 8 ... pixels, each one vector
/// operation per pixel group), then two overlapping power-of-two windows cover the k-pixel window - about
/// log2(k) + 1 vector operations per 16 or 32 pixels.</item>
/// <item>Columns: whole image rows are the vector lanes, so no gather or transpose is needed. Small windows take
/// the running minimum/maximum of the k source rows directly; larger ones run van Herk / Gil-Werman down narrow
/// column strips, three vector operations per pixel group regardless of k.</item>
/// </list>
/// </summary>
internal static class MorphologySimd
{
    /// <summary>Column windows up to this height are reduced directly; taller ones use van Herk / Gil-Werman.</summary>
    private const int DirectColumnWindow = 9;

    /// <summary>Width in bytes of the column strips of the vertical van Herk pass (two 256-bit vectors).</summary>
    private const int StripWidth = 64;

    /// <summary>True when the kernels may run.</summary>
    public static bool IsEnabled => SimdConfig.Vector128Enabled;

    /// <summary>
    /// 1D minimum or maximum of one row over the window <c>[x - left, x + right]</c>, ignoring positions outside the
    /// row. <paramref name="scratch"/> must hold at least <c>src.Length + left + right</c> bytes.
    /// </summary>
    public static void FilterRow(ReadOnlySpan<byte> src, Span<byte> dst, int left, int right, bool isMin, Span<byte> scratch)
    {
        int n = src.Length;
        int k = left + right + 1;
        if (k == 1)
        {
            src.CopyTo(dst);
            return;
        }

        // f: the row shifted right by `left`, padded with the identity so the padding never wins.
        byte identity = isMin ? byte.MaxValue : byte.MinValue;
        int length = n + k - 1;
        Span<byte> g = scratch[..length];
        g[..left].Fill(identity);
        src.CopyTo(g[left..]);
        g[(left + n)..].Fill(identity);

        // After the pass with step w, g[i] is the op over f[i .. i + 2w - 1]. Each pass reads g[i + w] before the
        // pass overwrites it (it only ever writes at or below the position it is reading from), so it runs in place.
        int window = 1;
        while (window * 2 <= k)
        {
            Combine(g[..(length - window)], g[window..], g, isMin);
            window *= 2;
        }

        // window is now the largest power of two not above k; two such windows starting k - window apart cover k.
        Combine(g[..n], g.Slice(k - window, n), dst, isMin);
    }

    /// <summary>
    /// 1D minimum or maximum of every column over the window <c>[y - up, y + down]</c>, ignoring rows outside the
    /// image; the result equals the scalar <see cref="MorphologyOps.FilterColumns(byte[], int, int, int, int, bool, bool)"/>.
    /// </summary>
    public static byte[] FilterColumns(byte[] src, int width, int height, int up, int down, bool isMin)
    {
        var dst = new byte[src.Length];
        int k = up + down + 1;
        if (k <= DirectColumnWindow)
        {
            ParallelRowIterator.IterateRows(width, height, (start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    int first = Math.Max(0, y - up);
                    int last = Math.Min(height - 1, y + down);
                    Span<byte> output = dst.AsSpan(y * width, width);
                    if (first == last)
                    {
                        src.AsSpan(first * width, width).CopyTo(output);
                        continue;
                    }

                    Combine(src.AsSpan(first * width, width), src.AsSpan((first + 1) * width, width), output, isMin);
                    for (int row = first + 2; row <= last; row++)
                    {
                        Combine(output, src.AsSpan(row * width, width), output, isMin);
                    }
                }
            });
            return dst;
        }

        // van Herk / Gil-Werman down each strip: in padded row coordinates i (source row i - up, identity outside
        // the image), g is the prefix op and h the suffix op within blocks of k rows, and output row y is
        // op(h[y], g[y + k - 1]).
        int padded = ((height + k - 1 + k - 1) / k) * k;
        int strips = (width + StripWidth - 1) / StripWidth;
        byte identity = isMin ? byte.MaxValue : byte.MinValue;
        ParallelRowIterator.IterateRows(StripWidth * height, strips, (startStrip, endStrip) =>
        {
            var g = new byte[padded * StripWidth];
            var h = new byte[padded * StripWidth];
            var identityRow = new byte[StripWidth];
            identityRow.AsSpan().Fill(identity);
            for (int strip = startStrip; strip < endStrip; strip++)
            {
                int x0 = strip * StripWidth;
                int w = Math.Min(StripWidth, width - x0);
                for (int b = 0; b < padded; b += k)
                {
                    // Prefix op downwards within the block.
                    Row(src, identityRow, b - up, height, width, x0, w).CopyTo(g.AsSpan(b * StripWidth, w));
                    for (int i = b + 1; i < b + k; i++)
                    {
                        Combine(g.AsSpan((i - 1) * StripWidth, w), Row(src, identityRow, i - up, height, width, x0, w), g.AsSpan(i * StripWidth, w), isMin);
                    }

                    // Suffix op upwards within the block. Only rows below the image's last output row are ever read.
                    int lastRow = b + k - 1;
                    if (b >= height)
                    {
                        continue;
                    }

                    Row(src, identityRow, lastRow - up, height, width, x0, w).CopyTo(h.AsSpan(lastRow * StripWidth, w));
                    for (int i = lastRow - 1; i >= b; i--)
                    {
                        Combine(h.AsSpan((i + 1) * StripWidth, w), Row(src, identityRow, i - up, height, width, x0, w), h.AsSpan(i * StripWidth, w), isMin);
                    }
                }

                for (int y = 0; y < height; y++)
                {
                    Combine(h.AsSpan(y * StripWidth, w), g.AsSpan((y + k - 1) * StripWidth, w), dst.AsSpan((y * width) + x0, w), isMin);
                }
            }
        });
        return dst;
    }

    /// <summary><c>destination[i] = op(a[i], b[i])</c>; <paramref name="destination"/> may be either input (or start at or before it).</summary>
    public static void Combine(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> destination, bool isMin)
    {
        int length = a.Length;
        ref byte first = ref MemoryMarshal.GetReference(a);
        ref byte second = ref MemoryMarshal.GetReference(b[..length]);
        ref byte output = ref MemoryMarshal.GetReference(destination[..length]);
        int i = 0;
        if (SimdConfig.Vector256Enabled)
        {
            for (; i <= length - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                Vector256<byte> x = Vector256.LoadUnsafe(ref first, (nuint)i);
                Vector256<byte> y = Vector256.LoadUnsafe(ref second, (nuint)i);
                (isMin ? Vector256.Min(x, y) : Vector256.Max(x, y)).StoreUnsafe(ref output, (nuint)i);
            }
        }

        if (SimdConfig.Vector128Enabled)
        {
            for (; i <= length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                Vector128<byte> x = Vector128.LoadUnsafe(ref first, (nuint)i);
                Vector128<byte> y = Vector128.LoadUnsafe(ref second, (nuint)i);
                (isMin ? Vector128.Min(x, y) : Vector128.Max(x, y)).StoreUnsafe(ref output, (nuint)i);
            }
        }

        for (; i < length; i++)
        {
            byte x = Unsafe.Add(ref first, i);
            byte y = Unsafe.Add(ref second, i);
            Unsafe.Add(ref output, i) = isMin ? (x < y ? x : y) : (x > y ? x : y);
        }
    }

    /// <summary><c>destination[i] = (byte)(a[i] - b[i])</c>, the wrapping difference the top-hat operators use.</summary>
    public static void Subtract(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> destination)
    {
        int length = a.Length;
        ref byte first = ref MemoryMarshal.GetReference(a);
        ref byte second = ref MemoryMarshal.GetReference(b[..length]);
        ref byte output = ref MemoryMarshal.GetReference(destination[..length]);
        int i = 0;
        if (SimdConfig.Vector256Enabled)
        {
            for (; i <= length - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                (Vector256.LoadUnsafe(ref first, (nuint)i) - Vector256.LoadUnsafe(ref second, (nuint)i)).StoreUnsafe(ref output, (nuint)i);
            }
        }

        if (SimdConfig.Vector128Enabled)
        {
            for (; i <= length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                (Vector128.LoadUnsafe(ref first, (nuint)i) - Vector128.LoadUnsafe(ref second, (nuint)i)).StoreUnsafe(ref output, (nuint)i);
            }
        }

        for (; i < length; i++)
        {
            Unsafe.Add(ref output, i) = (byte)(Unsafe.Add(ref first, i) - Unsafe.Add(ref second, i));
        }
    }

    /// <summary>The strip's part of padded row <paramref name="row"/>: a source row, or the identity outside the image.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> Row(byte[] src, byte[] identityRow, int row, int height, int width, int x0, int w)
        => (uint)row < (uint)height ? src.AsSpan((row * width) + x0, w) : identityRow.AsSpan(0, w);
}
