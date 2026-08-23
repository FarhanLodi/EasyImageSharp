using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyImageSharp.Formats.Jpeg;

/// <summary>
/// MSB-first entropy-coded segment writer: bits accumulate in a 64-bit register, complete bytes are copied
/// (with 0xFF00 stuffing) into a pooled buffer that is handed to the target stream in large chunks. Never
/// touches the stream for individual bits or bytes. Also hosts the baseline block coder, which keeps the bit
/// register in locals for the duration of a block.
/// </summary>
internal sealed class JpegBitWriter : IDisposable
{
    private const int BufferSize = 1 << 17;

    // The buffer is flushed to the stream when fewer than this many bytes are free, so a whole 8-byte register
    // (16 bytes worst case when every byte needs stuffing) plus a marker always fits without a check per byte.
    private const int FlushThreshold = 64;

    private readonly Stream stream;
    private byte[] buffer;
    private int position;

    /// <summary>Bits waiting to be written, left-aligned: the top (64 - freeBits) bits are valid.</summary>
    private ulong register;
    private int freeBits = 64;

    public JpegBitWriter(Stream stream)
    {
        this.stream = stream;
        this.buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    }

    /// <summary>Appends the low <paramref name="size"/> bits of <paramref name="code"/> (1..32 bits).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBits(uint code, int size)
    {
        ulong reg = this.register;
        int free = this.freeBits;
        Put(ref reg, ref free, code, size);
        this.register = reg;
        this.freeBits = free;
    }

    /// <summary>Appends a Huffman code immediately followed by <paramref name="valueBits"/> extra bits (total at most 32).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteCodeAndBits(uint code, int codeSize, uint value, int valueBits)
        => this.WriteBits((code << valueBits) | (value & ((1u << valueBits) - 1)), codeSize + valueBits);

    /// <summary>
    /// Huffman-codes one baseline block (64 quantised coefficients in zigzag order) per T.81 F.1.2: the DC
    /// difference category and bits, then AC run/size pairs with ZRL and EOB. Lookup entries are
    /// <c>(code &lt;&lt; 8) | length</c> as in <see cref="JpegHuffmanTable.Lookup"/>.
    /// </summary>
    public void EncodeSequentialBlock(ReadOnlySpan<short> block, ref int predictor, int[] dcLookup, int[] acLookup)
    {
        if (block.Length < 64 || dcLookup.Length < 12 || acLookup.Length < 256)
        {
            throw new ArgumentException("Block and Huffman lookups have unexpected sizes.");
        }

        ref short b = ref MemoryMarshal.GetReference(block);
        ulong reg = this.register;
        int free = this.freeBits;

        // DC: category (bit length of |diff|) followed by the diff bits (one's complement for negatives).
        int dc = b;
        int diff = dc - predictor;
        predictor = dc;
        int magnitude = diff;
        int bits = diff;
        if (diff < 0)
        {
            magnitude = -diff;
            bits = diff - 1;
        }

        int nbits = 32 - BitOperations.LeadingZeroCount((uint)magnitude);
        int entry = dcLookup[nbits];
        Put(ref reg, ref free, (((uint)entry >> 8) << nbits) | ((uint)bits & ((1u << nbits) - 1)), (entry & 0xFF) + nbits);

        // AC: find the last nonzero coefficient so the run loop stops early and a single EOB covers the tail.
        int last = 63;
        while (last > 0 && Unsafe.Add(ref b, last) == 0)
        {
            last--;
        }

        int run = 0;
        for (int k = 1; k <= last; k++)
        {
            int value = Unsafe.Add(ref b, k);
            if (value == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                int zrl = acLookup[0xF0];
                Put(ref reg, ref free, (uint)zrl >> 8, zrl & 0xFF);
                run -= 16;
            }

            magnitude = value;
            bits = value;
            if (value < 0)
            {
                magnitude = -value;
                bits = value - 1;
            }

            nbits = 32 - BitOperations.LeadingZeroCount((uint)magnitude);
            entry = acLookup[(run << 4) | nbits];
            Put(ref reg, ref free, (((uint)entry >> 8) << nbits) | ((uint)bits & ((1u << nbits) - 1)), (entry & 0xFF) + nbits);
            run = 0;
        }

        if (last < 63)
        {
            int eob = acLookup[0x00];
            Put(ref reg, ref free, (uint)eob >> 8, eob & 0xFF);
        }

        this.register = reg;
        this.freeBits = free;
    }

    /// <summary>Pads the pending bits to a byte boundary with 1-bits and writes them (T.81 F.1.2.3).</summary>
    public void AlignToByte()
    {
        int used = 64 - this.freeBits;
        if (used == 0)
        {
            return;
        }

        ulong value = this.register | ((1UL << this.freeBits) - 1);
        int byteCount = (used + 7) >> 3;
        this.EnsureCapacity();
        for (int i = 0; i < byteCount; i++)
        {
            byte b = (byte)(value >> (56 - (i * 8)));
            this.buffer[this.position++] = b;
            if (b == 0xFF)
            {
                this.buffer[this.position++] = 0x00;
            }
        }

        this.register = 0;
        this.freeBits = 64;
    }

    /// <summary>Byte-aligns and writes a marker (0xFF followed by <paramref name="marker"/>) without stuffing.</summary>
    public void WriteMarker(byte marker)
    {
        this.AlignToByte();
        this.EnsureCapacity();
        this.buffer[this.position++] = 0xFF;
        this.buffer[this.position++] = marker;
    }

    /// <summary>Byte-aligns and copies the buffered bytes to the stream. Call once at the end of every entropy-coded segment.</summary>
    public void Flush()
    {
        this.AlignToByte();
        this.FlushBuffer();
    }

    public void Dispose()
    {
        byte[] rented = this.buffer;
        if (rented.Length != 0)
        {
            this.buffer = Array.Empty<byte>();
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Appends <paramref name="size"/> bits (1..32) to a register held in locals, spilling to the buffer when it fills.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Put(ref ulong reg, ref int free, uint code, int size)
    {
        free -= size;
        if (free >= 0)
        {
            reg |= (ulong)code << free;
            return;
        }

        // The register overflows: top up its remaining free bits, emit all eight bytes, start over with the rest.
        reg |= (ulong)code >> -free;
        this.EmitRegister(reg);
        free += 64;
        reg = (ulong)code << free;
    }

    /// <summary>Emits all eight bytes of <paramref name="value"/>, most significant first, stuffing 0x00 after any 0xFF.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EmitRegister(ulong value)
    {
        this.EnsureCapacity();
        byte[] buf = this.buffer;
        int pos = this.position;

        // Fast path: no byte equals 0xFF, so nothing needs stuffing. A byte of ~value is zero exactly when the
        // corresponding byte of value is 0xFF (the classic has-zero-byte test).
        ulong inverted = ~value;
        if (((inverted - 0x0101010101010101UL) & ~inverted & 0x8080808080808080UL) == 0)
        {
            BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(pos, 8), value);
            this.position = pos + 8;
            return;
        }

        for (int shift = 56; shift >= 0; shift -= 8)
        {
            byte b = (byte)(value >> shift);
            buf[pos++] = b;
            if (b == 0xFF)
            {
                buf[pos++] = 0x00;
            }
        }

        this.position = pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity()
    {
        if (this.position > this.buffer.Length - FlushThreshold)
        {
            this.FlushBuffer();
        }
    }

    private void FlushBuffer()
    {
        if (this.position > 0)
        {
            this.stream.Write(this.buffer, 0, this.position);
            this.position = 0;
        }
    }
}
