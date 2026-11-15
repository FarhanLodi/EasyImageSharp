using System.Buffers;

namespace EasyImageSharp.Streaming;

/// <summary>
/// The transcoder's view of the caller's input stream: a read-only stream that can go back to where it started
/// - so a decoder that declines an image after reading its header can hand the same bytes to the fallback - and
/// that can be filled ahead asynchronously, so the synchronous row decoders find their next bytes already
/// buffered when the pipeline runs asynchronously.
/// </summary>
/// <remarks>
/// <para>
/// A seekable input is rewound by seeking, and exposes its own positions and length unchanged. A non-seekable
/// input is <em>recorded</em> from the start until <see cref="StopRecording"/> is called, which is what makes
/// <see cref="Rewind"/> possible; recording is stopped as soon as a decoder has accepted the image, so only the
/// header bytes are ever held twice.
/// </para>
/// <para>
/// A synchronous read is served from the buffer when it holds anything and only otherwise reads the inner stream
/// synchronously. <see cref="SynchronousReads"/> counts those, which tells the asynchronous pipeline - and its
/// tests - how much of the input was not read ahead.
/// </para>
/// </remarks>
internal sealed class StreamingInput : Stream
{
    private const int MinimumRead = 16384;

    private readonly Stream inner;
    private readonly long origin;
    private byte[]? buffer;

    /// <summary>Index of the next byte to serve.</summary>
    private int head;

    /// <summary>Index one past the last valid byte.</summary>
    private int tail;

    /// <summary>The inner stream's position (seekable) or bytes read so far (non-seekable) at <see cref="tail"/>.</summary>
    private long tailPosition;

    private bool recording;
    private bool innerEnded;

    /// <summary>Wraps <paramref name="inner"/>, which is never disposed by the wrapper.</summary>
    public StreamingInput(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(inner));
        }

        this.inner = inner;
        this.CanSeek = inner.CanSeek;
        if (this.CanSeek)
        {
            this.origin = inner.Position;
            this.tailPosition = this.origin;
        }
        else
        {
            this.recording = true;
        }

        this.buffer = ArrayPool<byte>.Shared.Rent(MinimumRead);
    }

    public override bool CanRead => true;

    public override bool CanSeek { get; }

    public override bool CanWrite => false;

    public override long Length => this.CanSeek ? this.inner.Length : throw new NotSupportedException();

    public override long Position
    {
        get => this.tailPosition - (this.tail - this.head);
        set => this.Seek(value, SeekOrigin.Begin);
    }

    /// <summary>Bytes buffered ahead of the read position.</summary>
    public int Buffered => this.tail - this.head;

    /// <summary>True once the inner stream has reported its end and everything buffered has been read.</summary>
    public bool Exhausted => this.innerEnded && this.head == this.tail;

    /// <summary>How many times a read had to go to the inner stream synchronously.</summary>
    public int SynchronousReads { get; private set; }

    /// <summary>Returns to the position the stream had when it was wrapped.</summary>
    /// <exception cref="InvalidOperationException">A non-seekable input is no longer being recorded.</exception>
    public void Rewind()
    {
        if (this.CanSeek)
        {
            this.Seek(this.origin, SeekOrigin.Begin);
            return;
        }

        if (!this.recording)
        {
            throw new InvalidOperationException("The input is no longer recorded, so it cannot be rewound.");
        }

        this.head = 0;
    }

    /// <summary>Lets the bytes already read be released; <see cref="Rewind"/> is no longer possible on a non-seekable input.</summary>
    public void StopRecording() => this.recording = false;

    /// <summary>Copies up to <paramref name="destination"/>'s length of upcoming bytes without consuming them.</summary>
    public int Peek(Span<byte> destination)
    {
        while (this.Buffered < destination.Length && this.FillOnce(destination.Length - this.Buffered))
        {
        }

        int count = Math.Min(destination.Length, this.Buffered);
        this.buffer.AsSpan(this.head, count).CopyTo(destination);
        return count;
    }

    /// <summary>Reads asynchronously until at least <paramref name="count"/> bytes are buffered or the input ends.</summary>
    public async ValueTask FillAsync(int count, CancellationToken cancellationToken)
    {
        while (this.Buffered < count && !this.innerEnded)
        {
            OperationExecution.Checkpoint();
            int space = this.MakeSpace(count - this.Buffered);
            int read = await this.inner.ReadAsync(this.buffer.AsMemory(this.tail, space), cancellationToken).ConfigureAwait(false);
            this.Advance(read);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return this.Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        if (this.head == this.tail)
        {
            this.SynchronousReads++;
            if (!this.recording && destination.Length >= MinimumRead)
            {
                // Nothing buffered and nothing to keep: a large read goes straight into the caller's buffer.
                ObjectDisposedException.ThrowIf(this.buffer is null, this);
                this.head = 0;
                this.tail = 0;
                if (this.innerEnded)
                {
                    return 0;
                }

                int direct = this.inner.Read(destination);
                if (direct <= 0)
                {
                    this.innerEnded = true;
                    return 0;
                }

                this.tailPosition += direct;
                return direct;
            }

            if (!this.FillOnce(destination.Length))
            {
                return 0;
            }
        }

        int count = Math.Min(destination.Length, this.tail - this.head);
        this.buffer.AsSpan(this.head, count).CopyTo(destination);
        this.head += count;
        return count;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        if (this.head == this.tail)
        {
            await this.FillAsync(1, cancellationToken).ConfigureAwait(false);
        }

        int count = Math.Min(destination.Length, this.tail - this.head);
        this.buffer.AsSpan(this.head, count).CopyTo(destination.Span);
        this.head += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (!this.CanSeek)
        {
            throw new NotSupportedException();
        }

        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => this.Position + offset,
            _ => this.inner.Length + offset,
        };

        long bufferStart = this.tailPosition - this.tail;
        if (target >= bufferStart && target <= this.tailPosition)
        {
            this.head = (int)(target - bufferStart);
            return target;
        }

        this.inner.Position = target;
        this.head = 0;
        this.tail = 0;
        this.tailPosition = target;
        this.innerEnded = false;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (this.buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(this.buffer);
            this.buffer = null;
        }

        base.Dispose(disposing);
    }

    /// <summary>One synchronous read of the inner stream into the buffer; false at its end.</summary>
    private bool FillOnce(int wanted)
    {
        if (this.innerEnded)
        {
            return false;
        }

        // A synchronous read asks for what the caller wants (at least a page), not for all the free space: after a
        // seek the PNG chunk walk only needs the next chunk header.
        int space = this.MakeSpace(wanted);
        int size = Math.Min(space, Math.Max(wanted, 4096));
        this.Advance(this.inner.Read(this.buffer.AsSpan(this.tail, size)));
        return !this.innerEnded;
    }

    private void Advance(int read)
    {
        if (read <= 0)
        {
            this.innerEnded = true;
            return;
        }

        this.tail += read;
        this.tailPosition += read;
    }

    /// <summary>
    /// Makes room for at least <paramref name="wanted"/> (and at least <see cref="MinimumRead"/>) more bytes and
    /// returns the free space: consumed bytes are dropped unless they are being recorded, and the buffer grows when
    /// that is not enough.
    /// </summary>
    private int MakeSpace(int wanted)
    {
        ObjectDisposedException.ThrowIf(this.buffer is null, this);
        wanted = Math.Max(wanted, MinimumRead);
        if (!this.recording && this.head > 0)
        {
            this.buffer.AsSpan(this.head, this.tail - this.head).CopyTo(this.buffer);
            this.tail -= this.head;
            this.head = 0;
        }

        if (this.buffer.Length - this.tail < wanted)
        {
            long needed = (long)this.tail + wanted;
            int size = (int)Math.Min(Array.MaxLength, Math.Max(needed, (long)this.buffer.Length * 2));
            if (size <= this.tail)
            {
                throw new InvalidOperationException("The input buffer cannot grow any further.");
            }

            byte[] grown = ArrayPool<byte>.Shared.Rent(size);
            this.buffer.AsSpan(0, this.tail).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(this.buffer);
            this.buffer = grown;
        }

        return this.buffer.Length - this.tail;
    }
}
