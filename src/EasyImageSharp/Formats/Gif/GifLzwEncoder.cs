namespace EasyImageSharp.Formats.Gif;

/// <summary>
/// GIF-variant LZW compression (GIF89a appendix F): variable code width from minimum code size + 1 up to 12
/// bits, LSB-first bit packing, an initial clear code, a clear code whenever the 4096-entry table fills, and the
/// end-of-information code last. Output is written as data sub-blocks of at most 255 bytes followed by the block
/// terminator. The code-width schedule mirrors <see cref="GifLzwDecoder"/> exactly (no "early change").
/// </summary>
internal static class GifLzwEncoder
{
    private const int MaxBits = 12;
    private const int MaxCodes = 1 << MaxBits;
    private const int HashBits = 13;
    private const int HashSize = 1 << HashBits;

    /// <summary>Compresses one palette index per byte and writes the sub-block sequence (including the terminator).</summary>
    public static void Encode(ReadOnlySpan<byte> indices, int minCodeSize, Stream output)
    {
        if (minCodeSize is < 2 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(minCodeSize), minCodeSize, "The GIF LZW minimum code size must be between 2 and 8.");
        }

        int clearCode = 1 << minCodeSize;
        int eoiCode = clearCode + 1;
        int firstFreeCode = clearCode + 2;
        int codeSize = minCodeSize + 1;
        int nextCode = firstFreeCode;

        // Open-addressed hash of (prefix code, next byte) -> code.
        var hashKeys = new int[HashSize];
        var hashCodes = new int[HashSize];
        Array.Fill(hashKeys, -1);

        var writer = new SubBlockBitWriter(output);
        writer.WriteCode(clearCode, codeSize);

        if (indices.IsEmpty)
        {
            writer.WriteCode(eoiCode, codeSize);
            writer.Finish();
            return;
        }

        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            int next = indices[i];
            int key = (prefix << 8) | next;
            int slot = (int)((uint)key * 2654435761u >> (32 - HashBits));
            bool found = false;
            while (hashKeys[slot] >= 0)
            {
                if (hashKeys[slot] == key)
                {
                    prefix = hashCodes[slot];
                    found = true;
                    break;
                }

                slot = (slot + 1) & (HashSize - 1);
            }

            if (found)
            {
                continue;
            }

            writer.WriteCode(prefix, codeSize);
            if (nextCode >= (1 << codeSize) && codeSize < MaxBits)
            {
                codeSize++; // The decoder widens after adding the entry that fills the current range.
            }

            if (nextCode < MaxCodes)
            {
                hashKeys[slot] = key;
                hashCodes[slot] = nextCode;
                nextCode++;
            }
            else
            {
                writer.WriteCode(clearCode, codeSize);
                codeSize = minCodeSize + 1;
                nextCode = firstFreeCode;
                Array.Fill(hashKeys, -1);
            }

            prefix = next;
        }

        writer.WriteCode(prefix, codeSize);
        if (nextCode >= (1 << codeSize) && codeSize < MaxBits)
        {
            codeSize++;
        }

        writer.WriteCode(eoiCode, codeSize);
        writer.Finish();
    }

    /// <summary>Packs codes LSB-first and emits them as 255-byte data sub-blocks.</summary>
    private sealed class SubBlockBitWriter
    {
        private readonly Stream output;
        private readonly byte[] block = new byte[256]; // block[0] holds the length.
        private int blockLength;
        private uint bitBuffer;
        private int bitCount;

        public SubBlockBitWriter(Stream output) => this.output = output;

        public void WriteCode(int code, int codeSize)
        {
            this.bitBuffer |= (uint)code << this.bitCount;
            this.bitCount += codeSize;
            while (this.bitCount >= 8)
            {
                this.WriteByte((byte)this.bitBuffer);
                this.bitBuffer >>= 8;
                this.bitCount -= 8;
            }
        }

        public void Finish()
        {
            if (this.bitCount > 0)
            {
                this.WriteByte((byte)this.bitBuffer);
                this.bitBuffer = 0;
                this.bitCount = 0;
            }

            this.FlushBlock();
            this.output.WriteByte(0); // Block terminator.
        }

        private void WriteByte(byte value)
        {
            this.block[1 + this.blockLength] = value;
            this.blockLength++;
            if (this.blockLength == 255)
            {
                this.FlushBlock();
            }
        }

        private void FlushBlock()
        {
            if (this.blockLength == 0)
            {
                return;
            }

            this.block[0] = (byte)this.blockLength;
            this.output.Write(this.block, 0, this.blockLength + 1);
            this.blockLength = 0;
        }
    }
}
