namespace EasyImageSharp.Formats.Gif;

/// <summary>
/// GIF-variant LZW as specified in GIF89a appendix F: LSB-first code packing, a variable code width that
/// starts at the minimum code size + 1 and grows up to 12 bits, explicit clear and end-of-information
/// codes, and no early change.
/// </summary>
internal static class GifLzwDecoder
{
    private const int MaxCodeBits = 12;
    private const int MaxTableSize = 1 << MaxCodeBits;

    /// <summary>
    /// Decodes a concatenated image-data payload into <paramref name="output"/> and returns the number of
    /// pixel indices produced. Decoding stops at the end-of-information code, when the input is exhausted
    /// or when <paramref name="output"/> is full, whichever comes first; a short return value therefore
    /// means the stream ended early rather than that it was corrupt.
    /// </summary>
    /// <param name="input">The de-blocked LZW code stream.</param>
    /// <param name="minCodeSize">The LZW minimum code size, in the range 1 to 11.</param>
    /// <param name="output">Receives one palette index per pixel.</param>
    /// <exception cref="InvalidImageContentException">The code stream is corrupt.</exception>
    public static int Decode(ReadOnlySpan<byte> input, int minCodeSize, Span<byte> output)
    {
        if (minCodeSize < 1 || minCodeSize >= MaxCodeBits)
        {
            throw new ArgumentOutOfRangeException(nameof(minCodeSize), minCodeSize, "GIF LZW minimum code size must be between 1 and 11.");
        }

        int clearCode = 1 << minCodeSize;
        int eoiCode = clearCode + 1;
        int firstFreeCode = clearCode + 2;
        int initialCodeSize = minCodeSize + 1;

        int codeSize = initialCodeSize;
        int nextCode = firstFreeCode;
        int prevCode = -1;
        int firstChar = 0; // First byte of the string most recently emitted (the string of prevCode).

        // Every table entry is its prefix entry's string followed by one byte; literals have no prefix.
        var prefix = new ushort[MaxTableSize];
        var suffix = new byte[MaxTableSize];
        var stack = new byte[MaxTableSize + 1];

        uint bitBuffer = 0;
        int bitCount = 0;
        int inPos = 0;
        int outPos = 0;

        while (outPos < output.Length)
        {
            // Read the next code (LSB-first).
            while (bitCount < codeSize)
            {
                if (inPos >= input.Length)
                {
                    return outPos; // Input exhausted.
                }

                bitBuffer |= (uint)input[inPos++] << bitCount;
                bitCount += 8;
            }

            int code = (int)(bitBuffer & ((1u << codeSize) - 1));
            bitBuffer >>= codeSize;
            bitCount -= codeSize;

            if (code == clearCode)
            {
                codeSize = initialCodeSize;
                nextCode = firstFreeCode;
                prevCode = -1;
                continue;
            }

            if (code == eoiCode)
            {
                break;
            }

            if (prevCode == -1)
            {
                // The first code after a clear must be a literal.
                if (code >= clearCode)
                {
                    throw new InvalidImageContentException("Corrupt GIF LZW data: the first code after a clear is not a literal.");
                }

                output[outPos++] = (byte)code;
                prevCode = code;
                firstChar = code;
                continue;
            }

            int stackTop = 0;
            int current;
            if (code < nextCode)
            {
                current = code;
            }
            else if (code == nextCode)
            {
                // KwKwK case: the string is prevCode's string followed by its own first byte.
                stack[stackTop++] = (byte)firstChar;
                current = prevCode;
            }
            else
            {
                throw new InvalidImageContentException("Corrupt GIF LZW data: code out of range.");
            }

            // Walk the prefix chain down to the literal, collecting bytes in reverse.
            while (current >= firstFreeCode)
            {
                stack[stackTop++] = suffix[current];
                current = prefix[current];
            }

            firstChar = current;
            stack[stackTop++] = (byte)current;

            while (stackTop > 0 && outPos < output.Length)
            {
                output[outPos++] = stack[--stackTop];
            }

            if (nextCode < MaxTableSize)
            {
                prefix[nextCode] = (ushort)prevCode;
                suffix[nextCode] = (byte)firstChar;
                nextCode++;

                if (nextCode == (1 << codeSize) && codeSize < MaxCodeBits)
                {
                    codeSize++;
                }
            }

            prevCode = code;
        }

        return outPos;
    }
}
