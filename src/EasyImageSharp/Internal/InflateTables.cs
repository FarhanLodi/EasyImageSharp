using System.Runtime.CompilerServices;

namespace EasyImageSharp;

/// <summary>Which of the three DEFLATE alphabets a Huffman table decodes.</summary>
internal enum TableKind
{
    /// <summary>The 19-symbol code-length alphabet that a dynamic block header is itself coded with.</summary>
    CodeLengths,

    /// <summary>The literal/length alphabet: 0-255 literals, 256 end-of-block, 257-285 lengths.</summary>
    LitLen,

    /// <summary>The distance alphabet: 0-29 distances (30 and 31 exist but are never legal).</summary>
    Distance,
}

/// <summary>
/// The static tables of RFC 1951 and the canonical Huffman table builder shared by the inflater.
/// <para>
/// A code is decoded through a two-level table indexed by the next bits of the LSB-first bit reader, so codes
/// are stored bit-reversed. The first <c>rootBits</c> bits index the root table; a code longer than that lands
/// on a pointer entry naming a sub-table and the width of the extra index it needs. Table sizes are zlib's
/// proven <c>ENOUGH</c> bounds for these exact root sizes, so a table is rented once per decode and never
/// grown.
/// </para>
/// <para>
/// One entry is a <see cref="uint"/>: bits 0-7 the code length, bits 8-11 the extra-bit count, bits 12-15 the
/// flags and bits 16-31 the value. Crucially the code length of a leaf is always its <em>full</em> length,
/// including the root bits already used to reach a sub-table, so a decoder can compare it against the bits it
/// has buffered without tracking how it got there, and consume exactly that many bits once. That is the one
/// deliberate departure from zlib, which stores the residual length in sub-tables.
/// </para>
/// <para>
/// Decoding one symbol from <c>bits</c>, the low bits of an LSB-first accumulator holding <c>count</c> valid
/// bits: read <c>entry = table[bits &amp; ((1 &lt;&lt; rootBits) - 1)]</c>; if <see cref="CodeLength"/> of it
/// exceeds <c>count</c> there is not enough input yet and nothing has been consumed; if
/// <see cref="IsSubtable"/> then re-read
/// <c>entry = table[Value(entry) + ((bits &gt;&gt; rootBits) &amp; ((1 &lt;&lt; ExtraBits(entry)) - 1))]</c> and
/// test its length the same way. Either test is sound with fewer bits buffered than the index consumed,
/// because an entry is replicated across every index that agrees with it in its own code length: an entry
/// whose length fits in <c>count</c> is the right entry however the bits above it happened to read.
/// </para>
/// <para>
/// Validity follows zlib exactly, because the PNG path has to accept and reject the same streams it does. An
/// over-subscribed code is always rejected. An incomplete code is rejected too, except when its longest code
/// is one bit - a single one-bit code, the degenerate tree an encoder emits for a block that references one
/// distance or none - and except when no symbol is coded at all. Those two build a table of invalid-code
/// entries instead, so the error surfaces only if the alphabet is actually used.
/// </para>
/// </summary>
internal static class InflateTables
{
    /// <summary>Literal/length symbols, including the two that exist but are never legal.</summary>
    public const int MaxLitLenSymbols = 288;

    /// <summary>Distance symbols, including the two that exist but are never legal.</summary>
    public const int MaxDistSymbols = 32;

    /// <summary>Symbols in the code-length alphabet.</summary>
    public const int MaxCodeLengthSymbols = 19;

    /// <summary>Longest Huffman code DEFLATE allows.</summary>
    public const int MaxCodeLength = 15;

    /// <summary>Root index width of a literal/length table.</summary>
    public const int LitLenRootBits = 9;

    /// <summary>Root index width of a distance table.</summary>
    public const int DistRootBits = 6;

    /// <summary>
    /// Root index width of a code-length table. A block header spells its code lengths as three-bit values, so
    /// in a real stream no code in that alphabet exceeds seven bits and only the root is ever filled.
    /// </summary>
    public const int CodeLengthRootBits = 7;

    /// <summary>
    /// Entries a literal/length table can need at <see cref="LitLenRootBits"/>, zlib's ENOUGH_LENS. Like
    /// zlib's, the bound is for the 286 symbols a dynamic block may legally declare; a build that would need
    /// more space fails rather than overrunning, so a caller that has not yet rejected HLIT above 286 still
    /// cannot corrupt anything.
    /// </summary>
    public const int EnoughLitLen = 852;

    /// <summary>Entries a distance table can need at <see cref="DistRootBits"/>, zlib's ENOUGH_DISTS, for 30 symbols.</summary>
    public const int EnoughDist = 592;

    /// <summary>
    /// Entries a code-length table can need at <see cref="CodeLengthRootBits"/>: the worst case over every
    /// complete code on 19 symbols, computed the same way zlib computes the other two. A real stream only ever
    /// fills the 128-entry root, but sizing for the true bound means the builder accepts exactly what zlib
    /// accepts instead of failing a deep code for want of space.
    /// </summary>
    public const int EnoughCodeLength = 388;

    /// <summary>Set on a leaf whose value is a literal byte, or a code-length symbol in a code-length table.</summary>
    public const uint LiteralFlag = 0x1000;

    /// <summary>Set on the leaf for symbol 256, the end of a block.</summary>
    public const uint EndOfBlockFlag = 0x2000;

    /// <summary>Set on a root entry that points at a sub-table rather than decoding a symbol.</summary>
    public const uint SubtableFlag = 0x4000;

    /// <summary>Set on an entry that stands for a code the format does not define; decoding one is an error.</summary>
    public const uint InvalidFlag = 0x8000;

    /// <summary>Bit position of the extra-bit count.</summary>
    private const int ExtraShift = 8;

    /// <summary>Bit position of the value.</summary>
    private const int ValueShift = 16;

    /// <summary>Lengths 3..258 for literal/length symbols 257..285.</summary>
    private static readonly ushort[] LengthBase =
    {
        3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59,
        67, 83, 99, 115, 131, 163, 195, 227, 258,
    };

    /// <summary>Extra bits following literal/length symbols 257..285.</summary>
    private static readonly byte[] LengthExtra =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3,
        4, 4, 4, 4, 5, 5, 5, 5, 0,
    };

    /// <summary>Distances 1..24577 for distance symbols 0..29.</summary>
    private static readonly ushort[] DistanceBase =
    {
        1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769,
        1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577,
    };

    /// <summary>Extra bits following distance symbols 0..29.</summary>
    private static readonly byte[] DistanceExtra =
    {
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8,
        9, 9, 10, 10, 11, 11, 12, 12, 13, 13,
    };

    /// <summary>The type-1 literal/length table: root 9 bits, and no code is longer, so exactly 512 entries.</summary>
    public static readonly uint[] FixedLitLen = CreateFixedLitLen();

    /// <summary>The type-1 distance table: 32 five-bit codes replicated across a 64-entry root.</summary>
    public static readonly uint[] FixedDistance = CreateFixedDistance();

    /// <summary>
    /// The order in which a dynamic block header stores the 19 code-length code lengths, most-used symbol
    /// first so that a trailing run of unused ones can be omitted.
    /// </summary>
    public static ReadOnlySpan<byte> CodeLengthOrder
        => new byte[] { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

    /// <summary>The full length of the code this entry decodes, root bits included, in bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CodeLength(uint entry) => (int)(entry & 0xFF);

    /// <summary>
    /// Extra bits to read after the code: the length or distance extra-bit count of a leaf, or the width of the
    /// sub-table index of a pointer entry.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ExtraBits(uint entry) => (int)((entry >> ExtraShift) & 0xF);

    /// <summary>
    /// The literal byte, the length or distance base, or - for a pointer entry - the index of the sub-table
    /// from the start of the same table span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Value(uint entry) => (int)(entry >> ValueShift);

    /// <summary>True when the entry decodes a literal byte (or, in a code-length table, a code-length symbol).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLiteral(uint entry) => (entry & LiteralFlag) != 0;

    /// <summary>True when the entry decodes symbol 256, the end of the block.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEndOfBlock(uint entry) => (entry & EndOfBlockFlag) != 0;

    /// <summary>True when the entry points at a sub-table and a second lookup is needed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSubtable(uint entry) => (entry & SubtableFlag) != 0;

    /// <summary>True when the entry stands for a code the format does not define.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInvalid(uint entry) => (entry & InvalidFlag) != 0;

    /// <summary>Builds a leaf for a literal byte, or for a symbol of the code-length alphabet.</summary>
    /// <param name="codeLength">Full length of the code in bits.</param>
    /// <param name="literal">The byte, or the code-length symbol 0..18.</param>
    public static uint PackLiteral(int codeLength, int literal)
        => (uint)codeLength | LiteralFlag | ((uint)literal << ValueShift);

    /// <summary>Builds a leaf for a length symbol.</summary>
    /// <param name="codeLength">Full length of the code in bits.</param>
    /// <param name="lengthBase">Smallest match length the symbol stands for.</param>
    /// <param name="extraBits">Bits to read and add to <paramref name="lengthBase"/>.</param>
    public static uint PackLength(int codeLength, int lengthBase, int extraBits)
        => (uint)codeLength | ((uint)extraBits << ExtraShift) | ((uint)lengthBase << ValueShift);

    /// <summary>Builds a leaf for a distance symbol.</summary>
    /// <param name="codeLength">Full length of the code in bits.</param>
    /// <param name="distanceBase">Smallest distance the symbol stands for.</param>
    /// <param name="extraBits">Bits to read and add to <paramref name="distanceBase"/>.</param>
    public static uint PackDistance(int codeLength, int distanceBase, int extraBits)
        => (uint)codeLength | ((uint)extraBits << ExtraShift) | ((uint)distanceBase << ValueShift);

    /// <summary>Builds the leaf for symbol 256.</summary>
    /// <param name="codeLength">Full length of the code in bits.</param>
    public static uint PackEndOfBlock(int codeLength)
        => (uint)codeLength | EndOfBlockFlag;

    /// <summary>Builds an entry for a code the format leaves undefined, or for a hole in a degenerate tree.</summary>
    /// <param name="codeLength">Bits to consume before reporting the error.</param>
    public static uint PackInvalid(int codeLength)
        => (uint)codeLength | InvalidFlag;

    /// <summary>Builds a root entry pointing at a sub-table.</summary>
    /// <param name="rootBits">Root index width, the bits this entry consumes before the second lookup.</param>
    /// <param name="indexBits">Width of the sub-table index, taken from the bits above the root.</param>
    /// <param name="offset">Index of the sub-table from the start of the table span.</param>
    public static uint PackSubtable(int rootBits, int indexBits, int offset)
        => (uint)rootBits | ((uint)indexBits << ExtraShift) | SubtableFlag | ((uint)offset << ValueShift);

    /// <summary>Entries a table of this kind can ever need, at the root width the inflater uses for it.</summary>
    /// <param name="kind">The alphabet.</param>
    public static int MaxTableSize(TableKind kind) => kind switch
    {
        TableKind.CodeLengths => EnoughCodeLength,
        TableKind.LitLen => EnoughLitLen,
        _ => EnoughDist,
    };

    /// <summary>Root index width the inflater uses for a table of this kind.</summary>
    /// <param name="kind">The alphabet.</param>
    public static int RootBits(TableKind kind) => kind switch
    {
        TableKind.CodeLengths => CodeLengthRootBits,
        TableKind.LitLen => LitLenRootBits,
        _ => DistRootBits,
    };

    /// <summary>
    /// Builds the two-level lookup table for a canonical code given one code length per symbol, and returns
    /// false when those lengths do not describe a code DEFLATE accepts.
    /// <para>
    /// The algorithm is zlib's: sort the symbols by code length, then walk the canonical codes in order,
    /// replicating each into every table slot whose low bits match it. The one difference is that the root
    /// width is fixed rather than shrunk to the longest code, because the decoder indexes the root with a
    /// constant mask; a short code simply replicates further. That is safe for every alphabet used here,
    /// whose shortest code can never exceed the root width - a complete code over <c>n</c> symbols must have
    /// a code of at most <c>log2(n)</c> bits, which is 8, 5 and 4 for the three alphabets against roots of 9,
    /// 6 and 7.
    /// </para>
    /// </summary>
    /// <param name="codeLengths">Code length per symbol, 0 for a symbol that is not coded.</param>
    /// <param name="symbolCount">Symbols to read from <paramref name="codeLengths"/>.</param>
    /// <param name="rootBits">Root index width; the first <c>1 &lt;&lt; rootBits</c> entries are the root table.</param>
    /// <param name="kind">The alphabet, which decides both the leaf payloads and the validity rules.</param>
    /// <param name="table">Destination, at least <see cref="MaxTableSize"/> entries for a worst-case code.</param>
    /// <param name="used">Entries written, root table included; zero when the build fails.</param>
    public static bool TryBuild(ReadOnlySpan<byte> codeLengths, int symbolCount, int rootBits, TableKind kind, Span<uint> table, out int used)
    {
        used = 0;
        if (symbolCount <= 0 || symbolCount > codeLengths.Length || symbolCount > MaxLitLenSymbols ||
            rootBits < 1 || rootBits > MaxCodeLength)
        {
            return false;
        }

        int rootSize = 1 << rootBits;
        if (table.Length < rootSize)
        {
            return false;
        }

        Span<int> count = stackalloc int[MaxCodeLength + 1];
        count.Clear();
        for (int symbol = 0; symbol < symbolCount; symbol++)
        {
            int length = codeLengths[symbol];
            if (length > MaxCodeLength)
            {
                return false;
            }

            count[length]++;
        }

        int max = MaxCodeLength;
        while (max >= 1 && count[max] == 0)
        {
            max--;
        }

        if (max == 0)
        {
            // Not one symbol is coded. zlib builds a table that fails on use rather than failing here, because
            // a block that never references a distance may legally carry an empty distance tree.
            table[..rootSize].Fill(PackInvalid(1));
            used = rootSize;
            return true;
        }

        int min = 1;
        while (count[min] == 0)
        {
            min++;
        }

        // Over-subscription is fatal for every alphabet; an incomplete code is fatal too, except for the
        // single one-bit code that zlib has always accepted in a literal/length or distance tree.
        int left = 1;
        for (int length = 1; length <= MaxCodeLength; length++)
        {
            left <<= 1;
            left -= count[length];
            if (left < 0)
            {
                return false;
            }
        }

        if (left > 0 && (kind == TableKind.CodeLengths || max != 1))
        {
            return false;
        }

        // Unreachable for the three alphabets at their prescribed roots (see the remarks on this method), but
        // a shorter root than the shortest code would make the replication step below run off the table.
        if (min > rootBits)
        {
            return false;
        }

        Span<int> offsets = stackalloc int[MaxCodeLength + 1];
        offsets[1] = 0;
        for (int length = 1; length < MaxCodeLength; length++)
        {
            offsets[length + 1] = offsets[length] + count[length];
        }

        Span<ushort> sorted = stackalloc ushort[MaxLitLenSymbols];
        for (int symbol = 0; symbol < symbolCount; symbol++)
        {
            int length = codeLengths[symbol];
            if (length != 0)
            {
                sorted[offsets[length]++] = (ushort)symbol;
            }
        }

        int huff = 0;             // Current canonical code, bit-reversed so it indexes the table directly.
        int sym = 0;              // Position in the length-sorted symbol list.
        int len = min;            // Length of the code being placed.
        int next = 0;             // Start of the table being filled: the root table, then each sub-table.
        int curr = rootBits;      // Index width of that table.
        int drop = 0;             // Bits the root already consumed, once sub-tables begin.
        int low = -1;             // Root slot the current sub-table hangs off, -1 before the first one.
        int mask = rootSize - 1;
        int limit = MaxTableSize(kind);
        int filled = rootSize;
        int currentSize;

        while (true)
        {
            uint entry = MakeEntry(kind, sorted[sym], len);

            // Fill every slot of the current table whose low (len - drop) bits are this code.
            int step = 1 << (len - drop);
            currentSize = 1 << curr;
            for (int fill = currentSize - step; fill >= 0; fill -= step)
            {
                table[next + (huff >> drop) + fill] = entry;
            }

            // Increment the code, which is stored reversed, so the carry runs from the high bit down.
            int increment = 1 << (len - 1);
            while ((huff & increment) != 0)
            {
                increment >>= 1;
            }

            if (increment != 0)
            {
                huff &= increment - 1;
                huff += increment;
            }
            else
            {
                huff = 0;
            }

            sym++;
            if (--count[len] == 0)
            {
                if (len == max)
                {
                    break;
                }

                len = codeLengths[sorted[sym]];
            }

            if (len <= rootBits || (huff & mask) == low)
            {
                continue;
            }

            // A code longer than the root that does not share the current sub-table needs a new one, sized to
            // the codes still to be placed under this root slot.
            if (drop == 0)
            {
                drop = rootBits;
            }

            next += currentSize;
            curr = len - drop;
            left = 1 << curr;
            while (curr + drop < max)
            {
                left -= count[curr + drop];
                if (left <= 0)
                {
                    break;
                }

                curr++;
                left <<= 1;
            }

            filled += 1 << curr;
            if (filled > limit || filled > table.Length)
            {
                return false;
            }

            low = huff & mask;
            table[low] = PackSubtable(rootBits, curr, next);
        }

        // An accepted incomplete code leaves exactly one hole, and only ever in the root table, since the one
        // shape that gets this far is a single one-bit code.
        if (huff != 0)
        {
            uint invalid = PackInvalid(len);
            int step = 1 << (len - drop);
            for (int fill = (1 << curr) - step; fill >= 0; fill -= step)
            {
                table[next + (huff >> drop) + fill] = invalid;
            }
        }

        used = filled;
        return true;
    }

    /// <summary>Builds the leaf for one symbol of the given alphabet.</summary>
    /// <param name="kind">The alphabet.</param>
    /// <param name="symbol">The symbol.</param>
    /// <param name="codeLength">Full length of its code in bits.</param>
    private static uint MakeEntry(TableKind kind, int symbol, int codeLength)
    {
        if (kind == TableKind.CodeLengths)
        {
            return PackLiteral(codeLength, symbol);
        }

        if (kind == TableKind.Distance)
        {
            return symbol < DistanceBase.Length
                ? PackDistance(codeLength, DistanceBase[symbol], DistanceExtra[symbol])
                : PackInvalid(codeLength);
        }

        if (symbol < 256)
        {
            return PackLiteral(codeLength, symbol);
        }

        if (symbol == 256)
        {
            return PackEndOfBlock(codeLength);
        }

        return symbol - 257 < LengthBase.Length
            ? PackLength(codeLength, LengthBase[symbol - 257], LengthExtra[symbol - 257])
            : PackInvalid(codeLength);
    }

    /// <summary>Builds the fixed literal/length table of RFC 1951 section 3.2.6.</summary>
    private static uint[] CreateFixedLitLen()
    {
        Span<byte> lengths = stackalloc byte[MaxLitLenSymbols];
        lengths[..144].Fill(8);
        lengths[144..256].Fill(9);
        lengths[256..280].Fill(7);
        lengths[280..].Fill(8);

        var table = new uint[1 << LitLenRootBits];
        if (!TryBuild(lengths, MaxLitLenSymbols, LitLenRootBits, TableKind.LitLen, table, out _))
        {
            throw new InvalidOperationException("The fixed literal/length code lengths do not form a valid code.");
        }

        return table;
    }

    /// <summary>Builds the fixed distance table of RFC 1951 section 3.2.6: 32 codes of five bits each.</summary>
    private static uint[] CreateFixedDistance()
    {
        Span<byte> lengths = stackalloc byte[MaxDistSymbols];
        lengths.Fill(5);

        var table = new uint[1 << DistRootBits];
        if (!TryBuild(lengths, MaxDistSymbols, DistRootBits, TableKind.Distance, table, out _))
        {
            throw new InvalidOperationException("The fixed distance code lengths do not form a valid code.");
        }

        return table;
    }
}
