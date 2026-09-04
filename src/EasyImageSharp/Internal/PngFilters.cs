using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace EasyImageSharp;

/// <summary>
/// The five PNG scanline filters (RFC 2083 section 6) in both directions.
/// <para>
/// Reconstruction of Sub, Average and Paeth is serial in the byte-per-pixel stride - byte <c>i</c> needs the
/// already-reconstructed byte <c>i - bpp</c> - so a full-width vector pass would read values it has not
/// produced yet. Those filters instead advance one whole pixel per step, folding all four bytes of a 32-bit
/// pixel at once with carry-free byte arithmetic. Up carries no such dependency and runs a full vector at a
/// time.
/// </para>
/// <para>
/// Filtering for the encoder reads only unmodified input rows, so there everything except Paeth vectorises
/// over the whole row.
/// </para>
/// </summary>
internal static class PngFilters
{
    /// <summary>Per-byte high bits of a 32-bit word, the carry positions of a byte-wise add.</summary>
    private const uint HighBits = 0x80808080u;

    /// <summary>Per-byte low seven bits of a 32-bit word.</summary>
    private const uint LowBits = 0x7F7F7F7Fu;

    /// <summary>
    /// Reconstructs <paramref name="row"/> in place from its filter type, with <paramref name="previous"/> the
    /// already-reconstructed row above (empty for the first scanline).
    /// </summary>
    public static void Unfilter(byte filterType, Span<byte> row, ReadOnlySpan<byte> previous, int bpp)
        => Unfilter(filterType, row, row, previous, bpp);

    /// <summary>
    /// Reconstructs the filtered scanline <paramref name="source"/> into <paramref name="destination"/>, with
    /// <paramref name="previous"/> the already-reconstructed row above (empty for the first scanline).
    /// <paramref name="destination"/> must be at least as long as <paramref name="source"/>; only its first
    /// <c>source.Length</c> bytes are written.
    /// <para>
    /// Aliasing contract. Every predictor reads <paramref name="source"/> at index <c>i</c> before writing
    /// <paramref name="destination"/> at index <c>i</c>, and otherwise reads only already-written destination
    /// bytes at <c>i - bpp</c> and bytes of the row above. So the two spans may be one and the same - which is
    /// exactly what the in-place overload passes - and more generally may alias a single backing buffer
    /// provided <paramref name="destination"/> does not start after <paramref name="source"/>: a write then
    /// only ever lands on source bytes that have already been consumed. <paramref name="previous"/> may sit in
    /// that buffer too, at any offset that does not overlap the written region of <paramref name="destination"/>.
    /// That is the shape an inflate window produces, where a decompressed row is unfiltered straight out of the
    /// window while the row above is still resident in it.
    /// </para>
    /// </summary>
    public static void Unfilter(byte filterType, ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> previous, int bpp)
    {
        switch (filterType)
        {
            case 0:
                CopyIfDistinct(source, destination[..source.Length]);
                break;
            case 1:
                UnfilterSub(source, destination, bpp);
                break;
            case 2:
                UnfilterUp(source, destination, previous);
                break;
            case 3:
                UnfilterAverage(source, destination, previous, bpp);
                break;
            case 4:
                UnfilterPaeth(source, destination, previous, bpp);
                break;
            default:
                throw new InvalidImageContentException($"Invalid PNG filter type: {filterType}.");
        }
    }

    /// <summary>Byte-wise <c>a + b</c> of four packed bytes, without carries crossing byte boundaries.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AddBytes(uint a, uint b) => ((a & LowBits) + (b & LowBits)) ^ ((a ^ b) & HighBits);

    /// <summary>Byte-wise <c>(a + b) &gt;&gt; 1</c> of four packed bytes (the PNG Average predictor).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AverageBytes(uint a, uint b) => (a & b) + (((a ^ b) >> 1) & LowBits);

    /// <summary>
    /// Copies <paramref name="source"/> over <paramref name="destination"/> unless the two already begin at the
    /// same address, which keeps the in-place overload free of a self-copy.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyIfDistinct(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (!Unsafe.AreSame(ref MemoryMarshal.GetReference(source), ref MemoryMarshal.GetReference(destination)))
        {
            source.CopyTo(destination);
        }
    }

    private static void UnfilterSub(ReadOnlySpan<byte> source, Span<byte> destination, int bpp)
    {
        int length = source.Length;
        ref byte input = ref MemoryMarshal.GetReference(source);
        ref byte data = ref MemoryMarshal.GetReference(destination);

        // The leading pixel has no left neighbour and carries across unchanged.
        CopyIfDistinct(source[..Math.Min(bpp, length)], destination);
        int i = bpp;

        if (bpp == 4 && !SimdConfig.ForceScalarFallback && BitConverter.IsLittleEndian)
        {
            for (; i + 4 <= length; i += 4)
            {
                uint value = AddBytes(
                    Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref input, (uint)i)),
                    Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref data, (uint)(i - 4))));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref data, (uint)i), value);
            }
        }

        for (; i < length; i++)
        {
            Unsafe.Add(ref data, (uint)i) =
                (byte)(Unsafe.Add(ref input, (uint)i) + Unsafe.Add(ref data, (uint)(i - bpp)));
        }
    }

    private static void UnfilterUp(ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> previous)
    {
        int length = source.Length;
        if (previous.IsEmpty)
        {
            CopyIfDistinct(source, destination[..length]);
            return;
        }

        ref byte input = ref MemoryMarshal.GetReference(source);
        ref byte data = ref MemoryMarshal.GetReference(destination);
        ref byte above = ref MemoryMarshal.GetReference(previous);
        int i = 0;

        if (SimdConfig.Vector256Enabled)
        {
            for (; i <= length - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                (Vector256.LoadUnsafe(ref input, (nuint)i) + Vector256.LoadUnsafe(ref above, (nuint)i))
                    .StoreUnsafe(ref data, (nuint)i);
            }
        }

        if (SimdConfig.Vector128Enabled)
        {
            for (; i <= length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                (Vector128.LoadUnsafe(ref input, (nuint)i) + Vector128.LoadUnsafe(ref above, (nuint)i))
                    .StoreUnsafe(ref data, (nuint)i);
            }
        }

        for (; i < length; i++)
        {
            Unsafe.Add(ref data, (uint)i) = (byte)(Unsafe.Add(ref input, (uint)i) + Unsafe.Add(ref above, (uint)i));
        }
    }

    private static void UnfilterAverage(ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> previous, int bpp)
    {
        int length = source.Length;
        ref byte input = ref MemoryMarshal.GetReference(source);
        ref byte data = ref MemoryMarshal.GetReference(destination);
        int limit = Math.Min(bpp, length);

        if (previous.IsEmpty)
        {
            CopyIfDistinct(source[..limit], destination);
            for (int i = bpp; i < length; i++)
            {
                Unsafe.Add(ref data, (uint)i) =
                    (byte)(Unsafe.Add(ref input, (uint)i) + (Unsafe.Add(ref data, (uint)(i - bpp)) >> 1));
            }

            return;
        }

        ref byte above = ref MemoryMarshal.GetReference(previous);
        for (int i = 0; i < limit; i++)
        {
            Unsafe.Add(ref data, (uint)i) = (byte)(Unsafe.Add(ref input, (uint)i) + (Unsafe.Add(ref above, (uint)i) >> 1));
        }

        int j = bpp;
        if (bpp == 4 && !SimdConfig.ForceScalarFallback && BitConverter.IsLittleEndian)
        {
            for (; j + 4 <= length; j += 4)
            {
                uint mean = AverageBytes(
                    Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref data, (uint)(j - 4))),
                    Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref above, (uint)j)));
                uint value = AddBytes(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref input, (uint)j)), mean);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref data, (uint)j), value);
            }
        }

        for (; j < length; j++)
        {
            Unsafe.Add(ref data, (uint)j) = (byte)(Unsafe.Add(ref input, (uint)j)
                + ((Unsafe.Add(ref data, (uint)(j - bpp)) + Unsafe.Add(ref above, (uint)j)) >> 1));
        }
    }

    private static void UnfilterPaeth(ReadOnlySpan<byte> source, Span<byte> destination, ReadOnlySpan<byte> previous, int bpp)
    {
        if (previous.IsEmpty)
        {
            // With no row above, the Paeth predictor degenerates to Sub.
            UnfilterSub(source, destination, bpp);
            return;
        }

        int length = source.Length;
        ref byte input = ref MemoryMarshal.GetReference(source);
        ref byte data = ref MemoryMarshal.GetReference(destination);
        ref byte above = ref MemoryMarshal.GetReference(previous);
        int limit = Math.Min(bpp, length);
        for (int i = 0; i < limit; i++)
        {
            Unsafe.Add(ref data, (uint)i) = (byte)(Unsafe.Add(ref input, (uint)i) + Unsafe.Add(ref above, (uint)i));
        }

        for (int i = bpp; i < length; i++)
        {
            Unsafe.Add(ref data, (uint)i) = (byte)(Unsafe.Add(ref input, (uint)i) + Paeth(
                Unsafe.Add(ref data, (uint)(i - bpp)),
                Unsafe.Add(ref above, (uint)i),
                Unsafe.Add(ref above, (uint)(i - bpp))));
        }
    }

    /// <summary>The Paeth predictor of RFC 2083 section 6.6.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // ----- Encoding -----

    /// <summary>Writes <paramref name="current"/> filtered by <paramref name="filterType"/> into <paramref name="destination"/>.</summary>
    public static void Filter(int filterType, ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous, int bpp, Span<byte> destination)
    {
        switch (filterType)
        {
            case 0:
                current.CopyTo(destination[..current.Length]);
                break;
            case 1:
                FilterSub(current, bpp, destination);
                break;
            case 2:
                FilterUp(current, previous, destination);
                break;
            case 3:
                FilterAverage(current, previous, bpp, destination);
                break;
            default:
                FilterPaeth(current, previous, bpp, destination);
                break;
        }
    }

    private static void FilterSub(ReadOnlySpan<byte> current, int bpp, Span<byte> destination)
    {
        int length = current.Length;
        int limit = Math.Min(bpp, length);
        current[..limit].CopyTo(destination);

        ref byte source = ref MemoryMarshal.GetReference(current);
        ref byte dest = ref MemoryMarshal.GetReference(destination);
        int i = bpp;
        if (SimdConfig.Vector128Enabled)
        {
            for (; i <= length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                (Vector128.LoadUnsafe(ref source, (nuint)i) - Vector128.LoadUnsafe(ref source, (nuint)(i - bpp)))
                    .StoreUnsafe(ref dest, (nuint)i);
            }
        }

        for (; i < length; i++)
        {
            Unsafe.Add(ref dest, (uint)i) = (byte)(Unsafe.Add(ref source, (uint)i) - Unsafe.Add(ref source, (uint)(i - bpp)));
        }
    }

    private static void FilterUp(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous, Span<byte> destination)
    {
        int length = current.Length;
        if (previous.IsEmpty)
        {
            current.CopyTo(destination[..length]);
            return;
        }

        ref byte source = ref MemoryMarshal.GetReference(current);
        ref byte above = ref MemoryMarshal.GetReference(previous);
        ref byte dest = ref MemoryMarshal.GetReference(destination);
        int i = 0;
        if (SimdConfig.Vector128Enabled)
        {
            for (; i <= length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                (Vector128.LoadUnsafe(ref source, (nuint)i) - Vector128.LoadUnsafe(ref above, (nuint)i))
                    .StoreUnsafe(ref dest, (nuint)i);
            }
        }

        for (; i < length; i++)
        {
            Unsafe.Add(ref dest, (uint)i) = (byte)(Unsafe.Add(ref source, (uint)i) - Unsafe.Add(ref above, (uint)i));
        }
    }

    private static void FilterAverage(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous, int bpp, Span<byte> destination)
    {
        int length = current.Length;
        int limit = Math.Min(bpp, length);
        for (int i = 0; i < limit; i++)
        {
            destination[i] = (byte)(current[i] - ((previous.IsEmpty ? 0 : previous[i]) >> 1));
        }

        ref byte source = ref MemoryMarshal.GetReference(current);
        ref byte dest = ref MemoryMarshal.GetReference(destination);
        int j = bpp;

        if (previous.IsEmpty)
        {
            for (; j < length; j++)
            {
                Unsafe.Add(ref dest, (uint)j) = (byte)(Unsafe.Add(ref source, (uint)j) - (Unsafe.Add(ref source, (uint)(j - bpp)) >> 1));
            }

            return;
        }

        ref byte above = ref MemoryMarshal.GetReference(previous);
        if (SimdConfig.Vector128Enabled)
        {
            Vector128<byte> low = Vector128.Create((byte)0x7F);
            for (; j <= length - Vector128<byte>.Count; j += Vector128<byte>.Count)
            {
                Vector128<byte> left = Vector128.LoadUnsafe(ref source, (nuint)(j - bpp));
                Vector128<byte> up = Vector128.LoadUnsafe(ref above, (nuint)j);

                // (left + up) >> 1 without widening: the shared bits plus half the differing ones.
                Vector128<byte> mean = (left & up)
                    + (Vector128.ShiftRightLogical((left ^ up).AsUInt16(), 1).AsByte() & low);
                (Vector128.LoadUnsafe(ref source, (nuint)j) - mean).StoreUnsafe(ref dest, (nuint)j);
            }
        }

        for (; j < length; j++)
        {
            Unsafe.Add(ref dest, (uint)j) =
                (byte)(Unsafe.Add(ref source, (uint)j) - ((Unsafe.Add(ref source, (uint)(j - bpp)) + Unsafe.Add(ref above, (uint)j)) >> 1));
        }
    }

    private static void FilterPaeth(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous, int bpp, Span<byte> destination)
    {
        int length = current.Length;
        int limit = Math.Min(bpp, length);
        for (int i = 0; i < limit; i++)
        {
            destination[i] = (byte)(current[i] - (previous.IsEmpty ? 0 : previous[i]));
        }

        ref byte source = ref MemoryMarshal.GetReference(current);
        ref byte dest = ref MemoryMarshal.GetReference(destination);
        if (previous.IsEmpty)
        {
            for (int i = bpp; i < length; i++)
            {
                Unsafe.Add(ref dest, (uint)i) = (byte)(Unsafe.Add(ref source, (uint)i) - Unsafe.Add(ref source, (uint)(i - bpp)));
            }

            return;
        }

        ref byte above = ref MemoryMarshal.GetReference(previous);
        for (int i = bpp; i < length; i++)
        {
            Unsafe.Add(ref dest, (uint)i) = (byte)(Unsafe.Add(ref source, (uint)i) - Paeth(
                Unsafe.Add(ref source, (uint)(i - bpp)),
                Unsafe.Add(ref above, (uint)i),
                Unsafe.Add(ref above, (uint)(i - bpp))));
        }
    }

    /// <summary>
    /// Sum of the absolute values of a filtered row read as signed bytes - the heuristic PNG encoders use to
    /// pick a filter. <c>|(sbyte)v|</c> is <c>min(v, -v)</c> in unsigned byte arithmetic, which vectorises.
    /// </summary>
    public static long AbsoluteSum(ReadOnlySpan<byte> data)
    {
        long total = 0;
        int i = 0;
        int length = data.Length;

        if (SimdConfig.Vector128Enabled && length >= Vector128<byte>.Count)
        {
            ref byte source = ref MemoryMarshal.GetReference(data);
            Vector128<ushort> low = Vector128<ushort>.Zero;
            Vector128<ushort> high = Vector128<ushort>.Zero;
            int sinceFlush = 0;
            for (; i <= length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                Vector128<byte> value = Vector128.LoadUnsafe(ref source, (nuint)i);
                (Vector128<ushort> a, Vector128<ushort> b) = Vector128.Widen(Vector128.Min(value, Vector128<byte>.Zero - value));
                low += a;
                high += b;

                // Lanes gain at most 128 per step, so flush well before sixteen bits overflow.
                if (++sinceFlush == 256)
                {
                    total += LaneSum(low) + LaneSum(high);
                    low = Vector128<ushort>.Zero;
                    high = Vector128<ushort>.Zero;
                    sinceFlush = 0;
                }
            }

            total += LaneSum(low) + LaneSum(high);
        }

        for (; i < length; i++)
        {
            total += Math.Abs((int)(sbyte)data[i]);
        }

        return total;

        // Vector128.Sum would return a ushort and overflow, so the lanes are widened one at a time.
        static long LaneSum(Vector128<ushort> value)
        {
            long sum = 0;
            for (int lane = 0; lane < Vector128<ushort>.Count; lane++)
            {
                sum += value[lane];
            }

            return sum;
        }
    }
}
