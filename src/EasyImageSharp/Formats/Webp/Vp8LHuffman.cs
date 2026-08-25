namespace EasyImageSharp.Formats.Webp;

/// <summary>One entry of a canonical Huffman lookup table: the code length (or the total length of a two-level lookup) and the symbol (or the offset of the second-level table).</summary>
internal readonly struct HuffmanCode
{
    public readonly byte Bits;
    public readonly ushort Value;

    public HuffmanCode(int bits, int value)
    {
        this.Bits = (byte)bits;
        this.Value = (ushort)value;
    }
}

/// <summary>
/// A canonical prefix code laid out as a two-level lookup table indexed by the next bits of an LSB-first
/// bit reader (the codes are therefore stored bit-reversed). Built with the classic zlib/brotli style
/// algorithm also used by the reference VP8L decoder; incomplete or over-subscribed codes are rejected,
/// with the single-symbol code (zero bits per symbol) as the only allowed degenerate case.
/// </summary>
internal sealed class HuffmanTree
{
    /// <summary>Longest code length allowed by the VP8L format.</summary>
    public const int MaxCodeLength = 15;

    private const int MaxRootBits = 8;

    private readonly HuffmanCode[] table;
    private readonly int rootBits;
    private readonly uint rootMask;

    private HuffmanTree(HuffmanCode[] table, int rootBits)
    {
        this.table = table;
        this.rootBits = rootBits;
        this.rootMask = (1u << rootBits) - 1;
    }

    /// <summary>True when the code has a single symbol and therefore consumes no bits.</summary>
    public bool IsSingleSymbol => this.table.Length == 1;

    /// <summary>The only symbol of a single-symbol code.</summary>
    public int SingleSymbol => this.table[0].Value;

    /// <summary>Builds the lookup table for the given code lengths; returns null when the lengths do not form a valid complete code.</summary>
    public static HuffmanTree? Build(ReadOnlySpan<int> codeLengths)
    {
        int maxLength = 0;
        foreach (int length in codeLengths)
        {
            if (length > MaxCodeLength)
            {
                return null;
            }

            maxLength = Math.Max(maxLength, length);
        }

        if (maxLength == 0)
        {
            return null; // No symbols at all.
        }

        int rootBits = Math.Min(MaxRootBits, maxLength);
        int size = BuildTable(null, rootBits, codeLengths, null);
        if (size <= 0)
        {
            return null;
        }

        var sorted = new ushort[codeLengths.Length];
        var table = new HuffmanCode[size];
        int built = BuildTable(table, rootBits, codeLengths, sorted);
        if (built != size)
        {
            return null;
        }

        return new HuffmanTree(table, size == 1 ? 0 : rootBits);
    }

    /// <summary>Decodes one symbol from the reader.</summary>
    public int ReadSymbol(Vp8LBitReader reader)
    {
        uint bits = reader.Peek(MaxCodeLength);
        int index = (int)(bits & this.rootMask);
        HuffmanCode code = this.table[index];
        if (code.Bits > this.rootBits)
        {
            int subBits = code.Bits - this.rootBits;
            index += code.Value + (int)((bits >> this.rootBits) & ((1u << subBits) - 1));
            code = this.table[index];
            reader.Skip(this.rootBits + code.Bits);
        }
        else
        {
            reader.Skip(code.Bits);
        }

        return code.Value;
    }

    /// <summary>
    /// Fills <paramref name="table"/> (when non-null) and returns the total table size, or 0 when the code lengths
    /// describe an invalid (incomplete or over-subscribed) code.
    /// </summary>
    private static int BuildTable(HuffmanCode[]? table, int rootBits, ReadOnlySpan<int> codeLengths, ushort[]? sorted)
    {
        Span<int> count = stackalloc int[MaxCodeLength + 1];
        Span<int> offset = stackalloc int[MaxCodeLength + 1];
        count.Clear();

        foreach (int length in codeLengths)
        {
            count[length]++;
        }

        if (count[0] == codeLengths.Length)
        {
            return 0;
        }

        offset[1] = 0;
        for (int len = 1; len < MaxCodeLength; len++)
        {
            if (count[len] > (1 << len))
            {
                return 0;
            }

            offset[len + 1] = offset[len] + count[len];
        }

        for (int symbol = 0; symbol < codeLengths.Length; symbol++)
        {
            int length = codeLengths[symbol];
            if (length > 0)
            {
                if (sorted is not null)
                {
                    sorted[offset[length]] = (ushort)symbol;
                }

                offset[length]++;
            }
        }

        int totalSymbols = offset[MaxCodeLength];
        if (totalSymbols == 1)
        {
            // A single symbol is coded with zero bits.
            if (table is not null)
            {
                table[0] = new HuffmanCode(0, sorted![0]);
            }

            return 1;
        }

        int tableBase = 0;
        int tableBits = rootBits;
        int tableSize = 1 << tableBits;
        int totalSize = tableSize;
        uint mask = (uint)totalSize - 1;
        uint low = uint.MaxValue;
        uint key = 0;
        int numNodes = 1;
        int numOpen = 1;
        int next = 0;

        int step = 2;
        for (int len = 1; len <= rootBits; len++, step <<= 1)
        {
            numOpen <<= 1;
            numNodes += numOpen;
            numOpen -= count[len];
            if (numOpen < 0)
            {
                return 0;
            }

            for (; count[len] > 0; count[len]--)
            {
                if (table is not null)
                {
                    Replicate(table, tableBase + (int)key, step, tableSize, new HuffmanCode(len, sorted![next++]));
                }

                key = NextKey(key, len);
            }
        }

        step = 2;
        for (int len = rootBits + 1; len <= MaxCodeLength; len++, step <<= 1)
        {
            numOpen <<= 1;
            numNodes += numOpen;
            numOpen -= count[len];
            if (numOpen < 0)
            {
                return 0;
            }

            for (; count[len] > 0; count[len]--)
            {
                if ((key & mask) != low)
                {
                    tableBase += tableSize;
                    tableBits = NextTableBits(count, len, rootBits);
                    tableSize = 1 << tableBits;
                    totalSize += tableSize;
                    low = key & mask;
                    if (table is not null)
                    {
                        table[low] = new HuffmanCode(tableBits + rootBits, tableBase - (int)low);
                    }
                }

                if (table is not null)
                {
                    Replicate(table, tableBase + (int)(key >> rootBits), step, tableSize, new HuffmanCode(len - rootBits, sorted![next++]));
                }

                key = NextKey(key, len);
            }
        }

        return numNodes == (2 * totalSymbols) - 1 ? totalSize : 0;
    }

    private static void Replicate(HuffmanCode[] table, int start, int step, int end, HuffmanCode code)
    {
        do
        {
            end -= step;
            table[start + end] = code;
        }
        while (end > 0);
    }

    /// <summary>Returns reverse(reverse(key, len) + 1, len): the next bit-reversed canonical code.</summary>
    private static uint NextKey(uint key, int len)
    {
        uint step = 1u << (len - 1);
        while ((key & step) != 0)
        {
            step >>= 1;
        }

        return step != 0 ? (key & (step - 1)) + step : key;
    }

    private static int NextTableBits(ReadOnlySpan<int> count, int len, int rootBits)
    {
        int left = 1 << (len - rootBits);
        while (len < MaxCodeLength)
        {
            left -= count[len];
            if (left <= 0)
            {
                break;
            }

            len++;
            left <<= 1;
        }

        return len - rootBits;
    }
}

/// <summary>The five prefix codes (green/length/cache, red, blue, alpha, distance) shared by a group of pixels.</summary>
internal sealed class HuffmanGroup
{
    public HuffmanTree Green = null!;
    public HuffmanTree Red = null!;
    public HuffmanTree Blue = null!;
    public HuffmanTree Alpha = null!;
    public HuffmanTree Distance = null!;

    /// <summary>True when red, blue and alpha are all single-symbol codes, so literals only need the green symbol.</summary>
    public bool TrivialLiteral;

    /// <summary>The ARGB literal (minus green) implied by <see cref="TrivialLiteral"/>.</summary>
    public uint LiteralArb;
}
