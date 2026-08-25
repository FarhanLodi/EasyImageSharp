namespace EasyImageSharp.Formats.Tiff;

/// <summary>
/// TIFF-variant LZW (MSB-first code packing with early code-size change), as specified in
/// TIFF 6.0 section 13.
/// </summary>
internal static class TiffLzw
{
    private const int ClearCode = 256;
    private const int EoiCode = 257;
    private const int FirstCode = 258;
    private const int MaxTableSize = 4096;

    /// <summary>
    /// Decodes one strip or tile into <paramref name="output"/>, which must be filled completely; a stream
    /// that ends (or signals EOI) early is reported as <see cref="InvalidImageContentException"/>.
    /// </summary>
    public static void Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int expectedSize = output.Length;
        int outPos = 0;

        var prefix = new int[MaxTableSize];
        var suffix = new byte[MaxTableSize];
        var stack = new byte[MaxTableSize];

        int codeSize = 9;
        int nextCode = FirstCode;
        int oldCode = -1;

        ulong bitBuffer = 0;
        int bitCount = 0;
        int inPos = 0;

        while (outPos < expectedSize)
        {
            // Read the next code (MSB-first).
            while (bitCount < codeSize && inPos < input.Length)
            {
                bitBuffer = (bitBuffer << 8) | input[inPos++];
                bitCount += 8;
            }

            if (bitCount < codeSize)
            {
                break; // Input exhausted.
            }

            int code = (int)((bitBuffer >> (bitCount - codeSize)) & (ulong)((1 << codeSize) - 1));
            bitCount -= codeSize;

            if (code == EoiCode)
            {
                break;
            }

            if (code == ClearCode)
            {
                codeSize = 9;
                nextCode = FirstCode;
                oldCode = -1;
                continue;
            }

            int stackTop = 0;
            if (oldCode == -1)
            {
                if (code >= ClearCode)
                {
                    throw new InvalidImageContentException("Corrupt TIFF LZW data: first code is not a literal.");
                }

                output[outPos++] = (byte)code;
                oldCode = code;
                continue;
            }

            int current = code;
            if (code >= nextCode)
            {
                if (code > nextCode)
                {
                    throw new InvalidImageContentException("Corrupt TIFF LZW data: code out of range.");
                }

                // KwKwK case: emit oldCode's string followed by its first byte.
                current = oldCode;
                stack[stackTop++] = FirstByteOf(current, prefix, suffix);
            }

            while (current >= FirstCode)
            {
                stack[stackTop++] = suffix[current];
                current = prefix[current];
            }

            stack[stackTop++] = (byte)current;

            // Emit in reverse.
            while (stackTop > 0 && outPos < expectedSize)
            {
                output[outPos++] = stack[--stackTop];
            }

            if (nextCode < MaxTableSize)
            {
                prefix[nextCode] = oldCode;
                suffix[nextCode] = FirstByteOf(code >= nextCode ? oldCode : code, prefix, suffix);
                nextCode++;

                // TIFF early change (matches libtiff behaviour): widen as soon as the
                // next entry to create equals the last representable code of the current width.
                if (nextCode == (1 << codeSize) - 1 && codeSize < 12)
                {
                    codeSize++;
                }
            }

            oldCode = code;
        }

        if (outPos < expectedSize)
        {
            throw new InvalidImageContentException("TIFF LZW data ended before the strip was complete.");
        }
    }

    private static byte FirstByteOf(int code, int[] prefix, byte[] suffix)
    {
        while (code >= FirstCode)
        {
            code = prefix[code];
        }

        return (byte)code;
    }

    public static byte[] Encode(ReadOnlySpan<byte> input)
    {
        using var output = new MemoryStream();
        var table = new Dictionary<(int Prefix, byte Value), int>();

        int codeSize = 9;
        int nextCode = FirstCode;
        uint bitBuffer = 0;
        int bitCount = 0;

        void WriteCode(int code)
        {
            bitBuffer = (bitBuffer << codeSize) | (uint)code;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                output.WriteByte((byte)(bitBuffer >> (bitCount - 8)));
                bitCount -= 8;
            }

            bitBuffer &= (1u << bitCount) - 1;
        }

        WriteCode(ClearCode);
        int prefix = -1;
        foreach (byte b in input)
        {
            if (prefix == -1)
            {
                prefix = b;
                continue;
            }

            if (table.TryGetValue((prefix, b), out int existing))
            {
                prefix = existing;
                continue;
            }

            WriteCode(prefix);
            table[(prefix, b)] = nextCode++;

            // The encoder's entry counter runs one ahead of the decoder's, so it widens one
            // entry after the decoder's (1 << codeSize) - 1 switch point.
            if (nextCode == (1 << codeSize) && codeSize < 12)
            {
                codeSize++;
            }

            if (nextCode >= MaxTableSize - 2)
            {
                WriteCode(ClearCode);
                table.Clear();
                codeSize = 9;
                nextCode = FirstCode;
            }

            prefix = b;
        }

        if (prefix != -1)
        {
            WriteCode(prefix);
        }

        WriteCode(EoiCode);
        if (bitCount > 0)
        {
            output.WriteByte((byte)(bitBuffer << (8 - bitCount)));
        }

        return output.ToArray();
    }
}
