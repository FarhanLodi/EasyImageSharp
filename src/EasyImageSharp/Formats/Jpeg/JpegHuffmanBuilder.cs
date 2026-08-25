namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// A Huffman table in the form the encoder needs: the DHT specification (code counts per length plus the
/// symbols in code order) and the derived per-symbol code/length lookup (T.81 C.2).
/// </summary>
internal sealed class JpegHuffmanTable
{
    /// <summary>Number of codes of each length 1..16 (the BITS list of T.81 C.1).</summary>
    public readonly byte[] Bits;

    /// <summary>Symbols in order of increasing code length (the HUFFVAL list of T.81 C.1).</summary>
    public readonly byte[] Values;

    /// <summary>
    /// Per-symbol encoding entry: <c>(code &lt;&lt; 8) | length</c>, or 0 when the symbol has no code. Codes are at most
    /// 16 bits long, so an entry never exceeds 24 bits.
    /// </summary>
    public readonly int[] Lookup = new int[256];

    public JpegHuffmanTable(byte[] bits, byte[] values)
    {
        this.Bits = bits;
        this.Values = values;

        // Canonical code assignment (T.81 C.2, figure C.2): codes of one length are consecutive, and the first
        // code of the next length is the last code plus one, shifted left by one.
        int code = 0;
        int k = 0;
        for (int length = 1; length <= 16; length++)
        {
            int count = bits[length - 1];
            for (int i = 0; i < count; i++)
            {
                this.Lookup[values[k++]] = (code << 8) | length;
                code++;
            }

            code <<= 1;
        }
    }

    /// <summary>The DHT payload for this table with the given class/identifier byte.</summary>
    public byte[] ToSegmentPayload(byte classAndId)
    {
        var payload = new byte[1 + 16 + this.Values.Length];
        payload[0] = classAndId;
        this.Bits.CopyTo(payload, 1);
        this.Values.CopyTo(payload, 17);
        return payload;
    }

    /// <summary>
    /// Builds the code-length-optimal table for the observed symbol frequencies using the procedure of
    /// T.81 Annex K.2 (figures K.1-K.4): pair-merge the two least frequent symbols until one tree remains, count
    /// the code lengths, fold lengths beyond 16 bits back into the tree, and drop the reserved symbol that
    /// guarantees no real code consists of all 1-bits.
    /// </summary>
    /// <param name="frequencies">257 entries: 256 symbol counts plus a slot for the reserved symbol (overwritten).</param>
    public static JpegHuffmanTable FromFrequencies(long[] frequencies)
    {
        if (frequencies.Length != 257)
        {
            throw new ArgumentException("Expected 257 frequency slots.", nameof(frequencies));
        }

        var freq = (long[])frequencies.Clone();
        freq[256] = 1; // Reserved code point: it will receive the longest all-ones code and is then discarded.

        Span<int> codeSize = stackalloc int[257];
        Span<int> others = stackalloc int[257];
        codeSize.Clear();
        others.Fill(-1);

        while (true)
        {
            // Least frequent symbol (largest index on ties, so the reserved symbol tends to sink to the bottom).
            int c1 = -1;
            long v = long.MaxValue;
            for (int i = 0; i <= 256; i++)
            {
                if (freq[i] != 0 && freq[i] <= v)
                {
                    v = freq[i];
                    c1 = i;
                }
            }

            // Second least frequent symbol.
            int c2 = -1;
            v = long.MaxValue;
            for (int i = 0; i <= 256; i++)
            {
                if (freq[i] != 0 && freq[i] <= v && i != c1)
                {
                    v = freq[i];
                    c2 = i;
                }
            }

            if (c2 < 0)
            {
                break; // Only one tree left.
            }

            // Merge the two trees: c1 absorbs c2's frequency and every symbol in either tree gets one bit longer.
            freq[c1] += freq[c2];
            freq[c2] = 0;

            codeSize[c1]++;
            while (others[c1] >= 0)
            {
                c1 = others[c1];
                codeSize[c1]++;
            }

            others[c1] = c2; // Chain c2's tree onto c1's.

            codeSize[c2]++;
            while (others[c2] >= 0)
            {
                c2 = others[c2];
                codeSize[c2]++;
            }
        }

        // Count how many codes of each length there are (lengths can reach 32 with 257 symbols).
        Span<int> bitCounts = stackalloc int[33];
        bitCounts.Clear();
        for (int i = 0; i <= 256; i++)
        {
            int size = codeSize[i];
            if (size > 0)
            {
                if (size > 32)
                {
                    throw new InvalidOperationException("Huffman code length exceeds 32 bits.");
                }

                bitCounts[size]++;
            }
        }

        // JPEG limits code lengths to 16 bits (figure K.3): repeatedly take a pair of codes off the longest
        // length and move it up, lengthening one code of a shorter length in exchange; the prefix property is
        // preserved because a code of length j is replaced by two codes of length j + 1.
        for (int i = 32; i > 16; i--)
        {
            while (bitCounts[i] > 0)
            {
                int j = i - 2;
                while (bitCounts[j] == 0)
                {
                    j--;
                }

                bitCounts[i] -= 2;
                bitCounts[i - 1]++;
                bitCounts[j + 1] += 2;
                bitCounts[j]--;
            }
        }

        // Remove the reserved symbol from the longest remaining length (figure K.3, final step). A scan that
        // emitted no symbols at all leaves only the reserved one, which yields an empty (but still legal) table.
        int longest = 16;
        while (longest > 0 && bitCounts[longest] == 0)
        {
            longest--;
        }

        if (longest > 0)
        {
            bitCounts[longest]--;
        }

        var bits = new byte[16];
        int total = 0;
        for (int i = 1; i <= 16; i++)
        {
            bits[i - 1] = (byte)bitCounts[i];
            total += bitCounts[i];
        }

        // Symbols sorted by code length, ties by symbol value (figure K.4).
        var values = new byte[total];
        int p = 0;
        for (int size = 1; size <= 32; size++)
        {
            for (int i = 0; i < 256; i++)
            {
                if (codeSize[i] == size)
                {
                    values[p++] = (byte)i;
                }
            }
        }

        return new JpegHuffmanTable(bits, values);
    }
}
