namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// Least-significant-bit-first bit writer for the VP8L (lossless) bitstream, the exact counterpart of
/// <see cref="Vp8LBitReader"/>: bits fill each byte from its low end and multi-bit fields are little-endian.
/// The trailing partial byte is zero-padded on <see cref="ToArray"/>.
/// </summary>
internal sealed class Vp8LBitWriter
{
    private byte[] buffer;
    private int count;
    private ulong accumulator;
    private int used;

    /// <summary>Creates a writer with room for <paramref name="capacity"/> bytes before the first growth.</summary>
    public Vp8LBitWriter(int capacity = 4096) => this.buffer = new byte[Math.Max(16, capacity)];

    /// <summary>The number of bits written so far, including the bits still sitting in the accumulator.</summary>
    public long BitPosition => ((long)this.count * 8) + this.used;

    /// <summary>The number of bytes the stream occupies once padded to a byte boundary.</summary>
    public int ByteLength => this.count + ((this.used + 7) >> 3);

    /// <summary>Writes the low <paramref name="bits"/> bits of <paramref name="value"/>, least significant first.</summary>
    /// <param name="value">The value to write; bits above <paramref name="bits"/> are ignored.</param>
    /// <param name="bits">How many bits to write, 0 to 32.</param>
    public void PutBits(uint value, int bits)
    {
        if (bits == 0)
        {
            return;
        }

        ulong masked = bits >= 32 ? value : value & ((1u << bits) - 1);
        this.accumulator |= masked << this.used;
        this.used += bits;
        while (this.used >= 8)
        {
            this.Append((byte)this.accumulator);
            this.accumulator >>= 8;
            this.used -= 8;
        }
    }

    /// <summary>Appends every bit of <paramref name="other"/> to this writer.</summary>
    public void Append(Vp8LBitWriter other)
    {
        byte[] bytes = other.PeekBytes(out int fullBytes, out uint tail, out int tailBits);
        for (int i = 0; i < fullBytes; i++)
        {
            this.PutBits(bytes[i], 8);
        }

        if (tailBits > 0)
        {
            this.PutBits(tail, tailBits);
        }
    }

    /// <summary>Returns the finished, byte-aligned bitstream.</summary>
    public byte[] ToArray()
    {
        int length = this.ByteLength;
        var result = new byte[length];
        Array.Copy(this.buffer, result, this.count);
        if (this.used > 0)
        {
            result[this.count] = (byte)this.accumulator;
        }

        return result;
    }

    private byte[] PeekBytes(out int fullBytes, out uint tail, out int tailBits)
    {
        fullBytes = this.count;
        tail = (uint)this.accumulator;
        tailBits = this.used;
        return this.buffer;
    }

    private void Append(byte value)
    {
        if (this.count == this.buffer.Length)
        {
            Array.Resize(ref this.buffer, this.buffer.Length * 2);
        }

        this.buffer[this.count++] = value;
    }
}
