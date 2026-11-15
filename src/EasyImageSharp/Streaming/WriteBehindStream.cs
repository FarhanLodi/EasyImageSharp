using System.Buffers;

namespace EasyImageSharp.Streaming;

/// <summary>
/// The asynchronous transcoder's output: synchronous writes from the row encoders land in a buffer, and the
/// pipeline drains it to the caller's stream asynchronously between rows. The buffer only ever holds what one
/// row (or, for an encoder that writes a whole image at the end, one image) produced.
/// </summary>
internal sealed class WriteBehindStream : Stream
{
    private readonly Stream inner;
    private byte[]? buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
    private int count;

    /// <summary>Wraps <paramref name="inner"/>, which is never disposed by the wrapper.</summary>
    public WriteBehindStream(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Bytes written but not yet drained.</summary>
    public int Buffered => this.count;

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        this.Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(this.buffer is null, this);
        if (this.buffer.Length - this.count < data.Length)
        {
            long needed = (long)this.count + data.Length;
            if (needed > Array.MaxLength)
            {
                throw new IOException("The pending output exceeds the largest addressable buffer.");
            }

            byte[] grown = ArrayPool<byte>.Shared.Rent((int)Math.Min(Array.MaxLength, Math.Max(needed, (long)this.buffer.Length * 2)));
            this.buffer.AsSpan(0, this.count).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(this.buffer);
            this.buffer = grown;
        }

        data.CopyTo(this.buffer.AsSpan(this.count));
        this.count += data.Length;
    }

    public override void WriteByte(byte value) => this.Write([value]);

    /// <summary>Writes everything buffered to the inner stream once at least <paramref name="threshold"/> bytes are pending.</summary>
    public async ValueTask DrainAsync(int threshold, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.buffer is null, this);
        if (this.count == 0 || this.count < threshold)
        {
            return;
        }

        await this.inner.WriteAsync(this.buffer.AsMemory(0, this.count), cancellationToken).ConfigureAwait(false);
        this.count = 0;
    }

    /// <summary>Synchronous flush is a no-op: the pipeline drains asynchronously.</summary>
    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (this.buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(this.buffer);
            this.buffer = null;
        }

        base.Dispose(disposing);
    }
}
