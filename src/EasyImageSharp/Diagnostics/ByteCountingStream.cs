namespace EasyImageSharp.Diagnostics;

/// <summary>
/// A write-only pass-through that counts the bytes written to a non-seekable stream (an HTTP response body, a pipe),
/// so an encode can report its size when there is no position to subtract. Used only while telemetry is recording.
/// The wrapped stream is never closed by this wrapper.
/// </summary>
internal sealed class ByteCountingStream : Stream
{
    private readonly Stream inner;

    public ByteCountingStream(Stream inner) => this.inner = inner;

    /// <summary>The number of bytes written through this wrapper.</summary>
    public long BytesWritten { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => this.inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => this.inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => this.inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        this.inner.Write(buffer, offset, count);
        this.BytesWritten += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        this.inner.Write(buffer);
        this.BytesWritten += buffer.Length;
    }

    public override void WriteByte(byte value)
    {
        this.inner.WriteByte(value);
        this.BytesWritten++;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await this.inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        this.BytesWritten += count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await this.inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        this.BytesWritten += buffer.Length;
    }

    protected override void Dispose(bool disposing)
    {
        // The wrapped stream belongs to the caller.
        base.Dispose(disposing);
    }
}
