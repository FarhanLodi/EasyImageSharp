using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace EasyImageSharp;

/// <summary>
/// ADLER-32 (RFC 1950 section 8.2), the checksum carried in a zlib stream trailer: <c>s1</c> is the sum of the
/// bytes and <c>s2</c> the sum of the running <c>s1</c>, both modulo 65521, packed as <c>(s2 &lt;&lt; 16) | s1</c>.
/// <para>
/// The reduction is taken per block rather than per byte: 5552 is the largest byte count for which the
/// unreduced 32-bit sums provably cannot overflow, so a block costs two divisions instead of two per byte.
/// Where the reductions fall does not change the answer - the checksum is a function of the byte sequence
/// alone - it only bounds the width of the intermediates.
/// </para>
/// <para>
/// The vector path folds a block with the algebraic identity <c>s2 += k * s1 + sum (k - i) * x[i]</c>: a byte
/// contributes its distance from the end of the block. Splitting the index of a byte into its 16-byte chunk
/// and its position inside that chunk splits that weight into <c>16 * (chunks before it)</c> plus a fixed
/// descending 16..1 weight, so the inner loop only widens and accumulates per-lane sums and the multiplies
/// happen once per 256 bytes. The result is the exact integer the scalar loop produces, not merely a congruent
/// one, because no accumulator is ever allowed to wrap.
/// </para>
/// </summary>
internal static class Adler32
{
    /// <summary>The modulus, the largest prime below 65536.</summary>
    private const uint Base = 65521;

    /// <summary>Largest block length for which the unreduced 32-bit sums cannot overflow.</summary>
    private const int Nmax = 5552;

    /// <summary>Bytes folded by one vector iteration.</summary>
    private const int ChunkBytes = 16;

    /// <summary>Whole chunks in a block; <c>347 * 16</c> is exactly <see cref="Nmax"/>.</summary>
    private const int NmaxChunks = Nmax / ChunkBytes;

    /// <summary>
    /// Chunks folded into one pair of 16-bit lane accumulators. Sixteen keeps every accumulator inside a
    /// <see cref="ushort"/>: a plain lane sum reaches <c>16 * 255 = 4080</c>, that sum times its largest
    /// descending weight reaches <c>4080 * 16 = 65280</c>, and the chunk-prefix accumulator - which adds the
    /// running lane sum once per chunk - reaches <c>255 * (0 + 1 + ... + 15) = 30600</c>.
    /// </summary>
    private const int GroupChunks = 16;

    /// <summary>Descending weights 16..9 for the low eight bytes of a chunk.</summary>
    private static readonly Vector128<ushort> WeightsLower = Vector128.Create((ushort)16, 15, 14, 13, 12, 11, 10, 9);

    /// <summary>Descending weights 8..1 for the high eight bytes of a chunk.</summary>
    private static readonly Vector128<ushort> WeightsUpper = Vector128.Create((ushort)8, 7, 6, 5, 4, 3, 2, 1);

    /// <summary>Updates a running ADLER-32 with the given data. Seed with 1 and use the final value directly.</summary>
    public static uint Append(uint adler, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return adler;
        }

        uint s1 = adler & 0xFFFF;
        uint s2 = adler >> 16;
        ref byte source = ref MemoryMarshal.GetReference(data);
        int i = 0;

        // Byte lanes load in memory order, so the vector path does not itself depend on the layout; the guard
        // keeps the scalar loop normative on any runtime that is not the little-endian one it is tuned for.
        if (SimdConfig.Vector128Enabled && BitConverter.IsLittleEndian && data.Length >= ChunkBytes)
        {
            i = AppendVector128(ref source, data.Length, ref s1, ref s2);
        }

        // The reference implementation, and the whole of it whenever the vector path is unavailable: sum in
        // blocks of at most Nmax bytes, reducing only once per block.
        while (i < data.Length)
        {
            int end = i + Math.Min(Nmax, data.Length - i);
            for (; i <= end - 16; i += 16)
            {
                s1 += Unsafe.Add(ref source, (uint)i);
                s2 += s1;
                s1 += Unsafe.Add(ref source, (uint)(i + 1));
                s2 += s1;
                s1 += Unsafe.Add(ref source, (uint)(i + 2));
                s2 += s1;
                s1 += Unsafe.Add(ref source, (uint)(i + 3));
                s2 += s1;
                s1 += Unsafe.Add(ref source, (uint)(i + 4));
                s2 += s1;
                s1 += Unsafe.Add(ref source, (uint)(i + 5));
                s2 += s1;
                s1 += Unsafe.Add(ref source, (uint)(i + 6));
                s2 += s1;
                s1 += Unsafe.Add(ref source, (uint)(i + 7));
                s2 += s1;
                s1 += Unsafe.Add(ref source, (uint)(i + 8));
                s2 += s1;
                s1 += Unsafe.Add(ref source, (uint)(i + 9));
                s2 += s1;
                s1 += Unsafe.Add(ref source, (uint)(i + 10));
                s2 += s1;
                s1 += Unsafe.Add(ref source, (uint)(i + 11));
                s2 += s1;
                s1 += Unsafe.Add(ref source, (uint)(i + 12));
                s2 += s1;
                s1 += Unsafe.Add(ref source, (uint)(i + 13));
                s2 += s1;
                s1 += Unsafe.Add(ref source, (uint)(i + 14));
                s2 += s1;
                s1 += Unsafe.Add(ref source, (uint)(i + 15));
                s2 += s1;
            }

            for (; i < end; i++)
            {
                s1 += Unsafe.Add(ref source, (uint)i);
                s2 += s1;
            }

            s1 %= Base;
            s2 %= Base;
        }

        return (s2 << 16) | s1;
    }

    /// <summary>Computes the ADLER-32 of <paramref name="data"/> from the RFC 1950 seed of 1.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Append(1, data);

    /// <summary>
    /// Folds every whole 16-byte chunk of the <paramref name="length"/> bytes at <paramref name="source"/> into
    /// <paramref name="s1"/> and <paramref name="s2"/>, and returns the index of the first byte it did not
    /// consume - always a multiple of 16.
    /// <para>
    /// One block is at most <see cref="NmaxChunks"/> chunks, which is exactly <see cref="Nmax"/> bytes, and the
    /// two sums are reduced at its end. Inside a block <c>blockBytes</c> stays below <c>5552 * 255</c>,
    /// <c>weighted</c> below <c>5552 * 255 * 16</c> and <c>prefixSum</c> below <c>4080 * 347 * 346 / 2</c>, all
    /// comfortably inside a <see cref="uint"/>; only the final combine, where <c>prefixSum</c> is scaled by 16,
    /// needs 64 bits.
    /// </para>
    /// </summary>
    /// <param name="source">First byte of the buffer.</param>
    /// <param name="length">Bytes available at <paramref name="source"/>.</param>
    /// <param name="s1">Running byte sum, already reduced modulo 65521 on return.</param>
    /// <param name="s2">Running sum of <paramref name="s1"/>, already reduced modulo 65521 on return.</param>
    private static int AppendVector128(ref byte source, int length, ref uint s1, ref uint s2)
    {
        int i = 0;
        while (length - i >= ChunkBytes)
        {
            int chunks = Math.Min((length - i) / ChunkBytes, NmaxChunks);
            uint blockBytes = 0;
            uint prefixSum = 0;
            uint weighted = 0;

            for (int chunk = 0; chunk < chunks;)
            {
                int groupChunks = Math.Min(chunks - chunk, GroupChunks);
                Vector128<ushort> sumLower = Vector128<ushort>.Zero;
                Vector128<ushort> sumUpper = Vector128<ushort>.Zero;
                Vector128<ushort> prefixLower = Vector128<ushort>.Zero;
                Vector128<ushort> prefixUpper = Vector128<ushort>.Zero;

                for (int g = 0; g < groupChunks; g++)
                {
                    (Vector128<ushort> lower, Vector128<ushort> upper) =
                        Vector128.Widen(Vector128.LoadUnsafe(ref source, (nuint)(i + ((chunk + g) * ChunkBytes))));

                    // Accumulated before the chunk itself is added, so each lane ends up holding the sum over
                    // the group of what that lane contributed to all earlier chunks - the per-lane form of the
                    // "16 * (chunks before it)" half of the weight.
                    prefixLower += sumLower;
                    prefixUpper += sumUpper;
                    sumLower += lower;
                    sumUpper += upper;
                }

                prefixSum += ((uint)groupChunks * blockBytes) + HorizontalSum(prefixLower) + HorizontalSum(prefixUpper);
                weighted += HorizontalSum(sumLower * WeightsLower) + HorizontalSum(sumUpper * WeightsUpper);
                blockBytes += HorizontalSum(sumLower) + HorizontalSum(sumUpper);
                chunk += groupChunks;
            }

            ulong high = s2 + ((ulong)chunks * ChunkBytes * s1) + (ChunkBytes * (ulong)prefixSum) + weighted;
            s1 = (s1 + blockBytes) % Base;
            s2 = (uint)(high % Base);
            i += chunks * ChunkBytes;
        }

        return i;
    }

    /// <summary>Sums the eight 16-bit lanes, widening first so a total above 65535 cannot wrap.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HorizontalSum(Vector128<ushort> value)
    {
        (Vector128<uint> lower, Vector128<uint> upper) = Vector128.Widen(value);
        return Vector128.Sum(lower + upper);
    }
}
