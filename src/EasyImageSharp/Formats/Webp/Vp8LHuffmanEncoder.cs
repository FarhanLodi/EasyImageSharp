namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// One prefix code of a VP8L prefix code group: canonical code lengths, the matching bit-reversed codes (the
/// bitstream is read least-significant-bit first, so a canonical code is emitted reversed) and the ability to
/// write its own description into the bitstream.
/// </summary>
internal sealed class Vp8LPrefixCode
{
    private readonly byte[] lengths;
    private readonly ushort[] codes;

    private Vp8LPrefixCode(byte[] lengths, ushort[] codes, int usedSymbols, int firstSymbol)
    {
        this.lengths = lengths;
        this.codes = codes;
        this.UsedSymbols = usedSymbols;
        this.FirstSymbol = firstSymbol;
    }

    /// <summary>How many symbols the code actually carries.</summary>
    public int UsedSymbols { get; }

    /// <summary>The lowest symbol with a non-zero code length, or 0 when the code is empty.</summary>
    public int FirstSymbol { get; }

    /// <summary>True when the code names a single symbol, which the format codes with zero bits.</summary>
    public bool IsTrivial => this.UsedSymbols <= 1;

    /// <summary>The alphabet the code was built for.</summary>
    public int AlphabetSize => this.lengths.Length;

    /// <summary>Builds the optimal length-limited prefix code for <paramref name="histogram"/>.</summary>
    public static Vp8LPrefixCode Build(ReadOnlySpan<uint> histogram, int maxLength = HuffmanTree.MaxCodeLength)
    {
        var lengths = new byte[histogram.Length];
        int used = Vp8LHuffmanEncoder.BuildCodeLengths(histogram, maxLength, lengths, out int first);
        var codes = new ushort[histogram.Length];
        Vp8LHuffmanEncoder.BuildCodes(lengths, codes);
        return new Vp8LPrefixCode(lengths, codes, used, first);
    }

    /// <summary>Writes the code for <paramref name="symbol"/>; a single-symbol code writes nothing at all.</summary>
    public void Emit(Vp8LBitWriter writer, int symbol)
    {
        if (this.UsedSymbols > 1)
        {
            writer.PutBits(this.codes[symbol], this.lengths[symbol]);
        }
    }

    /// <summary>The number of bits <see cref="Emit"/> writes for <paramref name="symbol"/>.</summary>
    public int BitLength(int symbol) => this.UsedSymbols > 1 ? this.lengths[symbol] : 0;

    /// <summary>Writes the description of this code (the header a decoder reads before any pixel data).</summary>
    public void Store(Vp8LBitWriter writer) => Vp8LHuffmanEncoder.StoreCode(writer, this.lengths, this.UsedSymbols, this.FirstSymbol, this.SecondSymbol());

    /// <summary>The number of bits <see cref="Store"/> writes.</summary>
    public int StoredBitCount()
    {
        var probe = new Vp8LBitWriter(64);
        this.Store(probe);
        return (int)probe.BitPosition;
    }

    private int SecondSymbol()
    {
        if (this.UsedSymbols != 2)
        {
            return 0;
        }

        for (int i = this.FirstSymbol + 1; i < this.lengths.Length; i++)
        {
            if (this.lengths[i] != 0)
            {
                return i;
            }
        }

        return 0;
    }
}

/// <summary>
/// Builds and serializes the prefix codes of a VP8L bitstream (RFC 9649 section 3.6): length-limited Huffman
/// code construction, the canonical bit-reversed code assignment and the "code length code" that describes a
/// code inside the bitstream, including the 16/17/18 repeat symbols and the trailing-zero trimming.
/// </summary>
internal static class Vp8LHuffmanEncoder
{
    /// <summary>Number of symbols in the code that describes the other codes' lengths.</summary>
    public const int NumCodeLengthCodes = 19;

    /// <summary>Longest code the code-length code may use; its lengths are stored in three bits each.</summary>
    private const int MaxCodeLengthCodeLength = 7;

    private const int DefaultCodeLength = 8;

    private static ReadOnlySpan<byte> CodeLengthCodeOrder => new byte[] { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

    /// <summary>
    /// Fills <paramref name="lengths"/> with a prefix code for <paramref name="histogram"/> whose longest code
    /// is at most <paramref name="maxLength"/> bits, and returns how many symbols the code carries.
    /// </summary>
    /// <remarks>
    /// The length limit is reached by repeatedly raising a floor under every non-zero count and rebuilding: as
    /// the floor grows the counts flatten out and the tree becomes balanced, so the loop always terminates well
    /// before the limit of a 4096-symbol alphabet (12 bits) is exceeded.
    /// </remarks>
    public static int BuildCodeLengths(ReadOnlySpan<uint> histogram, int maxLength, Span<byte> lengths, out int firstSymbol)
    {
        lengths.Clear();
        firstSymbol = 0;

        int used = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            if (histogram[i] != 0)
            {
                if (used == 0)
                {
                    firstSymbol = i;
                }

                used++;
            }
        }

        if (used == 0)
        {
            return 0;
        }

        if (used == 1)
        {
            // A single symbol is described with one bit but coded with none.
            lengths[firstSymbol] = 1;
            return 1;
        }

        var symbols = new int[used];
        var weights = new long[used];
        int next = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            if (histogram[i] != 0)
            {
                symbols[next++] = i;
            }
        }

        var order = new int[used];
        var nodeWeight = new long[(2 * used) - 1];
        var left = new int[used - 1];
        var right = new int[used - 1];
        var depths = new byte[used];
        var stackNode = new int[2 * used];
        var stackDepth = new int[2 * used];

        for (long floor = 1; ; floor *= 2)
        {
            for (int i = 0; i < used; i++)
            {
                weights[i] = Math.Max(histogram[symbols[i]], floor);
                order[i] = i;
            }

            SortByWeight(weights, order, used);
            for (int i = 0; i < used; i++)
            {
                nodeWeight[i] = weights[order[i]];
            }

            BuildTree(nodeWeight, left, right, used);
            int maxDepth = ComputeDepths(left, right, used, depths, stackNode, stackDepth);
            if (maxDepth <= maxLength)
            {
                for (int i = 0; i < used; i++)
                {
                    lengths[symbols[order[i]]] = depths[i];
                }

                return used;
            }
        }
    }

    /// <summary>Assigns the canonical codes for <paramref name="lengths"/>, stored bit-reversed for the LSB-first reader.</summary>
    public static void BuildCodes(ReadOnlySpan<byte> lengths, Span<ushort> codes)
    {
        Span<int> depthCount = stackalloc int[HuffmanTree.MaxCodeLength + 1];
        depthCount.Clear();
        foreach (byte length in lengths)
        {
            depthCount[length]++;
        }

        depthCount[0] = 0;
        Span<int> nextCode = stackalloc int[HuffmanTree.MaxCodeLength + 1];
        int code = 0;
        nextCode[0] = 0;
        for (int i = 1; i <= HuffmanTree.MaxCodeLength; i++)
        {
            code = (code + depthCount[i - 1]) << 1;
            nextCode[i] = code;
        }

        for (int i = 0; i < lengths.Length; i++)
        {
            int length = lengths[i];
            codes[i] = length > 0 ? ReverseBits(nextCode[length]++, length) : (ushort)0;
        }
    }

    /// <summary>Writes the description of a prefix code: the two-symbol short form when it fits, the full form otherwise.</summary>
    public static void StoreCode(Vp8LBitWriter writer, ReadOnlySpan<byte> lengths, int usedSymbols, int firstSymbol, int secondSymbol)
    {
        if (usedSymbols == 0)
        {
            // The smallest legal description: a one-symbol code naming symbol 0, which nothing ever references.
            writer.PutBits(0x01, 4);
            return;
        }

        if (usedSymbols <= 2 && firstSymbol < 256 && secondSymbol < 256)
        {
            writer.PutBits(1, 1);
            writer.PutBits((uint)(usedSymbols - 1), 1);
            if (firstSymbol <= 1)
            {
                writer.PutBits(0, 1);
                writer.PutBits((uint)firstSymbol, 1);
            }
            else
            {
                writer.PutBits(1, 1);
                writer.PutBits((uint)firstSymbol, 8);
            }

            if (usedSymbols == 2)
            {
                writer.PutBits((uint)secondSymbol, 8);
            }

            return;
        }

        StoreFullCode(writer, lengths);
    }

    private static void StoreFullCode(Vp8LBitWriter writer, ReadOnlySpan<byte> lengths)
    {
        Span<byte> tokens = new byte[(2 * lengths.Length) + 8];
        Span<byte> extra = new byte[(2 * lengths.Length) + 8];
        int tokenCount = CreateTokens(lengths, tokens, extra);

        Span<uint> histogram = stackalloc uint[NumCodeLengthCodes];
        histogram.Clear();
        for (int i = 0; i < tokenCount; i++)
        {
            histogram[tokens[i]]++;
        }

        Span<byte> clLengths = stackalloc byte[NumCodeLengthCodes];
        BuildCodeLengths(histogram, MaxCodeLengthCodeLength, clLengths, out _);
        Span<ushort> clCodes = stackalloc ushort[NumCodeLengthCodes];
        BuildCodes(clLengths, clCodes);

        writer.PutBits(0, 1);

        int storedCodes = NumCodeLengthCodes;
        while (storedCodes > 4 && clLengths[CodeLengthCodeOrder[storedCodes - 1]] == 0)
        {
            storedCodes--;
        }

        writer.PutBits((uint)(storedCodes - 4), 4);
        for (int i = 0; i < storedCodes; i++)
        {
            writer.PutBits(clLengths[CodeLengthCodeOrder[i]], 3);
        }

        // Trailing runs of zeros can be dropped by telling the decoder how many tokens to read.
        int trimmed = tokenCount;
        int trailingBits = 0;
        for (int i = tokenCount - 1; i >= 0; i--)
        {
            int token = tokens[i];
            if (token != 0 && token != 17 && token != 18)
            {
                break;
            }

            trimmed--;
            trailingBits += clLengths[token] + (token == 17 ? 3 : token == 18 ? 7 : 0);
        }

        bool writeTrimmed = trimmed > 1 && trailingBits > 12;
        writer.PutBits(writeTrimmed ? 1u : 0u, 1);
        if (writeTrimmed)
        {
            if (trimmed == 2)
            {
                writer.PutBits(0, 3 + 2);
            }
            else
            {
                int bits = Log2Floor(trimmed - 2);
                int pairs = (bits / 2) + 1;
                writer.PutBits((uint)(pairs - 1), 3);
                writer.PutBits((uint)(trimmed - 2), pairs * 2);
            }
        }

        int emit = writeTrimmed ? trimmed : tokenCount;
        for (int i = 0; i < emit; i++)
        {
            int token = tokens[i];
            writer.PutBits(clCodes[token], clLengths[token]);
            switch (token)
            {
                case 16:
                    writer.PutBits(extra[i], 2);
                    break;
                case 17:
                    writer.PutBits(extra[i], 3);
                    break;
                case 18:
                    writer.PutBits(extra[i], 7);
                    break;
            }
        }
    }

    /// <summary>Turns a run-length view of the code lengths into the 0..18 token stream the format defines.</summary>
    private static int CreateTokens(ReadOnlySpan<byte> lengths, Span<byte> tokens, Span<byte> extra)
    {
        int count = 0;
        int previous = DefaultCodeLength;
        int i = 0;
        while (i < lengths.Length)
        {
            byte value = lengths[i];
            int k = i + 1;
            while (k < lengths.Length && lengths[k] == value)
            {
                k++;
            }

            int runs = k - i;
            if (value == 0)
            {
                count = CodeRepeatedZeros(runs, tokens, extra, count);
            }
            else
            {
                count = CodeRepeatedValues(runs, value, previous, tokens, extra, count);
                previous = value;
            }

            i = k;
        }

        return count;
    }

    private static int CodeRepeatedValues(int repetitions, byte value, int previous, Span<byte> tokens, Span<byte> extra, int count)
    {
        if (value != previous)
        {
            tokens[count] = value;
            extra[count++] = 0;
            repetitions--;
        }

        while (repetitions >= 1)
        {
            if (repetitions < 3)
            {
                for (int i = 0; i < repetitions; i++)
                {
                    tokens[count] = value;
                    extra[count++] = 0;
                }

                break;
            }

            if (repetitions < 7)
            {
                tokens[count] = 16;
                extra[count++] = (byte)(repetitions - 3);
                break;
            }

            tokens[count] = 16;
            extra[count++] = 3;
            repetitions -= 6;
        }

        return count;
    }

    private static int CodeRepeatedZeros(int repetitions, Span<byte> tokens, Span<byte> extra, int count)
    {
        while (repetitions >= 1)
        {
            if (repetitions < 3)
            {
                for (int i = 0; i < repetitions; i++)
                {
                    tokens[count] = 0;
                    extra[count++] = 0;
                }

                break;
            }

            if (repetitions < 11)
            {
                tokens[count] = 17;
                extra[count++] = (byte)(repetitions - 3);
                break;
            }

            if (repetitions < 139)
            {
                tokens[count] = 18;
                extra[count++] = (byte)(repetitions - 11);
                break;
            }

            tokens[count] = 18;
            extra[count++] = 127;
            repetitions -= 138;
        }

        return count;
    }

    private static void SortByWeight(long[] weights, int[] order, int count)
    {
        var keys = new long[count];
        for (int i = 0; i < count; i++)
        {
            // The symbol index is folded into the low bits so equal weights keep a deterministic order.
            keys[i] = (weights[i] << 20) + i;
        }

        Array.Sort(keys, order, 0, count);
    }

    /// <summary>Merges the sorted leaves into a Huffman tree; internal node <c>k</c> has index <c>count + k</c>.</summary>
    private static void BuildTree(long[] nodeWeight, int[] left, int[] right, int count)
    {
        int leaf = 0;
        int internalStart = count;
        int internalEnd = count;
        for (int k = 0; k < count - 1; k++)
        {
            int a = PickSmallest(nodeWeight, ref leaf, ref internalStart, count, internalEnd);
            int b = PickSmallest(nodeWeight, ref leaf, ref internalStart, count, internalEnd);
            int node = count + k;
            nodeWeight[node] = nodeWeight[a] + nodeWeight[b];
            left[k] = a;
            right[k] = b;
            internalEnd = node + 1;
        }
    }

    private static int PickSmallest(long[] nodeWeight, ref int leaf, ref int internalStart, int count, int internalEnd)
    {
        bool leafAvailable = leaf < count;
        bool internalAvailable = internalStart < internalEnd;

        // Ties go to the already-merged node, which keeps the tree shallower.
        if (internalAvailable && (!leafAvailable || nodeWeight[internalStart] <= nodeWeight[leaf]))
        {
            return internalStart++;
        }

        return leaf++;
    }

    private static int ComputeDepths(int[] left, int[] right, int count, byte[] depths, int[] stackNode, int[] stackDepth)
    {
        int max = 0;
        int top = 0;
        stackNode[top] = (2 * count) - 2;
        stackDepth[top++] = 0;
        while (top > 0)
        {
            top--;
            int node = stackNode[top];
            int depth = stackDepth[top];
            if (node < count)
            {
                depths[node] = (byte)depth;
                max = Math.Max(max, depth);
                continue;
            }

            int k = node - count;
            stackNode[top] = left[k];
            stackDepth[top++] = depth + 1;
            stackNode[top] = right[k];
            stackDepth[top++] = depth + 1;
        }

        return max;
    }

    private static ushort ReverseBits(int value, int bits)
    {
        int result = 0;
        for (int i = 0; i < bits; i++)
        {
            result = (result << 1) | ((value >> i) & 1);
        }

        return (ushort)result;
    }

    private static int Log2Floor(int value)
    {
        int result = 0;
        while (value > 1)
        {
            value >>= 1;
            result++;
        }

        return result;
    }
}
