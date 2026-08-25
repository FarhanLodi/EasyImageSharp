using System.Buffers.Binary;
using System.Numerics;

namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// The VP8 boolean entropy decoder (RFC 6386 section 7), implemented with a 64-bit value register that is
/// refilled 56 bits at a time. Reading past the end of the partition feeds zero bits and raises
/// <see cref="Eof"/>, which the frame decoder turns into a truncation error, mirroring the reference decoder.
/// </summary>
internal sealed class Vp8BitReader
{
    private const int LoadBits = 56;

    private readonly byte[] buffer;
    private readonly int end;
    private int pos;
    private ulong value;
    private uint range;
    private int bits;

    public Vp8BitReader(byte[] buffer, int start, int length)
    {
        this.buffer = buffer;
        this.pos = start;
        this.end = start + length;
        this.range = 255 - 1;
        this.value = 0;
        this.bits = -8;
        this.Eof = false;
        this.LoadNewBytes();
    }

    /// <summary>True once the decoder has needed bits beyond the end of the partition.</summary>
    public bool Eof { get; private set; }

    /// <summary>Decodes one boolean whose probability of being 0 is <paramref name="prob"/>/256.</summary>
    public int GetBit(int prob)
    {
        uint range = this.range;
        if (this.bits < 0)
        {
            this.LoadNewBytes();
        }

        int pos = this.bits;
        uint split = (range * (uint)prob) >> 8;
        uint value = (uint)(this.value >> pos);
        int bit;
        if (value > split)
        {
            range -= split;
            this.value -= (ulong)(split + 1) << pos;
            bit = 1;
        }
        else
        {
            range = split + 1;
            bit = 0;
        }

        int shift = 7 ^ BitOperations.Log2(range);
        range <<= shift;
        this.bits -= shift;
        this.range = range - 1;
        return bit;
    }

    /// <summary>Reads a bit with even probability.</summary>
    public int GetFlag() => this.GetBit(0x80);

    /// <summary>Reads an unsigned <paramref name="count"/>-bit literal, most significant bit first.</summary>
    public int GetValue(int count)
    {
        int v = 0;
        while (count-- > 0)
        {
            v |= this.GetBit(0x80) << count;
        }

        return v;
    }

    /// <summary>Reads a magnitude followed by a sign bit.</summary>
    public int GetSignedValue(int count)
    {
        int value = this.GetValue(count);
        return this.GetBit(0x80) != 0 ? -value : value;
    }

    /// <summary>Applies a sign bit to an already decoded magnitude.</summary>
    public int GetSigned(int magnitude) => this.GetBit(0x80) != 0 ? -magnitude : magnitude;

    private void LoadNewBytes()
    {
        if (this.pos + 8 <= this.end)
        {
            ulong inBits = BinaryPrimitives.ReadUInt64BigEndian(this.buffer.AsSpan(this.pos, 8)) >> (64 - LoadBits);
            this.pos += LoadBits >> 3;
            this.value = inBits | (this.value << LoadBits);
            this.bits += LoadBits;
        }
        else
        {
            this.LoadFinalBytes();
        }
    }

    private void LoadFinalBytes()
    {
        if (this.pos < this.end)
        {
            this.bits += 8;
            this.value = this.buffer[this.pos++] | (this.value << 8);
        }
        else if (!this.Eof)
        {
            this.value <<= 8;
            this.bits += 8;
            this.Eof = true;
        }
        else
        {
            this.bits = 0; // Keep shifts well defined once the stream is exhausted.
        }
    }
}
