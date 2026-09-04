using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace EasyImageSharp;

/// <summary>What a call to <see cref="Inflater.Fill"/> was able to do.</summary>
internal enum InflateStatus
{
    /// <summary>
    /// The requested byte count is not available and the current input segment is spent. Whatever
    /// <see cref="Inflater.Available"/> reports may still be taken; hand the next segment to
    /// <see cref="Inflater.SetInput"/> and call again.
    /// </summary>
    NeedInput,

    /// <summary>The requested byte count is buffered and can be taken.</summary>
    Output,

    /// <summary>
    /// The stream ended before the requested byte count could be produced. Anything
    /// <see cref="Inflater.Available"/> still reports is real output; there will never be more.
    /// </summary>
    Finished,
}

/// <summary>
/// A resumable DEFLATE (RFC 1951) and zlib (RFC 1950) decompressor that is pushed input in segments and hands
/// output back as spans into its own window, so a caller that consumes the stream in rows never copies.
/// <para>
/// The window is one contiguous buffer with a 32 KiB history prefix: output is written linearly from offset
/// 32768, and because every legal distance is at most 32768 and the write head never passes the buffer end,
/// the source of every back-reference is already resident at <c>head - distance</c>. There is no ring wrap and
/// no modulo in the copy loop. When the buffer fills, the trailing history is moved to the front once and the
/// head resets - roughly half a 32 KiB memmove per 64 KiB emitted.
/// </para>
/// <para>
/// Bits come from a 64-bit LSB-first accumulator. While at least eight input bytes and
/// <see cref="FastOutputSlack"/> bytes of output room are in hand, one branchless refill per iteration
/// guarantees 56 bits, which covers a literal/length code (15) plus its extra bits (5) plus a distance code
/// (15) plus its extra bits (13); outside that window a byte-at-a-time refill runs. The accumulator
/// deliberately reads ahead: bits above <c>bitCount</c> are either zero or already equal the stream bits that
/// follow the read position, so re-OR-ing a byte a wide read had already pulled in is idempotent, and at a
/// segment boundary - where the read position has reached the end of the segment - nothing but zeroes can
/// remain above <c>bitCount</c>. That is what makes a fresh <see cref="SetInput"/> safe without snapshotting
/// the reader.
/// </para>
/// <para>
/// Nothing is consumed until a whole symbol is available: a code is peeked with whatever bits are buffered and
/// the table entry's own length decides whether it can be taken, so re-entry at symbol granularity is free.
/// Everything coarser is an explicit state machine, and a match interrupted by the end of the emit region
/// records its <em>relative</em> distance, never a window index, so a rewind between calls is invisible to it.
/// </para>
/// <para>
/// Malformed input always raises <see cref="InvalidImageContentException"/> carrying the caller's label; no
/// framework exception escapes it. Every buffer is fixed at construction, every state transition either
/// consumes a bit, emits a byte or reports <see cref="InflateStatus.NeedInput"/>, and every write is bounded by
/// the window, so no input can make the decoder allocate without bound, spin, or write out of range.
/// </para>
/// </summary>
internal ref struct Inflater
{
    /// <summary>The largest distance DEFLATE can express, and therefore the history that must stay resident.</summary>
    private const int WindowSize = 32768;

    /// <summary>The longest match DEFLATE can express.</summary>
    private const int MaxMatch = 258;

    /// <summary>Smallest emit region, so that even a one-byte stride hint leaves room for long matches.</summary>
    private const int MinEmitCapacity = 64 * 1024;

    /// <summary>Largest emit region; a stride above this is served through <see cref="ReadInto"/>.</summary>
    private const int MaxEmitCapacity = 1024 * 1024;

    /// <summary>Bytes the widest copy tier may write past the end of a match, and its step size.</summary>
    private const int CopyOverrun = 16;

    /// <summary>Input bytes the branchless refill needs in hand.</summary>
    private const int FastInputSlack = 8;

    /// <summary>Output bytes the fast loop needs in hand: one maximal match plus the copy overrun.</summary>
    private const int FastOutputSlack = MaxMatch + CopyOverrun;

    /// <summary>Index mask for the root of a literal/length table.</summary>
    private const ulong LitLenRootMask = (1UL << InflateTables.LitLenRootBits) - 1;

    /// <summary>Index mask for the root of a distance table.</summary>
    private const ulong DistRootMask = (1UL << InflateTables.DistRootBits) - 1;

    /// <summary>Index mask for the root of a code-length table.</summary>
    private const ulong CodeLengthRootMask = (1UL << InflateTables.CodeLengthRootBits) - 1;

    /// <summary>Largest literal/length count a dynamic block may declare.</summary>
    private const int MaxDeclaredLitLen = 286;

    /// <summary>Largest distance count a dynamic block may declare.</summary>
    private const int MaxDeclaredDist = 30;

    /// <summary>Code lengths a dynamic header can spell out: both alphabets at their widest.</summary>
    private const int MaxDeclaredLengths = InflateTables.MaxLitLenSymbols + InflateTables.MaxDistSymbols;

    private readonly string label;
    private readonly bool expectZlibHeader;
    private readonly int emitCapacity;
    private readonly int capacity;

    private readonly byte[] window;
    private readonly byte[] lengths;
    private readonly uint[] dynamicLitLen;
    private readonly uint[] dynamicDist;
    private readonly uint[] codeLengthTable;
    private uint[] activeLitLen;
    private uint[] activeDist;

    private ReadOnlySpan<byte> input;
    private int inputPos;
    private ulong bitBuffer;
    private int bitCount;

    private int windowStart;
    private int head;
    private int tail;
    private int committed;

    private State state;
    private bool finalBlock;
    private bool disposed;
    private int pendingLength;
    private int pendingDistance;
    private int storedRemaining;
    private int hlit;
    private int hdist;
    private int hclen;
    private int lengthIndex;

    private uint adler;
    private long produced;

    /// <summary>Creates a decompressor and rents its window and tables.</summary>
    /// <param name="strideHint">
    /// Bytes the caller intends to take in one go, typically a PNG scanline. It only sizes the emit region,
    /// which is clamped to <see cref="MinEmitCapacity"/>..<see cref="MaxEmitCapacity"/>; a larger request is
    /// still served, through <see cref="ReadInto"/>.
    /// </param>
    /// <param name="label">Subject of every error message, for example <c>"PNG image"</c>.</param>
    /// <param name="zlibWrapper">
    /// True for a zlib stream, whose two-byte header is validated and whose ADLER-32 trailer is checked; false
    /// for a bare DEFLATE stream, which ends with its final block.
    /// </param>
    public Inflater(int strideHint, string label, bool zlibWrapper = true)
    {
        ArgumentNullException.ThrowIfNull(label);

        this.label = label;
        this.expectZlibHeader = zlibWrapper;
        this.emitCapacity = (int)Math.Clamp((long)Math.Max(strideHint, 0) + 1, MinEmitCapacity, MaxEmitCapacity);
        this.capacity = WindowSize + this.emitCapacity;

        this.window = ArrayPool<byte>.Shared.Rent(this.capacity + CopyOverrun);
        this.lengths = ArrayPool<byte>.Shared.Rent(MaxDeclaredLengths);
        this.dynamicLitLen = ArrayPool<uint>.Shared.Rent(InflateTables.EnoughLitLen);
        this.dynamicDist = ArrayPool<uint>.Shared.Rent(InflateTables.EnoughDist);
        this.codeLengthTable = ArrayPool<uint>.Shared.Rent(InflateTables.EnoughCodeLength);
        this.activeLitLen = InflateTables.FixedLitLen;
        this.activeDist = InflateTables.FixedDistance;

        this.input = default;
        this.inputPos = 0;
        this.bitBuffer = 0;
        this.bitCount = 0;

        this.windowStart = WindowSize;
        this.head = WindowSize;
        this.tail = WindowSize;
        this.committed = WindowSize;

        this.state = zlibWrapper ? State.ZlibHeader : State.BlockHeader;
        this.finalBlock = false;
        this.disposed = false;
        this.pendingLength = 0;
        this.pendingDistance = 0;
        this.storedRemaining = 0;
        this.hlit = 0;
        this.hdist = 0;
        this.hclen = 0;
        this.lengthIndex = 0;

        this.adler = 1;
        this.produced = 0;
    }

    /// <summary>The stages the decoder can be suspended in between input segments.</summary>
    private enum State
    {
        /// <summary>Before the two-byte zlib header.</summary>
        ZlibHeader,

        /// <summary>Before the three-bit header of a block.</summary>
        BlockHeader,

        /// <summary>Before the LEN/NLEN pair of a stored block.</summary>
        StoredHeader,

        /// <summary>Inside the body of a stored block.</summary>
        StoredCopy,

        /// <summary>Before the HLIT/HDIST/HCLEN counts of a dynamic block.</summary>
        DynamicCounts,

        /// <summary>Reading the three-bit lengths of the code-length alphabet.</summary>
        CodeLengthLengths,

        /// <summary>Reading the literal/length and distance code lengths through the code-length code.</summary>
        CodeLengths,

        /// <summary>Inside the compressed body of a fixed or dynamic block.</summary>
        Block,

        /// <summary>A length has been decoded and its distance has not.</summary>
        MatchDistance,

        /// <summary>A match is fully decoded and partly, or not at all, copied.</summary>
        MatchCopy,

        /// <summary>Before the four-byte ADLER-32 trailer.</summary>
        Adler,

        /// <summary>The stream is complete.</summary>
        Done,
    }

    /// <summary>Bytes decoded so far, across every segment.</summary>
    public readonly long Produced => this.produced;

    /// <summary>True once the final block, and the ADLER-32 trailer when there is one, have been consumed.</summary>
    public readonly bool Finished => this.state == State.Done;

    /// <summary>True when the current input segment holds no more bytes for the decoder.</summary>
    public readonly bool InputExhausted => this.inputPos >= this.input.Length;

    /// <summary>Decoded bytes buffered and not yet taken.</summary>
    public readonly int Available => this.head - this.tail;

    /// <summary>The largest count <see cref="Fill"/> accepts.</summary>
    public readonly int EmitCapacity => this.emitCapacity;

    /// <summary>
    /// Hands the decoder its next slice of compressed data. The span must stay valid until the next call to
    /// <see cref="SetInput"/> or to <see cref="Dispose"/>, and the previous segment must be exhausted - which
    /// it is exactly when <see cref="Fill"/> has reported <see cref="InflateStatus.NeedInput"/>.
    /// </summary>
    /// <param name="segment">The next compressed bytes, which may be empty.</param>
    public void SetInput(ReadOnlySpan<byte> segment)
    {
        if (this.inputPos < this.input.Length)
        {
            throw new InvalidOperationException("The previous input segment has not been consumed.");
        }

        this.input = segment;
        this.inputPos = 0;
    }

    /// <summary>
    /// Decodes until <paramref name="wanted"/> bytes are buffered, the input segment is spent, or the stream
    /// ends. <see cref="InflateStatus.Output"/> is returned only when the full count is available; in the other
    /// two cases <see cref="Available"/> reports what did arrive, and it may be more than zero.
    /// </summary>
    /// <param name="wanted">Bytes the caller is about to take; at most <see cref="EmitCapacity"/>.</param>
    public InflateStatus Fill(int wanted)
    {
        if (wanted < 0 || wanted > this.emitCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(wanted), wanted, "The requested count must fit the emit region.");
        }

        while (this.head - this.tail < wanted)
        {
            if (this.state == State.Done)
            {
                break;
            }

            if (this.head >= this.capacity)
            {
                this.Rewind();
            }

            // Two bounds, because they answer different questions. The emit region ends as soon as the
            // caller's request is met, so no pass runs on decoding bytes nobody asked for; the write bound sits
            // one maximal match plus the copy overrun beyond it, which is exactly the headroom the fast loop
            // needs to stay engaged for the whole of that region rather than handing its last 274 bytes to the
            // guarded path. Both are capped by the window.
            int limit = Math.Min(this.capacity, this.tail + wanted);
            int writeLimit = Math.Min(this.capacity, limit + FastOutputSlack);
            if (this.Run(limit, writeLimit) == InflateStatus.NeedInput)
            {
                break;
            }
        }

        if (this.head - this.tail >= wanted)
        {
            return InflateStatus.Output;
        }

        return this.state == State.Done ? InflateStatus.Finished : InflateStatus.NeedInput;
    }

    /// <summary>
    /// Takes decoded bytes as a span into the window. The span stays valid until the next <see cref="Fill"/>,
    /// which may move the window contents, so it must be read or copied before then.
    /// </summary>
    /// <param name="count">Bytes to take; at most <see cref="Available"/>.</param>
    public ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > this.head - this.tail)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Fewer bytes are buffered than were requested.");
        }

        ReadOnlySpan<byte> taken = this.window.AsSpan(this.tail, count);
        this.tail += count;
        return taken;
    }

    /// <summary>
    /// Copies decoded bytes into <paramref name="destination"/>, filling it unless the stream ends or the input
    /// segment is spent first, and returns how many were written. This is the path for a request larger than
    /// <see cref="EmitCapacity"/>; anything smaller is cheaper through <see cref="Fill"/> and <see cref="Take"/>,
    /// which do not copy at all.
    /// </summary>
    /// <param name="destination">Buffer to fill.</param>
    public int ReadInto(Span<byte> destination)
    {
        int written = 0;
        while (written < destination.Length)
        {
            if (this.head == this.tail)
            {
                this.Fill(Math.Min(destination.Length - written, this.emitCapacity));
                if (this.head == this.tail)
                {
                    break;
                }
            }

            int take = Math.Min(this.head - this.tail, destination.Length - written);
            this.Take(take).CopyTo(destination.Slice(written, take));
            written += take;
        }

        return written;
    }

    /// <summary>Returns the window and tables to the pool. Calling it twice is harmless.</summary>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        ArrayPool<byte>.Shared.Return(this.window);
        ArrayPool<byte>.Shared.Return(this.lengths);
        ArrayPool<uint>.Shared.Return(this.dynamicLitLen);
        ArrayPool<uint>.Shared.Return(this.dynamicDist);
        ArrayPool<uint>.Shared.Return(this.codeLengthTable);
    }

    // -------------------------------------------------------------------------------------------------------
    // Window
    // -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Moves the tail of the window to the front to make room. What is kept is the 32 KiB of history a
    /// back-reference may still need, widened when the caller has left more than that untaken. Both are counts
    /// of bytes already produced, so what is kept never exceeds the history that actually exists, and because
    /// a pending match holds a distance rather than an index it needs no fixing up.
    /// </summary>
    private void Rewind()
    {
        this.Commit();

        int history = Math.Min(this.head - this.windowStart, WindowSize);
        int keep = Math.Max(history, this.head - this.tail);
        int shift = this.head - keep;
        if (shift <= 0)
        {
            return;
        }

        Buffer.BlockCopy(this.window, shift, this.window, 0, keep);
        this.windowStart = 0;
        this.tail -= shift;
        this.head = keep;
        this.committed = this.head;
    }

    /// <summary>
    /// Folds everything emitted since the last call into the running checksum and the produced counter. It runs
    /// at the end of every decode pass and before every rewind, so no byte is counted twice and none is moved
    /// before it has been counted.
    /// </summary>
    private void Commit()
    {
        int pending = this.head - this.committed;
        if (pending <= 0)
        {
            return;
        }

        if (this.expectZlibHeader)
        {
            this.adler = Adler32.Append(this.adler, this.window.AsSpan(this.committed, pending));
        }

        this.produced += pending;
        this.committed = this.head;
    }

    // -------------------------------------------------------------------------------------------------------
    // Bit reader
    // -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Pulls eight bytes in one read, which is why it needs eight to be present. The read deliberately overruns
    /// what it consumes: <c>bitCount</c> rises only to the byte boundary at or above 56, and the bits above it
    /// are exactly the stream bits following the new read position, so the next refill OR-ing the same byte in
    /// again changes nothing. <c>bitCount</c> never exceeds 63, so the shift is never a whole word.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RefillFast()
    {
        this.bitBuffer |= BinaryPrimitives.ReadUInt64LittleEndian(this.input.Slice(this.inputPos, FastInputSlack)) << this.bitCount;
        this.inputPos += (63 - this.bitCount) >> 3;
        this.bitCount |= 56;
    }

    /// <summary>
    /// Fills the accumulator a byte at a time, for the tail of a segment where a wide read would run off the
    /// end. It stops below 64 bits so <see cref="RefillFast"/>'s shift can never be a full word.
    /// </summary>
    private void RefillSlow()
    {
        while (this.bitCount < 56 && this.inputPos < this.input.Length)
        {
            this.bitBuffer |= (ulong)this.input[this.inputPos++] << this.bitCount;
            this.bitCount += 8;
        }
    }

    /// <summary>Tops the accumulator up towards <paramref name="wantedBits"/>, as far as the segment allows.</summary>
    /// <param name="wantedBits">Bits the caller would like to have; at most 32.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Refill(int wantedBits)
    {
        if (this.bitCount >= wantedBits)
        {
            return;
        }

        if (this.input.Length - this.inputPos >= FastInputSlack)
        {
            this.RefillFast();
            return;
        }

        this.RefillSlow();
    }

    /// <summary>Refills, and reports whether the accumulator now holds <paramref name="count"/> bits.</summary>
    /// <param name="count">Bits needed; at most 32.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool EnsureBits(int count)
    {
        this.Refill(count);
        return this.bitCount >= count;
    }

    /// <summary>Reads the low <paramref name="count"/> bits without consuming them.</summary>
    /// <param name="count">Bits to read, 0 to 32.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly uint PeekBits(int count) => (uint)(this.bitBuffer & ((1UL << count) - 1));

    /// <summary>Drops the low <paramref name="count"/> bits.</summary>
    /// <param name="count">Bits to drop, 0 to 32.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ConsumeBits(int count)
    {
        this.bitBuffer >>= count;
        this.bitCount -= count;
    }

    /// <summary>Discards the rest of the current byte. Doing it twice is the same as doing it once.</summary>
    private void AlignToByte() => this.ConsumeBits(this.bitCount & 7);

    // -------------------------------------------------------------------------------------------------------
    // State machine
    // -------------------------------------------------------------------------------------------------------

    /// <summary>Runs the state machine and folds whatever it emitted into the checksum.</summary>
    /// <param name="limit">Window index at which this pass has produced enough and stops.</param>
    /// <param name="writeLimit">Window index no write may pass; at or above <paramref name="limit"/>.</param>
    private InflateStatus Run(int limit, int writeLimit)
    {
        InflateStatus status = this.RunCore(limit, writeLimit);
        this.Commit();
        return status;
    }

    /// <summary>
    /// The state machine proper. Every arm either finishes its stage and moves on, or reports
    /// <see cref="InflateStatus.NeedInput"/> having consumed nothing of the stage it could not finish.
    /// </summary>
    /// <param name="limit">Window index at which this pass has produced enough and stops.</param>
    /// <param name="writeLimit">Window index no write may pass; at or above <paramref name="limit"/>.</param>
    private InflateStatus RunCore(int limit, int writeLimit)
    {
        while (true)
        {
            switch (this.state)
            {
                case State.ZlibHeader:
                    if (!this.EnsureBits(16))
                    {
                        return InflateStatus.NeedInput;
                    }

                    this.ReadZlibHeader();
                    this.state = State.BlockHeader;
                    continue;

                case State.BlockHeader:
                    if (!this.EnsureBits(3))
                    {
                        return InflateStatus.NeedInput;
                    }

                    this.ReadBlockHeader();
                    continue;

                case State.StoredHeader:
                    this.AlignToByte();
                    if (!this.EnsureBits(32))
                    {
                        return InflateStatus.NeedInput;
                    }

                    this.ReadStoredHeader();
                    continue;

                case State.StoredCopy:
                {
                    InflateStatus status = this.CopyStored(limit);
                    if (status != InflateStatus.Output || this.storedRemaining > 0)
                    {
                        return status;
                    }

                    this.FinishBlock();
                    continue;
                }

                case State.DynamicCounts:
                    if (!this.EnsureBits(14))
                    {
                        return InflateStatus.NeedInput;
                    }

                    this.ReadDynamicCounts();
                    continue;

                case State.CodeLengthLengths:
                    if (!this.ReadCodeLengthLengths())
                    {
                        return InflateStatus.NeedInput;
                    }

                    continue;

                case State.CodeLengths:
                    if (!this.ReadCodeLengths())
                    {
                        return InflateStatus.NeedInput;
                    }

                    continue;

                case State.Block:
                {
                    Step step = this.DecodeBlock(limit, writeLimit);
                    if (step == Step.NeedInput)
                    {
                        return InflateStatus.NeedInput;
                    }

                    if (step == Step.OutputFull)
                    {
                        return InflateStatus.Output;
                    }

                    continue;
                }

                case State.MatchDistance:
                    if (!this.ReadDistance())
                    {
                        return InflateStatus.NeedInput;
                    }

                    continue;

                case State.MatchCopy:
                {
                    int room = limit - this.head;
                    if (room <= 0)
                    {
                        return InflateStatus.Output;
                    }

                    int take = Math.Min(this.pendingLength, room);
                    this.head = this.CopyMatch(this.head, this.pendingDistance, take);
                    this.pendingLength -= take;
                    if (this.pendingLength > 0)
                    {
                        return InflateStatus.Output;
                    }

                    this.state = State.Block;
                    continue;
                }

                case State.Adler:
                    this.AlignToByte();
                    if (!this.EnsureBits(32))
                    {
                        return InflateStatus.NeedInput;
                    }

                    this.Commit();
                    this.ReadAdler();
                    continue;

                default:
                    return InflateStatus.Finished;
            }
        }
    }

    /// <summary>Validates and consumes the two-byte zlib header, applying exactly the checks zlib applies.</summary>
    private void ReadZlibHeader()
    {
        uint cmf = this.PeekBits(8);
        uint flg = (uint)((this.bitBuffer >> 8) & 0xFF);
        this.ConsumeBits(16);

        if ((cmf & 0x0F) != 8)
        {
            throw this.Malformed("uses an unsupported compression method");
        }

        if ((cmf >> 4) > 7)
        {
            throw this.Malformed("declares a window larger than 32 KiB");
        }

        if ((((cmf * 256) + flg) % 31) != 0)
        {
            throw this.Malformed("has a corrupt zlib header");
        }

        if (((flg >> 5) & 1) != 0)
        {
            throw this.Malformed("requires a preset dictionary");
        }
    }

    /// <summary>Consumes a block header and selects the tables, or the stored path, that it names.</summary>
    private void ReadBlockHeader()
    {
        this.finalBlock = this.PeekBits(1) != 0;
        int type = (int)((this.bitBuffer >> 1) & 3);
        this.ConsumeBits(3);

        switch (type)
        {
            case 0:
                this.state = State.StoredHeader;
                break;
            case 1:
                this.activeLitLen = InflateTables.FixedLitLen;
                this.activeDist = InflateTables.FixedDistance;
                this.state = State.Block;
                break;
            case 2:
                this.state = State.DynamicCounts;
                break;
            default:
                throw this.Malformed("uses a reserved block type");
        }
    }

    /// <summary>Consumes the LEN/NLEN pair of a stored block and checks that they are complements.</summary>
    private void ReadStoredHeader()
    {
        int length = (int)this.PeekBits(16);
        this.ConsumeBits(16);
        int complement = (int)this.PeekBits(16);
        this.ConsumeBits(16);

        if (length != (~complement & 0xFFFF))
        {
            throw this.Malformed("has a stored block whose length does not match its complement");
        }

        this.storedRemaining = length;
        this.state = State.StoredCopy;
    }

    /// <summary>Consumes the HLIT, HDIST and HCLEN counts of a dynamic block and rejects the illegal ones.</summary>
    private void ReadDynamicCounts()
    {
        this.hlit = (int)this.PeekBits(5) + 257;
        this.ConsumeBits(5);
        this.hdist = (int)this.PeekBits(5) + 1;
        this.ConsumeBits(5);
        this.hclen = (int)this.PeekBits(4) + 4;
        this.ConsumeBits(4);

        if (this.hlit > MaxDeclaredLitLen || this.hdist > MaxDeclaredDist)
        {
            throw this.Malformed("declares too many literal/length or distance codes");
        }

        this.lengths.AsSpan(0, InflateTables.MaxCodeLengthSymbols).Clear();
        this.lengthIndex = 0;
        this.state = State.CodeLengthLengths;
    }

    /// <summary>
    /// Reads the HCLEN three-bit lengths of the code-length alphabet, in the header's own permuted order, and
    /// builds the code they describe. Returns false when the segment runs out part-way, with the cursor parked
    /// on the length it could not read.
    /// </summary>
    private bool ReadCodeLengthLengths()
    {
        ReadOnlySpan<byte> order = InflateTables.CodeLengthOrder;
        while (this.lengthIndex < this.hclen)
        {
            if (!this.EnsureBits(3))
            {
                return false;
            }

            this.lengths[order[this.lengthIndex]] = (byte)this.PeekBits(3);
            this.ConsumeBits(3);
            this.lengthIndex++;
        }

        if (!InflateTables.TryBuild(
                this.lengths.AsSpan(0, InflateTables.MaxCodeLengthSymbols),
                InflateTables.MaxCodeLengthSymbols,
                InflateTables.CodeLengthRootBits,
                TableKind.CodeLengths,
                this.codeLengthTable.AsSpan(0, InflateTables.EnoughCodeLength),
                out _))
        {
            throw this.Malformed("has an invalid code-length code");
        }

        // The scratch is reused for the two alphabets now that the code-length table itself is built.
        this.lengths.AsSpan(0, this.hlit + this.hdist).Clear();
        this.lengthIndex = 0;
        this.state = State.CodeLengths;
        return true;
    }

    /// <summary>
    /// Reads the literal/length and distance code lengths through the code-length code, expanding the repeat
    /// codes 16, 17 and 18, then builds both tables. Returns false when the segment runs out; the cursor and the
    /// lengths written so far are the whole of the resume state, because a repeat's source is a length that has
    /// already been stored.
    /// </summary>
    private bool ReadCodeLengths()
    {
        int total = this.hlit + this.hdist;
        while (this.lengthIndex < total)
        {
            if (!this.TryPeekSymbol(this.codeLengthTable, InflateTables.CodeLengthRootBits, CodeLengthRootMask, out uint entry, out int codeLength))
            {
                return false;
            }

            if (InflateTables.IsInvalid(entry))
            {
                throw this.Malformed("has an invalid code-length code");
            }

            int symbol = InflateTables.Value(entry);
            if (symbol < 16)
            {
                this.ConsumeBits(codeLength);
                this.lengths[this.lengthIndex++] = (byte)symbol;
                continue;
            }

            int extraBits = symbol == 16 ? 2 : (symbol == 17 ? 3 : 7);
            if (!this.EnsureBits(codeLength + extraBits))
            {
                return false;
            }

            int repeat = (int)((this.bitBuffer >> codeLength) & (ulong)((1 << extraBits) - 1));
            byte value;
            if (symbol == 16)
            {
                if (this.lengthIndex == 0)
                {
                    throw this.Malformed("repeats a code length before any has been read");
                }

                value = this.lengths[this.lengthIndex - 1];
                repeat += 3;
            }
            else if (symbol == 17)
            {
                value = 0;
                repeat += 3;
            }
            else
            {
                value = 0;
                repeat += 11;
            }

            if (this.lengthIndex + repeat > total)
            {
                throw this.Malformed("declares more code lengths than the block has symbols");
            }

            this.ConsumeBits(codeLength + extraBits);
            this.lengths.AsSpan(this.lengthIndex, repeat).Fill(value);
            this.lengthIndex += repeat;
        }

        if (!InflateTables.TryBuild(
                this.lengths.AsSpan(0, this.hlit),
                this.hlit,
                InflateTables.LitLenRootBits,
                TableKind.LitLen,
                this.dynamicLitLen.AsSpan(0, InflateTables.EnoughLitLen),
                out _))
        {
            throw this.Malformed("has an invalid literal/length code");
        }

        if (!InflateTables.TryBuild(
                this.lengths.AsSpan(this.hlit, this.hdist),
                this.hdist,
                InflateTables.DistRootBits,
                TableKind.Distance,
                this.dynamicDist.AsSpan(0, InflateTables.EnoughDist),
                out _))
        {
            throw this.Malformed("has an invalid distance code");
        }

        this.activeLitLen = this.dynamicLitLen;
        this.activeDist = this.dynamicDist;
        this.state = State.Block;
        return true;
    }

    /// <summary>Consumes the four big-endian bytes of the ADLER-32 trailer and compares them with the running sum.</summary>
    private void ReadAdler()
    {
        uint stored = 0;
        for (int i = 0; i < 4; i++)
        {
            stored = (stored << 8) | this.PeekBits(8);
            this.ConsumeBits(8);
        }

        if (stored != this.adler)
        {
            throw this.Malformed("has a corrupt ADLER-32 checksum");
        }

        this.state = State.Done;
    }

    /// <summary>
    /// Copies the body of a stored block. Bytes the bit reader had already pulled into the accumulator come out
    /// of it first; the rest is a bulk copy straight from the input segment.
    /// </summary>
    /// <param name="limit">Window index the emitted data may not pass.</param>
    private InflateStatus CopyStored(int limit)
    {
        while (this.storedRemaining > 0)
        {
            if (this.head >= limit)
            {
                return InflateStatus.Output;
            }

            if (this.bitCount >= 8)
            {
                this.window[this.head++] = (byte)this.bitBuffer;
                this.ConsumeBits(8);
                this.storedRemaining--;
                continue;
            }

            // The accumulator is empty and byte-aligned here, so the rest comes straight out of the segment.
            // Its read-ahead bits are dropped first: they mirror bytes at the old read position, and the bulk
            // copy is about to move that position past them, which would leave them describing the wrong bytes.
            this.bitBuffer = 0;

            int available = this.input.Length - this.inputPos;
            if (available <= 0)
            {
                return InflateStatus.NeedInput;
            }

            int take = Math.Min(Math.Min(this.storedRemaining, available), limit - this.head);
            this.input.Slice(this.inputPos, take).CopyTo(this.window.AsSpan(this.head, take));
            this.inputPos += take;
            this.head += take;
            this.storedRemaining -= take;
        }

        return InflateStatus.Output;
    }

    /// <summary>Moves on from a completed block, to the next one or to the end of the stream.</summary>
    private void FinishBlock()
    {
        if (!this.finalBlock)
        {
            this.state = State.BlockHeader;
            return;
        }

        this.state = this.expectZlibHeader ? State.Adler : State.Done;
    }

    // -------------------------------------------------------------------------------------------------------
    // Compressed blocks
    // -------------------------------------------------------------------------------------------------------

    /// <summary>What a pass over a compressed block managed before it had to stop.</summary>
    private enum Step
    {
        /// <summary>The state changed; the machine should keep running.</summary>
        Continue,

        /// <summary>The segment ran out mid-symbol; nothing of that symbol was consumed.</summary>
        NeedInput,

        /// <summary>The emit region is full.</summary>
        OutputFull,
    }

    /// <summary>
    /// Decodes literals and matches. The first loop is the hot one: it runs only while eight input bytes and one
    /// maximal match plus the copy overrun of output room are in hand, which is what lets it refill once and
    /// then take a literal/length code, its extra bits, a distance code and its extra bits - 48 bits at worst -
    /// out of the 56 the refill guarantees, with no further checks. The reader and the write head live in locals
    /// for its duration and are written back at every exit, because reached through a by-reference <c>this</c>
    /// each of them is memory the compiler must reload. Its indexing is unchecked, which the loop entry
    /// condition is what earns: the headroom covers a maximal match and the copy overrun, a root index is
    /// masked to the root width and every table is at least that wide, and a sub-table index falls inside the
    /// region <see cref="InflateTables.TryBuild"/> reserved for it. The second loop is the same decode written
    /// so that every read is guarded, for the ends of segments and of the emit region.
    /// </summary>
    /// <param name="limit">Window index at which this pass has produced enough and stops.</param>
    /// <param name="writeLimit">Window index no write may pass; at or above <paramref name="limit"/>.</param>
    private Step DecodeBlock(int limit, int writeLimit)
    {
        uint[] litTable = this.activeLitLen;
        uint[] distanceTable = this.activeDist;

        // The headroom test is against the write bound, so the loop stays engaged over the whole emit region
        // and leaves it only once the region itself is full - never merely because its end came into view.
        if (this.input.Length - this.inputPos >= FastInputSlack &&
            writeLimit - this.head >= FastOutputSlack &&
            this.head < limit)
        {
            ref byte windowRef = ref MemoryMarshal.GetArrayDataReference(this.window);
            ref uint litRef = ref MemoryMarshal.GetArrayDataReference(litTable);
            ref uint distanceRef = ref MemoryMarshal.GetArrayDataReference(distanceTable);
            ReadOnlySpan<byte> segment = this.input;
            int historyStart = this.windowStart;
            ulong bits = this.bitBuffer;
            int count = this.bitCount;
            int pos = this.inputPos;
            int at = this.head;

            while (segment.Length - pos >= FastInputSlack && writeLimit - at >= FastOutputSlack && at < limit)
            {
                // The branchless refill of RefillFast, inlined against the locals.
                bits |= BinaryPrimitives.ReadUInt64LittleEndian(segment.Slice(pos, FastInputSlack)) << count;
                pos += (63 - count) >> 3;
                count |= 56;

                uint entry = Unsafe.Add(ref litRef, (nint)(bits & LitLenRootMask));
                if (InflateTables.IsSubtable(entry))
                {
                    entry = Unsafe.Add(
                        ref litRef,
                        (nint)(InflateTables.Value(entry) +
                            (int)((bits >> InflateTables.LitLenRootBits) & (ulong)((1 << InflateTables.ExtraBits(entry)) - 1))));
                }

                int used = InflateTables.CodeLength(entry);
                bits >>= used;
                count -= used;

                if (InflateTables.IsLiteral(entry))
                {
                    Unsafe.Add(ref windowRef, (nint)at) = (byte)InflateTables.Value(entry);
                    at++;
                    continue;
                }

                this.bitBuffer = bits;
                this.bitCount = count;
                this.inputPos = pos;
                this.head = at;

                if (InflateTables.IsEndOfBlock(entry))
                {
                    this.FinishBlock();
                    return Step.Continue;
                }

                if (InflateTables.IsInvalid(entry) || used == 0)
                {
                    throw this.Malformed("has an invalid literal/length code");
                }

                // An extra-bit count of zero masks to nothing and shifts by nothing, so both reads below run
                // unconditionally rather than behind a branch the data gives no way to predict.
                int length = InflateTables.Value(entry);
                int lengthExtra = InflateTables.ExtraBits(entry);
                length += (int)(bits & (ulong)((1 << lengthExtra) - 1));
                bits >>= lengthExtra;
                count -= lengthExtra;

                uint distanceEntry = Unsafe.Add(ref distanceRef, (nint)(bits & DistRootMask));
                if (InflateTables.IsSubtable(distanceEntry))
                {
                    distanceEntry = Unsafe.Add(
                        ref distanceRef,
                        (nint)(InflateTables.Value(distanceEntry) +
                            (int)((bits >> InflateTables.DistRootBits) & (ulong)((1 << InflateTables.ExtraBits(distanceEntry)) - 1))));
                }

                int distanceUsed = InflateTables.CodeLength(distanceEntry);
                bits >>= distanceUsed;
                count -= distanceUsed;

                if (InflateTables.IsInvalid(distanceEntry) || distanceUsed == 0)
                {
                    this.bitBuffer = bits;
                    this.bitCount = count;
                    throw this.Malformed("has an invalid distance code");
                }

                int distance = InflateTables.Value(distanceEntry);
                int distanceExtra = InflateTables.ExtraBits(distanceEntry);
                distance += (int)(bits & (ulong)((1 << distanceExtra) - 1));
                bits >>= distanceExtra;
                count -= distanceExtra;

                if (distance > at - historyStart)
                {
                    this.bitBuffer = bits;
                    this.bitCount = count;
                    throw this.Malformed("references data further back than the window holds");
                }

                at = this.CopyMatch(at, distance, length);
            }

            this.bitBuffer = bits;
            this.bitCount = count;
            this.inputPos = pos;
            this.head = at;
        }

        while (true)
        {
            if (this.head >= limit)
            {
                return Step.OutputFull;
            }

            if (!this.TryPeekSymbol(litTable, InflateTables.LitLenRootBits, LitLenRootMask, out uint entry, out int codeLength))
            {
                return Step.NeedInput;
            }

            if (InflateTables.IsLiteral(entry))
            {
                this.ConsumeBits(codeLength);
                this.window[this.head++] = (byte)InflateTables.Value(entry);
                continue;
            }

            if (InflateTables.IsEndOfBlock(entry))
            {
                this.ConsumeBits(codeLength);
                this.FinishBlock();
                return Step.Continue;
            }

            if (InflateTables.IsInvalid(entry))
            {
                throw this.Malformed("has an invalid literal/length code");
            }

            int extraBits = InflateTables.ExtraBits(entry);
            if (!this.EnsureBits(codeLength + extraBits))
            {
                return Step.NeedInput;
            }

            this.pendingLength = InflateTables.Value(entry) +
                (int)((this.bitBuffer >> codeLength) & (ulong)((1 << extraBits) - 1));
            this.ConsumeBits(codeLength + extraBits);
            this.state = State.MatchDistance;
            return Step.Continue;
        }
    }

    /// <summary>
    /// Decodes the distance half of a match, atomically with its extra bits, and validates it against the
    /// history that is actually present. Returns false when the segment runs out, having consumed nothing.
    /// </summary>
    private bool ReadDistance()
    {
        if (!this.TryPeekSymbol(this.activeDist, InflateTables.DistRootBits, DistRootMask, out uint entry, out int codeLength))
        {
            return false;
        }

        if (InflateTables.IsInvalid(entry))
        {
            throw this.Malformed("has an invalid distance code");
        }

        int extraBits = InflateTables.ExtraBits(entry);
        if (!this.EnsureBits(codeLength + extraBits))
        {
            return false;
        }

        int distance = InflateTables.Value(entry) + (int)((this.bitBuffer >> codeLength) & (ulong)((1 << extraBits) - 1));
        this.ConsumeBits(codeLength + extraBits);

        if (distance > this.head - this.windowStart)
        {
            throw this.Malformed("references data further back than the window holds");
        }

        this.pendingDistance = distance;
        this.state = State.MatchCopy;
        return true;
    }

    /// <summary>
    /// Looks one code up without consuming it. The entry's own length decides whether the bits buffered are
    /// enough, which is sound even when fewer bits are held than the index used: an entry is replicated across
    /// every table slot that agrees with it over its own code length.
    /// </summary>
    /// <param name="table">The table to decode through.</param>
    /// <param name="rootBits">Its root index width.</param>
    /// <param name="rootMask">The mask for that width.</param>
    /// <param name="entry">The entry found; meaningful only when this returns true.</param>
    /// <param name="codeLength">Bits the caller must consume for it; meaningful only when this returns true.</param>
    private bool TryPeekSymbol(uint[] table, int rootBits, ulong rootMask, out uint entry, out int codeLength)
    {
        this.Refill(rootBits);
        entry = table[(int)(this.bitBuffer & rootMask)];
        codeLength = InflateTables.CodeLength(entry);
        if (codeLength > this.bitCount)
        {
            return false;
        }

        if (InflateTables.IsSubtable(entry))
        {
            int indexBits = InflateTables.ExtraBits(entry);
            this.Refill(rootBits + indexBits);
            entry = table[InflateTables.Value(entry) +
                (int)((this.bitBuffer >> rootBits) & (ulong)((1 << indexBits) - 1))];
            codeLength = InflateTables.CodeLength(entry);
            if (codeLength > this.bitCount)
            {
                return false;
            }
        }

        if (codeLength == 0)
        {
            throw this.Malformed("contains an undecodable symbol");
        }

        return true;
    }

    /// <summary>
    /// Copies a back-reference forward inside the window, in the widest steps the distance allows. A distance
    /// below the step width would make a wide copy read bytes it is itself producing, so short distances seed
    /// one period and then double it. The two wide tiers may write up to <see cref="CopyOverrun"/> - 1 bytes
    /// past the match, which the window is over-rented to absorb.
    /// </summary>
    /// <param name="target">Window index to copy to, which the caller owns rather than this method.</param>
    /// <param name="distance">How far back the source lies; already validated against the history.</param>
    /// <param name="count">Bytes to copy; the caller has already checked that they fit below the limit.</param>
    /// <returns>The window index just past the copy.</returns>
    private int CopyMatch(int target, int distance, int count)
    {
        ref byte start = ref MemoryMarshal.GetArrayDataReference(this.window);
        int source = target - distance;

        if (distance >= CopyOverrun && SimdConfig.Vector128Enabled)
        {
            for (int i = 0; i < count; i += CopyOverrun)
            {
                Vector128.StoreUnsafe(Vector128.LoadUnsafe(ref start, (nuint)(source + i)), ref start, (nuint)(target + i));
            }
        }
        else if (distance >= sizeof(ulong))
        {
            for (int i = 0; i < count; i += sizeof(ulong))
            {
                Unsafe.WriteUnaligned(
                    ref Unsafe.Add(ref start, (nint)(target + i)),
                    Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, (nint)(source + i))));
            }
        }
        else
        {
            int seeded = Math.Min(distance, count);
            for (int i = 0; i < seeded; i++)
            {
                Unsafe.Add(ref start, (nint)(target + i)) = Unsafe.Add(ref start, (nint)(source + i));
            }

            while (seeded < count)
            {
                int step = Math.Min(seeded, count - seeded);
                Buffer.BlockCopy(this.window, target, this.window, target + seeded, step);
                seeded += step;
            }
        }

        return target + count;
    }

    /// <summary>Builds the one exception type this decoder ever throws.</summary>
    /// <param name="detail">What is wrong, as a phrase completing "&lt;label&gt; compressed data ...".</param>
    private readonly InvalidImageContentException Malformed(string detail)
        => new($"{this.label} compressed data {detail}.");
}

/// <summary>
/// Whole-buffer entry point, for callers that hold all of the compressed data and want all of the decompressed
/// data back - metadata chunks and tests, rather than the row-at-a-time image path, which drives
/// <see cref="Inflater"/> directly and never materialises the whole thing.
/// </summary>
internal static class Inflate
{
    /// <summary>Initial size of the output buffer, which doubles from there.</summary>
    private const int InitialCapacity = 8192;

    /// <summary>
    /// Decompresses a complete zlib stream. Bytes after the end of the stream are ignored, exactly as
    /// <c>ZLibStream</c> ignores them.
    /// </summary>
    /// <param name="source">The complete zlib stream.</param>
    /// <param name="maxBytes">Largest output accepted; anything longer is treated as malformed.</param>
    /// <param name="label">Subject of every error message, for example <c>"PNG iCCP profile"</c>.</param>
    public static byte[] Decompress(ReadOnlySpan<byte> source, long maxBytes, string label)
    {
        ArgumentNullException.ThrowIfNull(label);

        long ceiling = Math.Clamp(maxBytes, 0, Array.MaxLength);
        var inflater = new Inflater(0, label);
        try
        {
            inflater.SetInput(source);

            byte[] output = Array.Empty<byte>();
            int written = 0;
            while (true)
            {
                InflateStatus status = inflater.Fill(inflater.EmitCapacity);
                int available = inflater.Available;
                if (available > 0)
                {
                    if (written + (long)available > ceiling)
                    {
                        throw new InvalidImageContentException($"{label} exceeds the {maxBytes:N0} byte limit.");
                    }

                    if (output.Length < written + available)
                    {
                        long grown = Math.Max(written + (long)available, Math.Max(InitialCapacity, output.Length * 2L));
                        Array.Resize(ref output, (int)Math.Min(grown, ceiling));
                    }

                    inflater.Take(available).CopyTo(output.AsSpan(written, available));
                    written += available;
                }

                if (inflater.Finished)
                {
                    break;
                }

                if (status == InflateStatus.NeedInput)
                {
                    throw new InvalidImageContentException($"{label} compressed data is truncated.");
                }
            }

            if (written != output.Length)
            {
                Array.Resize(ref output, written);
            }

            return output;
        }
        finally
        {
            inflater.Dispose();
        }
    }
}
