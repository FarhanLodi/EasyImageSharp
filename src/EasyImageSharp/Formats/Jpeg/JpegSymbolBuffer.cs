using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// A compact, append-only record of the Huffman symbols of one sequential scan, kept so the scan can be written
/// after the fact: with tables derived from the very statistics gathered while recording, or after an earlier
/// scan has been finished. Used by <see cref="JpegScanlineSink"/>, which receives the image one row at a time and
/// so cannot revisit the pixels.
/// </summary>
/// <remarks>
/// <para>
/// Each block is stored as its DC size category followed by its AC run/size symbols (ZRL and EOB included), each
/// symbol followed by its extra magnitude bits in one byte (sizes up to 8) or two (sizes 9 to 11). Zero
/// coefficients cost nothing, so the record grows with the compressed size, not the image size: measured on the
/// benchmark corpus at 4:2:0 and quality 50 to 90 it is 2.3 to 3.7 times the size of the optimised JPEG it stands
/// for, which is 7 to 30 times smaller than the two bytes per coefficient a coefficient buffer needs (or than the
/// pixels).
/// </para>
/// <para>
/// The block order is the scan order, so replaying needs nothing but the scan's MCU structure; DC predictions and
/// restart-interval resets are already folded into the recorded differences. Storage is a list of pooled 64 KiB
/// chunks and no block straddles two of them.
/// </para>
/// </remarks>
internal sealed class JpegSymbolBuffer : IDisposable
{
    private const int ChunkSize = 1 << 16;

    /// <summary>Upper bound on one block's record: 64 symbols with two extra-bit bytes each, plus ZRLs and EOB.</summary>
    private const int MaxBlockBytes = 256;

    private readonly List<byte[]> chunks = new();
    private readonly List<int> chunkLengths = new();
    private byte[] current = Array.Empty<byte>();
    private int position;

    // Replay cursor.
    private int readChunk;
    private int readPosition;

    /// <summary>Total bytes recorded so far (for diagnostics and tests).</summary>
    public long Length { get; private set; }

    /// <summary>
    /// Records one block (64 quantised coefficients in zigzag order) exactly as the sequential block coder would
    /// code it, updating the DC prediction and, when given, the symbol frequencies of the block's tables.
    /// </summary>
    public void AppendBlock(ReadOnlySpan<short> block, ref int predictor, long[]? dcFrequencies, long[]? acFrequencies)
    {
        if (block.Length < 64)
        {
            throw new ArgumentException("A block holds 64 coefficients.", nameof(block));
        }

        if (this.position > this.current.Length - MaxBlockBytes)
        {
            this.StartChunk();
        }

        byte[] buffer = this.current;
        int pos = this.position;

        int dc = block[0];
        int diff = dc - predictor;
        predictor = dc;
        int nbits = SizeCategory(diff);
        buffer[pos++] = (byte)nbits;
        pos = PutBits(buffer, pos, diff, nbits);
        if (dcFrequencies is not null)
        {
            dcFrequencies[nbits]++;
        }

        int last = 63;
        while (last > 0 && block[last] == 0)
        {
            last--;
        }

        int run = 0;
        for (int k = 1; k <= last; k++)
        {
            int value = block[k];
            if (value == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                buffer[pos++] = 0xF0;
                if (acFrequencies is not null)
                {
                    acFrequencies[0xF0]++;
                }

                run -= 16;
            }

            nbits = SizeCategory(value);
            int symbol = (run << 4) | nbits;
            buffer[pos++] = (byte)symbol;
            pos = PutBits(buffer, pos, value, nbits);
            if (acFrequencies is not null)
            {
                acFrequencies[symbol]++;
            }

            run = 0;
        }

        if (last < 63)
        {
            buffer[pos++] = 0x00;
            if (acFrequencies is not null)
            {
                acFrequencies[0x00]++;
            }
        }

        this.Length += pos - this.position;
        this.position = pos;
    }

    /// <summary>Rewinds the replay cursor to the first recorded block.</summary>
    public void Rewind()
    {
        this.CloseChunk();
        this.readChunk = 0;
        this.readPosition = 0;
    }

    /// <summary>
    /// Writes the next recorded block with the given Huffman lookups (<c>(code &lt;&lt; 8) | length</c> entries, as in
    /// <see cref="JpegHuffmanTable.Lookup"/>). Produces exactly the bits the sequential block coder produces for
    /// the original coefficients.
    /// </summary>
    public void ReplayBlock(JpegBitWriter writer, int[] dcLookup, int[] acLookup)
    {
        if (this.readPosition >= this.chunkLengths[this.readChunk])
        {
            this.readChunk++;
            this.readPosition = 0;
        }

        byte[] buffer = this.chunks[this.readChunk];
        int pos = this.readPosition;

        int nbits = buffer[pos++];
        uint bits = GetBits(buffer, ref pos, nbits);
        int entry = dcLookup[nbits];
        writer.WriteCodeAndBits((uint)entry >> 8, entry & 0xFF, bits, nbits);

        int k = 1;
        while (k <= 63)
        {
            int symbol = buffer[pos++];
            entry = acLookup[symbol];
            if (symbol == 0x00)
            {
                writer.WriteBits((uint)entry >> 8, entry & 0xFF);
                break;
            }

            if (symbol == 0xF0)
            {
                writer.WriteBits((uint)entry >> 8, entry & 0xFF);
                k += 16;
                continue;
            }

            nbits = symbol & 15;
            bits = GetBits(buffer, ref pos, nbits);
            writer.WriteCodeAndBits((uint)entry >> 8, entry & 0xFF, bits, nbits);
            k += (symbol >> 4) + 1;
        }

        this.readPosition = pos;
    }

    public void Dispose()
    {
        foreach (byte[] chunk in this.chunks)
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        this.chunks.Clear();
        this.chunkLengths.Clear();
        this.current = Array.Empty<byte>();
        this.position = 0;
    }

    /// <summary>The JPEG size category of a coefficient or difference: the bit length of its magnitude.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SizeCategory(int value) => 32 - BitOperations.LeadingZeroCount((uint)Math.Abs(value));

    /// <summary>Stores the <paramref name="nbits"/> extra bits of <paramref name="value"/> (one's complement when negative).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PutBits(byte[] buffer, int pos, int value, int nbits)
    {
        if (nbits == 0)
        {
            return pos;
        }

        int bits = (value < 0 ? value - 1 : value) & ((1 << nbits) - 1);
        buffer[pos++] = (byte)bits;
        if (nbits > 8)
        {
            buffer[pos++] = (byte)(bits >> 8);
        }

        return pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint GetBits(byte[] buffer, ref int pos, int nbits)
    {
        if (nbits == 0)
        {
            return 0;
        }

        uint bits = buffer[pos++];
        if (nbits > 8)
        {
            bits |= (uint)buffer[pos++] << 8;
        }

        return bits;
    }

    private void StartChunk()
    {
        this.CloseChunk();
        this.current = ArrayPool<byte>.Shared.Rent(ChunkSize);
        this.chunks.Add(this.current);
        this.chunkLengths.Add(0);
        this.position = 0;
    }

    /// <summary>Records how much of the chunk being written is in use.</summary>
    private void CloseChunk()
    {
        if (this.chunks.Count > 0)
        {
            this.chunkLengths[^1] = this.position;
        }
    }
}
