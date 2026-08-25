namespace EasyImageSharp.Formats.Tiff;

/// <summary>The bilevel coding schemes TIFF stores under compression tags 2, 3 and 4.</summary>
internal enum TiffCcittScheme
{
    /// <summary>Compression 2: one-dimensional Modified Huffman runs, each row padded to a byte boundary.</summary>
    ModifiedHuffman,

    /// <summary>Compression 3: ITU-T T.4 (Group 3), one- or two-dimensional, rows separated by EOL codes.</summary>
    Group3,

    /// <summary>Compression 4: ITU-T T.6 (Group 4), purely two-dimensional with no EOL codes.</summary>
    Group4,
}

/// <summary>How one CCITT-coded segment is laid out.</summary>
/// <param name="Scheme">The coding scheme.</param>
/// <param name="TwoDimensional">For <see cref="TiffCcittScheme.Group3"/>: bit 0 of T4Options, meaning rows may be 2D-coded.</param>
/// <param name="ByteAlign">True when every row starts on a byte boundary (mandatory for compression 2, optional elsewhere).</param>
/// <param name="LsbFirst">True when FillOrder is 2, i.e. the coded bits fill each byte from its least significant end.</param>
internal readonly record struct TiffCcittOptions(TiffCcittScheme Scheme, bool TwoDimensional, bool ByteAlign, bool LsbFirst);

/// <summary>
/// Decoder and encoder for the CCITT bilevel schemes of ITU-T Recommendations T.4 (Group 3) and T.6
/// (Group 4) as embedded in TIFF, plus the Modified Huffman variant of TIFF 6.0 compression 2.
/// </summary>
/// <remarks>
/// <para>
/// Decoded rows are written bit-packed, most significant bit first, with a set bit meaning a black pixel —
/// the "0 is white" convention CCITT-coded TIFF pages use, so the page's PhotometricInterpretation decides
/// how those bits become grey levels exactly as it does for uncompressed bilevel data.
/// </para>
/// <para>
/// The decoder never throws on damaged coded data: a row that cannot be decoded ends the segment and the
/// remaining rows stay white, which is what fax readers do with a corrupted transmission. Every loop is
/// bounded by the row width or the input length, so a hostile stream cannot stall the decoder.
/// </para>
/// </remarks>
internal static class TiffCcitt
{
    /// <summary>The T.4 end-of-line code: eleven zero bits followed by a one.</summary>
    private const int EndOfLineCode = 1;

    /// <summary>Length of <see cref="EndOfLineCode"/> in bits.</summary>
    private const int EndOfLineBits = 12;

    /// <summary>Upper bound on consecutive EOL codes accepted between two rows (a return-to-control sequence is six).</summary>
    private const int MaxConsecutiveEols = 64;

    /// <summary>Byte-reversal table for FillOrder 2 segments.</summary>
    private static readonly byte[] ReversedBytes = BuildReversedBytes();

    /// <summary>
    /// Decodes one strip or tile of CCITT-coded data into bit-packed rows.
    /// </summary>
    /// <param name="input">The coded bytes of the segment.</param>
    /// <param name="output">Receives <paramref name="rows"/> rows of <c>(width + 7) / 8</c> bytes.</param>
    /// <param name="width">The pixel width of a row.</param>
    /// <param name="rows">The number of rows the segment holds.</param>
    /// <param name="options">The layout of the coded data.</param>
    public static void Decode(ReadOnlySpan<byte> input, Span<byte> output, int width, int rows, in TiffCcittOptions options)
    {
        output.Clear();
        if (width <= 0 || rows <= 0)
        {
            return;
        }

        int rowBytes = (width + 7) / 8;
        var reader = new BitReader(input, options.LsbFirst);

        // Changing-element positions of the reference and coding lines; a row can never hold more than one
        // transition per pixel plus the two sentinels the search needs.
        int[] reference = new int[width + 3];
        int[] coding = new int[width + 3];
        int referenceCount = 0;

        bool group3 = options.Scheme == TiffCcittScheme.Group3;
        bool twoDimensional = options.Scheme == TiffCcittScheme.Group4;

        for (int y = 0; y < rows; y++)
        {
            if (options.ByteAlign)
            {
                reader.AlignToByte();
            }

            if (options.Scheme != TiffCcittScheme.ModifiedHuffman
                && !ConsumeRowSeparator(ref reader, group3, options.TwoDimensional, ref twoDimensional))
            {
                return; // EOFB / RTC, or the coded data ran out: the remaining rows stay white.
            }

            if (reader.AtEnd)
            {
                return;
            }

            int startBit = reader.BitPosition;
            int count = twoDimensional
                ? DecodeTwoDimensionalRow(ref reader, reference, referenceCount, coding, width)
                : DecodeOneDimensionalRow(ref reader, coding, width);

            if (count < 0 && reader.BitPosition == startBit && (startBit & 7) != 0)
            {
                // Writers that byte-align rows without saying so leave the reader mid-byte; the row consumed
                // nothing, so give it one more chance from the next boundary before abandoning the segment.
                reader.AlignToByte();
                count = twoDimensional
                    ? DecodeTwoDimensionalRow(ref reader, reference, referenceCount, coding, width)
                    : DecodeOneDimensionalRow(ref reader, coding, width);
            }

            if (count < 0)
            {
                return;
            }

            FillRow(output.Slice(y * rowBytes, rowBytes), coding, count, width);
            (reference, coding) = (coding, reference);
            referenceCount = count;
        }
    }

    /// <summary>
    /// Encodes bit-packed bilevel rows (set bit = black) into a CCITT-coded segment.
    /// </summary>
    /// <param name="input">The rows, each <c>(width + 7) / 8</c> bytes, most significant bit first.</param>
    /// <param name="width">The pixel width of a row.</param>
    /// <param name="rows">The number of rows.</param>
    /// <param name="scheme">The coding scheme to produce.</param>
    /// <returns>The coded bytes, filled most significant bit first (FillOrder 1).</returns>
    public static byte[] Encode(ReadOnlySpan<byte> input, int width, int rows, TiffCcittScheme scheme)
    {
        var writer = new BitWriter();
        int rowBytes = (width + 7) / 8;
        int[] reference = new int[width + 3];
        int[] coding = new int[width + 3];
        int referenceCount = 0;

        for (int y = 0; y < rows; y++)
        {
            ReadOnlySpan<byte> row = input.Slice(y * rowBytes, rowBytes);
            int count = ExtractChanges(row, width, coding);

            switch (scheme)
            {
                case TiffCcittScheme.ModifiedHuffman:
                    writer.AlignToByte();
                    EncodeOneDimensionalRow(writer, coding, count, width);
                    break;
                case TiffCcittScheme.Group3:
                    WriteEndOfLine(writer);
                    EncodeOneDimensionalRow(writer, coding, count, width);
                    break;
                default:
                    EncodeTwoDimensionalRow(writer, reference, referenceCount, coding, count, width);
                    break;
            }

            (reference, coding) = (coding, reference);
            referenceCount = count;
        }

        switch (scheme)
        {
            case TiffCcittScheme.ModifiedHuffman:
                break;
            case TiffCcittScheme.Group3:
                // Return to control: six consecutive EOL codes (T.4 section 4.1.3).
                for (int i = 0; i < 6; i++)
                {
                    WriteEndOfLine(writer);
                }

                break;
            default:
                // End of facsimile block: two consecutive EOL codes (T.6 section 2.2.1).
                WriteEndOfLine(writer);
                WriteEndOfLine(writer);
                break;
        }

        return writer.ToArray();
    }

    /// <summary>
    /// Consumes the fill bits and EOL codes that separate two rows. Returns false when the segment is over
    /// (an end-of-facsimile-block, a return-to-control sequence, or exhausted input).
    /// </summary>
    private static bool ConsumeRowSeparator(ref BitReader reader, bool group3, bool mayBeTwoDimensional, ref bool twoDimensional)
    {
        int eols = 0;
        while (TryConsumeEndOfLine(ref reader))
        {
            eols++;
            if (group3 && mayBeTwoDimensional)
            {
                // T.4 section 4.2.1.3.1: the bit following an EOL is 1 for a 1D-coded row and 0 for a 2D-coded row.
                twoDimensional = reader.Peek(1) == 0;
                reader.Skip(1);
            }

            if (!group3 || eols >= 2)
            {
                // Group 4 has no EOLs at all, so any EOL is the EOFB; two or more in Group 3 is the RTC.
                return false;
            }

            if (eols >= MaxConsecutiveEols || reader.AtEnd)
            {
                return false;
            }

            break;
        }

        return !reader.AtEnd;
    }

    /// <summary>Consumes any fill bits followed by an EOL code, or leaves the reader untouched and returns false.</summary>
    private static bool TryConsumeEndOfLine(ref BitReader reader)
    {
        int start = reader.BitPosition;

        // No T.4 code begins with twelve zero bits, so a zero window can only be fill or the EOL itself.
        while (reader.Peek(EndOfLineBits) == 0 && !reader.AtEnd)
        {
            reader.Skip(1);
        }

        if (reader.Peek(EndOfLineBits) == EndOfLineCode)
        {
            reader.Skip(EndOfLineBits);
            return true;
        }

        reader.BitPosition = start;
        return false;
    }

    /// <summary>
    /// Decodes a one-dimensional (Modified Huffman) row into changing-element positions, returning their
    /// count or -1 when the row could not be decoded.
    /// </summary>
    private static int DecodeOneDimensionalRow(ref BitReader reader, int[] changes, int width)
    {
        int count = 0;
        int position = 0;
        int color = 0;
        int limit = changes.Length - 2;
        while (position < width && count < limit)
        {
            int run = ReadRun(ref reader, color);
            if (run < 0)
            {
                return count > 0 ? count : -1;
            }

            position += run;
            if (position > width)
            {
                position = width;
            }

            changes[count++] = position;
            color ^= 1;
        }

        return count;
    }

    /// <summary>
    /// Decodes a two-dimensional (T.4 2D / T.6) row against the reference line's changing elements,
    /// returning the coding line's changing-element count or -1 when the row could not be decoded.
    /// </summary>
    private static int DecodeTwoDimensionalRow(ref BitReader reader, int[] reference, int referenceCount, int[] coding, int width)
    {
        int count = 0;
        int a0 = -1;
        int color = 0;
        int hint = 0;
        int limit = coding.Length - 2;

        while (a0 < width && count < limit)
        {
            // b1 is the first changing element on the reference line right of a0 whose colour is the opposite
            // of the current colour; alternating transitions put it at an index of the same parity as `color`.
            while (hint > 0 && reference[hint - 1] > a0)
            {
                hint--;
            }

            while (hint < referenceCount && reference[hint] <= a0)
            {
                hint++;
            }

            int index = hint;
            if (((index ^ color) & 1) != 0)
            {
                index++;
            }

            int b1 = index < referenceCount ? reference[index] : width;
            int b2 = index + 1 < referenceCount ? reference[index + 1] : width;

            int mode = reader.Peek(7);
            int delta;
            if (mode >= 0b100_0000)
            {
                reader.Skip(1); // V(0)
                delta = 0;
            }
            else if (mode >= 0b011_0000)
            {
                reader.Skip(3); // VR(1)
                delta = 1;
            }
            else if (mode >= 0b010_0000)
            {
                reader.Skip(3); // VL(1)
                delta = -1;
            }
            else if (mode >= 0b001_0000)
            {
                // Horizontal: two runs starting at a0 (or at 0 for the imaginary element before the row).
                reader.Skip(3);
                int start = a0 < 0 ? 0 : a0;
                int first = ReadRun(ref reader, color);
                if (first < 0)
                {
                    return count > 0 ? count : -1;
                }

                int second = ReadRun(ref reader, color ^ 1);
                if (second < 0)
                {
                    return count > 0 ? count : -1;
                }

                int a1 = Math.Min(start + first, width);
                int a2 = Math.Min(a1 + second, width);
                coding[count++] = a1;
                coding[count++] = a2;
                a0 = a2;
                continue;
            }
            else if (mode >= 0b000_1000)
            {
                // Pass: the current colour runs past b2, so no transition is recorded.
                reader.Skip(4);
                a0 = b2;
                continue;
            }
            else if (mode >= 0b000_0110)
            {
                reader.Skip(6); // VR(2)
                delta = 2;
            }
            else if (mode >= 0b000_0100)
            {
                reader.Skip(6); // VL(2)
                delta = -2;
            }
            else if (mode == 0b000_0011)
            {
                reader.Skip(7); // VR(3)
                delta = 3;
            }
            else if (mode == 0b000_0010)
            {
                reader.Skip(7); // VL(3)
                delta = -3;
            }
            else
            {
                // 0000001 is the (unsupported) uncompressed-mode extension; 0000000 is an EOL, fill or damage.
                return count > 0 ? count : -1;
            }

            int vertical = Math.Clamp(b1 + delta, 0, width);
            coding[count++] = vertical;
            a0 = vertical;
            color ^= 1;
        }

        return count;
    }

    /// <summary>Reads one complete run (any number of make-up codes followed by a terminating code), or -1.</summary>
    private static int ReadRun(ref BitReader reader, int color)
    {
        int[] lookup = color == 0 ? TiffCcittTables.WhiteLookup : TiffCcittTables.BlackLookup;
        int total = 0;

        // A run needs at most a handful of make-up codes; the bound only stops damaged data from looping.
        for (int i = 0; i < 64; i++)
        {
            int entry = lookup[reader.Peek(TiffCcittTables.LookupBits)];
            int length = entry & TiffCcittTables.LengthMask;
            if (length == 0)
            {
                return -1;
            }

            reader.Skip(length);
            int run = entry >> TiffCcittTables.LengthBits;
            total += run;
            if (run < 64)
            {
                return total;
            }
        }

        return -1;
    }

    /// <summary>Paints the black spans described by <paramref name="changes"/> into an all-white bit-packed row.</summary>
    private static void FillRow(Span<byte> row, int[] changes, int count, int width)
    {
        int position = 0;
        int color = 0;
        for (int k = 0; k < count && position < width; k++)
        {
            int end = Math.Clamp(changes[k], position, width);
            if (color == 1)
            {
                SetBits(row, position, end);
            }

            position = end;
            color ^= 1;
        }

        if (color == 1 && position < width)
        {
            SetBits(row, position, width);
        }
    }

    /// <summary>Sets the bits of <paramref name="row"/> in <c>[from, to)</c>, most significant bit first.</summary>
    private static void SetBits(Span<byte> row, int from, int to)
    {
        if (from >= to)
        {
            return;
        }

        int firstByte = from >> 3;
        int lastByte = (to - 1) >> 3;
        int head = 0xFF >> (from & 7);
        int tail = 0xFF << (7 - ((to - 1) & 7));
        if (firstByte == lastByte)
        {
            row[firstByte] |= (byte)(head & tail);
            return;
        }

        row[firstByte] |= (byte)head;
        for (int i = firstByte + 1; i < lastByte; i++)
        {
            row[i] = 0xFF;
        }

        row[lastByte] |= (byte)tail;
    }

    private static void WriteEndOfLine(BitWriter writer) => writer.Write(EndOfLineCode, EndOfLineBits);

    /// <summary>Collects the positions at which a bit-packed row changes colour, the row starting white.</summary>
    private static int ExtractChanges(ReadOnlySpan<byte> row, int width, int[] changes)
    {
        int count = 0;
        int previous = 0;
        for (int x = 0; x < width; x++)
        {
            int bit = (row[x >> 3] >> (7 - (x & 7))) & 1;
            if (bit != previous)
            {
                changes[count++] = x;
                previous = bit;
            }
        }

        return count;
    }

    private static void EncodeOneDimensionalRow(BitWriter writer, int[] changes, int count, int width)
    {
        int position = 0;
        int color = 0;
        for (int k = 0; k < count; k++)
        {
            WriteRun(writer, color, changes[k] - position);
            position = changes[k];
            color ^= 1;
        }

        WriteRun(writer, color, width - position);
    }

    private static void EncodeTwoDimensionalRow(
        BitWriter writer, int[] reference, int referenceCount, int[] coding, int codingCount, int width)
    {
        int a0 = -1;
        int color = 0;
        int codingIndex = 0;
        int hint = 0;

        while (a0 < width)
        {
            while (codingIndex < codingCount && coding[codingIndex] <= a0)
            {
                codingIndex++;
            }

            int a1 = codingIndex < codingCount ? coding[codingIndex] : width;
            int a2 = codingIndex + 1 < codingCount ? coding[codingIndex + 1] : width;

            while (hint > 0 && reference[hint - 1] > a0)
            {
                hint--;
            }

            while (hint < referenceCount && reference[hint] <= a0)
            {
                hint++;
            }

            int index = hint;
            if (((index ^ color) & 1) != 0)
            {
                index++;
            }

            int b1 = index < referenceCount ? reference[index] : width;
            int b2 = index + 1 < referenceCount ? reference[index + 1] : width;

            if (b2 < a1)
            {
                writer.Write(0b0001, 4); // Pass
                a0 = b2;
                continue;
            }

            int delta = a1 - b1;
            if (delta is >= -3 and <= 3)
            {
                WriteVertical(writer, delta);
                a0 = a1;
                color ^= 1;
                continue;
            }

            writer.Write(0b001, 3); // Horizontal
            int start = a0 < 0 ? 0 : a0;
            WriteRun(writer, color, a1 - start);
            WriteRun(writer, color ^ 1, a2 - a1);
            a0 = a2;
        }
    }

    private static void WriteVertical(BitWriter writer, int delta)
    {
        switch (delta)
        {
            case 0:
                writer.Write(0b1, 1);
                break;
            case 1:
                writer.Write(0b011, 3);
                break;
            case -1:
                writer.Write(0b010, 3);
                break;
            case 2:
                writer.Write(0b000011, 6);
                break;
            case -2:
                writer.Write(0b000010, 6);
                break;
            case 3:
                writer.Write(0b0000011, 7);
                break;
            default:
                writer.Write(0b0000010, 7);
                break;
        }
    }

    private static void WriteRun(BitWriter writer, int color, int run)
    {
        string[] terminating = color == 0 ? TiffCcittTables.WhiteTerminating : TiffCcittTables.BlackTerminating;
        string[] makeup = color == 0 ? TiffCcittTables.WhiteMakeup : TiffCcittTables.BlackMakeup;

        while (run >= TiffCcittTables.MaxMakeupRun + 64)
        {
            WriteCode(writer, TiffCcittTables.ExtendedMakeup[^1]);
            run -= TiffCcittTables.MaxMakeupRun;
        }

        if (run >= 64)
        {
            int multiple = run & ~63;
            WriteCode(writer, multiple >= 1792
                ? TiffCcittTables.ExtendedMakeup[(multiple - 1792) / 64]
                : makeup[(multiple / 64) - 1]);
            run -= multiple;
        }

        WriteCode(writer, terminating[run]);
    }

    private static void WriteCode(BitWriter writer, string code) => writer.Write(TiffCcittTables.ParseCode(code), code.Length);

    private static byte[] BuildReversedBytes()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int b = i;
            b = ((b & 0xF0) >> 4) | ((b & 0x0F) << 4);
            b = ((b & 0xCC) >> 2) | ((b & 0x33) << 2);
            b = ((b & 0xAA) >> 1) | ((b & 0x55) << 1);
            table[i] = (byte)b;
        }

        return table;
    }

    /// <summary>Reads big-endian bit fields out of a coded segment, optionally reversing each byte for FillOrder 2.</summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> data;
        private readonly bool lsbFirst;
        private readonly int bitLength;

        public BitReader(ReadOnlySpan<byte> data, bool lsbFirst)
        {
            this.data = data;
            this.lsbFirst = lsbFirst;
            this.bitLength = data.Length * 8;
            this.BitPosition = 0;
        }

        /// <summary>The reader's position in bits from the start of the segment.</summary>
        public int BitPosition { readonly get; set; }

        /// <summary>True once every coded bit has been consumed.</summary>
        public readonly bool AtEnd => this.BitPosition >= this.bitLength;

        /// <summary>Returns the next <paramref name="count"/> bits (at most 14) without consuming them, zero-padded past the end.</summary>
        public readonly int Peek(int count)
        {
            int index = this.BitPosition >> 3;
            int shift = this.BitPosition & 7;
            uint window = ((uint)this.ByteAt(index) << 16) | ((uint)this.ByteAt(index + 1) << 8) | this.ByteAt(index + 2);
            return (int)((window >> (24 - shift - count)) & ((1u << count) - 1));
        }

        /// <summary>Advances the reader by <paramref name="count"/> bits.</summary>
        public void Skip(int count) => this.BitPosition += count;

        /// <summary>Advances the reader to the next byte boundary.</summary>
        public void AlignToByte() => this.BitPosition = (this.BitPosition + 7) & ~7;

        private readonly byte ByteAt(int index)
        {
            if (index < 0 || index >= this.data.Length)
            {
                return 0;
            }

            byte value = this.data[index];
            return this.lsbFirst ? ReversedBytes[value] : value;
        }
    }

    /// <summary>Accumulates big-endian bit fields into a growable byte buffer.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> bytes = new();
        private uint accumulator;
        private int bits;

        /// <summary>Appends the low <paramref name="count"/> bits of <paramref name="value"/>, most significant first.</summary>
        public void Write(int value, int count)
        {
            this.accumulator = (this.accumulator << count) | (uint)(value & ((1 << count) - 1));
            this.bits += count;
            while (this.bits >= 8)
            {
                this.bytes.Add((byte)(this.accumulator >> (this.bits - 8)));
                this.bits -= 8;
            }

            this.accumulator &= (1u << this.bits) - 1;
        }

        /// <summary>Pads with zero bits up to the next byte boundary.</summary>
        public void AlignToByte()
        {
            if (this.bits > 0)
            {
                this.Write(0, 8 - this.bits);
            }
        }

        /// <summary>Returns the coded bytes, padding the final partial byte with zeros.</summary>
        public byte[] ToArray()
        {
            this.AlignToByte();
            return this.bytes.ToArray();
        }
    }
}
