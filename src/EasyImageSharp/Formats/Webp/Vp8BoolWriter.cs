using System.Numerics;

namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// The VP8 boolean entropy encoder (RFC 6386 section 7.3), the exact inverse of <see cref="Vp8BitReader"/>.
/// </summary>
/// <remarks>
/// The coder keeps a range (stored biased by one, exactly as the decoder does), a value accumulator and a
/// count of bits pending in that accumulator. Renormalisation shifts whole bytes out; because a shifted-out
/// byte can still be incremented by a later carry, runs of <c>0xff</c> are held back in <see cref="run"/>
/// until it is known whether the carry reaches them.
/// </remarks>
internal sealed class Vp8BoolWriter
{
    private byte[] buffer;
    private int pos;
    private int range = 255 - 1;
    private int value;
    private int nbBits = -8;
    private int run;

    /// <summary>Initialises a writer whose buffer starts at <paramref name="expectedSize"/> bytes.</summary>
    public Vp8BoolWriter(int expectedSize) => this.buffer = new byte[Math.Max(expectedSize, 256)];

    /// <summary>Number of bytes written so far.</summary>
    public int Length => this.pos;

    /// <summary>The backing buffer; only the first <see cref="Length"/> bytes are meaningful.</summary>
    public byte[] Buffer => this.buffer;

    /// <summary>Encodes one boolean whose probability of being zero is <paramref name="prob"/>/256.</summary>
    public void PutBit(int bit, int prob)
    {
        int split = (this.range * prob) >> 8;
        if (bit != 0)
        {
            this.value += split + 1;
            this.range -= split + 1;
        }
        else
        {
            this.range = split;
        }

        if (this.range < 127)
        {
            int shift = 7 - BitOperations.Log2((uint)(this.range + 1));
            this.range = ((this.range + 1) << shift) - 1;
            this.value <<= shift;
            this.nbBits += shift;
            if (this.nbBits > 0)
            {
                this.Flush();
            }
        }
    }

    /// <summary>Encodes one boolean with even probability.</summary>
    public void PutFlag(bool bit) => this.PutBit(bit ? 1 : 0, 0x80);

    /// <summary>Encodes an unsigned <paramref name="count"/>-bit literal, most significant bit first.</summary>
    public void PutValue(int literal, int count)
    {
        for (int mask = 1 << (count - 1); mask != 0; mask >>= 1)
        {
            this.PutBit((literal & mask) != 0 ? 1 : 0, 0x80);
        }
    }

    /// <summary>Encodes a <paramref name="count"/>-bit magnitude followed by a sign bit.</summary>
    public void PutSignedValue(int signedValue, int count)
    {
        this.PutValue(Math.Abs(signedValue), count);
        this.PutBit(signedValue < 0 ? 1 : 0, 0x80);
    }

    /// <summary>Encodes an optional signed field: a presence flag then, when non-zero, the value.</summary>
    public void PutOptionalSigned(int signedValue, int count)
    {
        if (signedValue != 0)
        {
            this.PutBit(1, 0x80);
            this.PutSignedValue(signedValue, count);
        }
        else
        {
            this.PutBit(0, 0x80);
        }
    }

    /// <summary>Flushes the pending bits and returns the encoded partition.</summary>
    public byte[] Finish()
    {
        this.PutValue(0, 9 - this.nbBits);
        this.nbBits = 0; // Pad the tail with zeroes.
        this.Flush();
        byte[] result = new byte[this.pos];
        Array.Copy(this.buffer, result, this.pos);
        return result;
    }

    private void Flush()
    {
        int s = 8 + this.nbBits;
        int bits = this.value >> s;
        this.value -= bits << s;
        this.nbBits -= 8;
        if ((bits & 0xff) != 0xff)
        {
            bool carry = (bits & 0x100) != 0;
            if (carry && this.pos > 0)
            {
                this.buffer[this.pos - 1]++;
            }

            if (this.run > 0)
            {
                // The pending 0xff bytes become 0x00 when the carry ripples through them.
                byte fill = carry ? (byte)0x00 : (byte)0xff;
                this.Reserve(this.run + 1);
                for (; this.run > 0; this.run--)
                {
                    this.buffer[this.pos++] = fill;
                }
            }
            else
            {
                this.Reserve(1);
            }

            this.buffer[this.pos++] = (byte)(bits & 0xff);
        }
        else
        {
            // Hold the byte back: a later carry would turn it into 0x00 and bump its predecessor.
            this.run++;
        }
    }

    private void Reserve(int extra)
    {
        if (this.pos + extra <= this.buffer.Length)
        {
            return;
        }

        int size = Math.Max(this.buffer.Length * 2, this.pos + extra + 256);
        Array.Resize(ref this.buffer, size);
    }
}
