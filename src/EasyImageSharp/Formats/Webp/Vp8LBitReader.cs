namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// Least-significant-bit-first bit reader used by the VP8L (lossless) bitstream. Bytes are consumed in
/// order and bits are taken from the low end of each byte, so multi-bit fields are little-endian.
/// Consuming more bits than the buffer holds is a bitstream error and throws
/// <see cref="InvalidImageContentException"/>; peeking past the end yields zero bits, as in the reference decoder.
/// </summary>
internal sealed class Vp8LBitReader
{
    private readonly byte[] data;
    private readonly int end;
    private int pos;
    private ulong window;
    private int bitsInWindow;

    public Vp8LBitReader(byte[] data, int start, int length)
    {
        this.data = data;
        this.pos = start;
        this.end = start + length;
    }

    /// <summary>Returns the next <paramref name="count"/> bits (at most 32) without consuming them.</summary>
    public uint Peek(int count)
    {
        if (this.bitsInWindow < count)
        {
            this.Refill();
        }

        return (uint)(this.window & ((1UL << count) - 1));
    }

    /// <summary>Consumes <paramref name="count"/> bits (at most 32).</summary>
    public void Skip(int count)
    {
        if (this.bitsInWindow < count)
        {
            this.Refill();
            if (this.bitsInWindow < count)
            {
                throw new InvalidImageContentException("VP8L bitstream is truncated.");
            }
        }

        this.window >>= count;
        this.bitsInWindow -= count;
    }

    /// <summary>Reads <paramref name="count"/> bits (at most 32) as an unsigned little-endian value.</summary>
    public uint ReadBits(int count)
    {
        uint value = this.Peek(count);
        this.Skip(count);
        return value;
    }

    public bool ReadBit() => this.ReadBits(1) != 0;

    private void Refill()
    {
        while (this.bitsInWindow <= 56 && this.pos < this.end)
        {
            this.window |= (ulong)this.data[this.pos++] << this.bitsInWindow;
            this.bitsInWindow += 8;
        }
    }
}
