using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace EasyImageSharp.Formats.Png;

/// <summary>
/// Hands <see cref="PngDecoder"/> one image's decompressed bytes - the filter byte and the filtered bytes of
/// each scanline in turn - out of the chunk payloads that carry them: the IDAT chunks of a still image, or the
/// fdAT payloads (past their sequence numbers) of one animation frame. It is the single seam between the
/// decoder and the inflate backend, so the decoder reads scanlines the same way whichever backend is compiled
/// in, and the frame-by-frame APNG path shares it with the still path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the backend is chosen by target framework.</b> On .NET 8 the runtime's <see cref="ZLibStream"/> sits
/// on native zlib, and this library's own <see cref="Inflater"/> beats it: measured on synthesised photographic
/// 3032x2008 PNGs and an A4-at-300-DPI grayscale document, the managed path ran at 0.41x-0.98x of the
/// <see cref="ZLibStream"/> path's time - 1.4x-2.4x faster - with byte-identical output. On .NET 10 the runtime
/// switched to zlib-ng with hand-written SIMD and the comparison inverts: 1.32x-1.36x <em>slower</em> on the
/// photographic files and 8x-13x slower on the highly compressible document. So the managed inflate is compiled
/// in below .NET 10 only. Do not "fix" this by enabling it everywhere - those net10 ratios were stable to two
/// decimal places across repeated runs and are not measurement noise.
/// </para>
/// <para>
/// The two backends are also fed differently, which is the other half of the win. The <see cref="ZLibStream"/>
/// path has to concatenate every chunk payload into one pooled buffer first, because a stream reads from a
/// single <see cref="MemoryStream"/>; <see cref="Inflater"/> is pushed the payload slices where they already
/// lie in the file, so nothing is copied, and each scanline is then taken as a span straight out of the inflate
/// window instead of being read into a buffer of the caller's. Empty payloads are skipped rather than
/// concatenated away, which is what keeps a zero-length IDAT chunk invisible in any position.
/// </para>
/// <para>
/// Behaviour is identical on both. A short read is <c>"PNG pixel data ended unexpectedly."</c>; a stream that
/// ends exactly on the last scanline byte decodes, missing final block and missing trailer included; surplus
/// decompressed data is reported through <see cref="ProbeSurplus"/> for the decoder to reject; bytes after the
/// end of the compressed stream are ignored unless <see cref="EndedAtStreamEnd"/> is consulted; and a corrupt
/// ADLER-32 trailer raises <see cref="InvalidImageContentException"/> - from the framework by way of
/// <c>DecoderGuard</c> on one path and from <see cref="Inflater"/> on the other, so only the wording differs.
/// </para>
/// </remarks>
internal ref struct PngIdatReader
{
#if NET10_0_OR_GREATER
    /// <summary>The concatenated chunk payloads, rented for the reader's lifetime.</summary>
    private readonly byte[] compressed;

    /// <summary>How much of <see cref="compressed"/> is real payload.</summary>
    private readonly int compressedLength;

    private readonly MemoryStream source;
    private readonly ZLibStream zlib;

    /// <summary>The ADLER-32 tap, present only when the caller asked for <see cref="EndedAtStreamEnd"/>.</summary>
    private readonly AdlerReadStream? checksummed;

    /// <summary>Where scanlines are read: <see cref="checksummed"/> when there is one, else the raw stream.</summary>
    private readonly Stream stream;

    private bool disposed;
#else
    /// <summary>The whole file, which <see cref="segments"/> indexes into.</summary>
    private readonly ReadOnlySpan<byte> file;

    /// <summary>The chunk payloads carrying this image's compressed data, in file order.</summary>
    private readonly List<(int Start, int Length)> segments;

    private Inflater inflater;

    /// <summary>Index into <see cref="segments"/> of the next payload to push.</summary>
    private int nextSegment;
#endif

    /// <summary>Creates a reader over one image's compressed chunk payloads.</summary>
    /// <param name="file">The whole file, which <paramref name="segments"/> indexes into.</param>
    /// <param name="segments">The payload slices carrying the compressed data, in file order; some may be empty.
    /// </param>
    /// <param name="strideHint">
    /// The longest single read the decoder will make, which is one filter byte plus the widest scanline of any
    /// interlace pass. It only sizes a buffer; a larger read is still served.
    /// </param>
    /// <param name="verifyStreamEnd">
    /// True to make <see cref="EndedAtStreamEnd"/> meaningful, which costs a checksum of every decompressed
    /// byte on the <see cref="ZLibStream"/> path. Pass false when trailing bytes are to be ignored.
    /// </param>
    public PngIdatReader(
        ReadOnlySpan<byte> file, List<(int Start, int Length)> segments, int strideHint, bool verifyStreamEnd)
    {
        ArgumentNullException.ThrowIfNull(segments);

#if NET10_0_OR_GREATER
        long total = 0;
        foreach ((int _, int length) in segments)
        {
            total += length;
        }

        if (total > int.MaxValue)
        {
            throw new InvalidImageContentException("PNG compressed data is too large.");
        }

        this.compressed = ArrayPool<byte>.Shared.Rent((int)total);
        int copied = 0;
        foreach ((int start, int length) in segments)
        {
            file.Slice(start, length).CopyTo(this.compressed.AsSpan(copied));
            copied += length;
        }

        this.compressedLength = copied;
        this.source = new MemoryStream(this.compressed, 0, copied, writable: false);
        this.zlib = new ZLibStream(this.source, CompressionMode.Decompress);

        // ZLibStream stops at the end of its own stream and silently ignores whatever follows, so the only way
        // to notice a second stream appended to a frame is to check that the payload's last four bytes really
        // are the ADLER-32 trailer of the data just read.
        this.checksummed = verifyStreamEnd ? new AdlerReadStream(this.zlib) : null;
        this.stream = this.checksummed ?? (Stream)this.zlib;
        this.disposed = false;
#else
        this.file = file;
        this.segments = segments;
        this.inflater = new Inflater(strideHint, "PNG image");
        this.nextSegment = 0;
#endif
    }

    /// <summary>
    /// True when the compressed data ended exactly where its zlib stream did: the stream really finished, and
    /// nothing follows it. Only meaningful when the reader was built with <c>verifyStreamEnd</c>, and only once
    /// every scanline has been read and <see cref="ProbeSurplus"/> has driven the stream to its end.
    /// </summary>
    public readonly bool EndedAtStreamEnd
    {
        get
        {
#if NET10_0_OR_GREATER
            return this.checksummed is not null
                && this.compressedLength >= 4
                && BinaryPrimitives.ReadUInt32BigEndian(
                    this.compressed.AsSpan(this.compressedLength - 4)) == this.checksummed.Checksum;
#else
            return this.inflater.Finished && !this.HasPendingInput;
#endif
        }
    }

    /// <summary>Reads one scanline's filter byte.</summary>
    public byte ReadFilterType()
    {
#if NET10_0_OR_GREATER
        int value = this.stream.ReadByte();
        if (value < 0)
        {
            throw Ended();
        }

        return (byte)value;
#else
        while (true)
        {
            InflateStatus status = this.inflater.Fill(1);
            if (status == InflateStatus.Output)
            {
                return this.inflater.Take(1)[0];
            }

            if (status == InflateStatus.Finished || !this.Advance())
            {
                throw Ended();
            }
        }
#endif
    }

    /// <summary>
    /// Reads one filtered scanline, exactly as long as <paramref name="scratch"/>. The returned span is that
    /// scanline, and is either <paramref name="scratch"/> itself or a view of the reader's own buffer that stays
    /// valid only until the next read - so the caller must consume it before reading again. Unfiltering it into
    /// a destination of the caller's own is what the aliasing-safe
    /// <see cref="PngFilters.Unfilter(byte, ReadOnlySpan{byte}, Span{byte}, ReadOnlySpan{byte}, int)"/> is for.
    /// </summary>
    /// <param name="scratch">A buffer the length of the scanline, used only if the reader cannot lend one.</param>
    public ReadOnlySpan<byte> ReadRow(Span<byte> scratch)
    {
#if NET10_0_OR_GREATER
        int read = 0;
        while (read < scratch.Length)
        {
            int n = this.stream.Read(scratch[read..]);
            if (n <= 0)
            {
                throw Ended();
            }

            read += n;
        }

        return scratch;
#else
        int wanted = scratch.Length;
        if (wanted <= this.inflater.EmitCapacity)
        {
            while (true)
            {
                InflateStatus status = this.inflater.Fill(wanted);
                if (status == InflateStatus.Output)
                {
                    return this.inflater.Take(wanted);
                }

                if (status == InflateStatus.Finished || !this.Advance())
                {
                    throw Ended();
                }
            }
        }

        // A scanline wider than the emit region cannot be lent as one span, so that one is copied instead.
        int written = 0;
        while (written < wanted)
        {
            written += this.inflater.ReadInto(scratch[written..]);
            if (written >= wanted)
            {
                break;
            }

            if (this.inflater.Finished || !this.Advance())
            {
                throw Ended();
            }
        }

        return scratch;
#endif
    }

    /// <summary>
    /// True when the compressed data decodes to more bytes than the caller asked for, which is how the decoder
    /// notices that IHDR and the image data disagree about the layout. Driving the stream this one byte further
    /// is also what reaches - and so validates - the ADLER-32 trailer.
    /// </summary>
    public bool ProbeSurplus()
    {
#if NET10_0_OR_GREATER
        Span<byte> probe = stackalloc byte[1];
        return this.stream.Read(probe) > 0;
#else
        while (true)
        {
            InflateStatus status = this.inflater.Fill(1);
            if (status == InflateStatus.Output)
            {
                return true;
            }

            if (status == InflateStatus.Finished || !this.Advance())
            {
                return false;
            }
        }
#endif
    }

    /// <summary>Releases the backend's buffers. Calling it twice is harmless.</summary>
    public void Dispose()
    {
#if NET10_0_OR_GREATER
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.zlib.Dispose();
        this.source.Dispose();
        ArrayPool<byte>.Shared.Return(this.compressed);
#else
        this.inflater.Dispose();
#endif
    }

    private static InvalidImageContentException Ended()
        => new("PNG pixel data ended unexpectedly.");

#if !NET10_0_OR_GREATER
    /// <summary>True while any compressed byte the reader was given is still unconsumed.</summary>
    private readonly bool HasPendingInput
    {
        get
        {
            if (!this.inflater.InputExhausted)
            {
                return true;
            }

            for (int i = this.nextSegment; i < this.segments.Count; i++)
            {
                if (this.segments[i].Length > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Pushes the next non-empty payload slice at the decompressor, and returns false once they are all spent.
    /// Empty slices are skipped rather than pushed, so a zero-length chunk costs nothing and - exactly as the
    /// concatenating path arranged - cannot be told apart from not being there at all.
    /// </summary>
    private bool Advance()
    {
        while (this.nextSegment < this.segments.Count)
        {
            (int start, int length) = this.segments[this.nextSegment++];
            if (length == 0)
            {
                continue;
            }

            this.inflater.SetInput(this.file.Slice(start, length));
            return true;
        }

        return false;
    }
#endif

#if NET10_0_OR_GREATER
    /// <summary>
    /// A read-only pass-through that accumulates the ADLER-32 of everything read through it, so a caller can
    /// compare it against the zlib trailer the compressed data is supposed to end with.
    /// </summary>
    private sealed class AdlerReadStream : Stream
    {
        private readonly Stream inner;
        private uint checksum = 1;

        public AdlerReadStream(Stream inner) => this.inner = inner;

        /// <summary>The ADLER-32 of every byte read so far, seeded the way a zlib stream seeds it.</summary>
        public uint Checksum => this.checksum;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return this.Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            int read = this.inner.Read(buffer);
            if (read > 0)
            {
                this.checksum = Adler32.Append(this.checksum, buffer[..read]);
            }

            return read;
        }

        public override int ReadByte()
        {
            Span<byte> one = stackalloc byte[1];
            return this.Read(one) == 1 ? one[0] : -1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
#endif
}
