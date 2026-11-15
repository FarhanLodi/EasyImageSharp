using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Streaming;

namespace EasyImageSharp.Formats.Png;

/// <summary>
/// Encodes a PNG one row at a time: each row is filtered as it arrives and fed to a <see cref="ZLibStream"/>
/// whose output is written out as IDAT chunks of at most <see cref="IdatChunkBytes"/> bytes, so neither the
/// frame nor the compressed image is ever held in memory.
/// </summary>
/// <remarks>
/// <para>
/// The row-compatible <see cref="PngEncoder"/> options are honoured exactly: the colour type (grayscale at 8
/// or 16 bits, grayscale with alpha, truecolour, truecolour with alpha; or the one the pixel format implies),
/// the bit depth, every <see cref="PngFilterMethod"/> including the adaptive choice, the compression level and
/// <see cref="PngTransparentColorMode"/>, together with the metadata chunks <see cref="PngEncoder"/> writes.
/// Rows are filtered by the encoder's own code and written to the compressor in the same order and sizes, so
/// the concatenated IDAT payloads equal the single IDAT payload <see cref="PngEncoder"/> writes for the same
/// pixels; only the split into chunks differs.
/// </para>
/// <para>
/// Palette output and grayscale below 8 bits need a quantizer that sees the whole frame, and Adam7 needs every
/// row before the first pass can be written, so <see cref="TryCreate{TPixel}"/> returns <see langword="false"/>
/// for them and the caller collects the image and runs <see cref="PngEncoder"/> instead. The sink always writes
/// a still image: an animated <see cref="PngMetadata"/> does not turn it into an APNG.
/// </para>
/// </remarks>
internal static class PngScanlineSink
{
    /// <summary>The largest IDAT payload the sink writes; compressed data is flushed in chunks of this size.</summary>
    internal const int IdatChunkBytes = 1 << 16;

    /// <summary>Creates a sink for <paramref name="encoder"/>'s options when they can be encoded row by row.</summary>
    /// <param name="output">The stream the PNG is written to; never disposed by the sink.</param>
    /// <param name="encoder">The options to honour.</param>
    /// <param name="sink">The sink, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <typeparam name="TPixel">The pixel format the rows arrive in.</typeparam>
    /// <returns><see langword="false"/> for palette, sub-8-bit grayscale and interlaced output.</returns>
    /// <exception cref="NotSupportedException">The bit depth is not valid for the colour type, as <see cref="PngEncoder"/> reports.</exception>
    public static bool TryCreate<TPixel>(Stream output, PngEncoder encoder, out IScanlineSink<TPixel>? sink)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(encoder);
        if (!output.CanWrite)
        {
            throw new ArgumentException("Stream must be writable.", nameof(output));
        }

        sink = null;
        PngColorType colorType = encoder.ColorType ?? PngEncoder.DefaultColorType<TPixel>();
        int bitDepth = encoder.BitDepth.HasValue ? (int)encoder.BitDepth.Value : 8;
        if (encoder.BitDepth.HasValue)
        {
            PngEncoder.ValidateBitDepth(colorType, bitDepth);
        }

        if (encoder.InterlaceMethod != PngInterlaceMethod.None
            || colorType == PngColorType.Palette
            || (colorType == PngColorType.Grayscale && bitDepth < 8))
        {
            return false;
        }

        sink = new PngScanlineSink<TPixel>(output, encoder, colorType, bitDepth);
        return true;
    }
}

/// <summary>A row-by-row PNG writer; created by <see cref="PngScanlineSink.TryCreate{TPixel}"/>.</summary>
/// <typeparam name="TPixel">The pixel format the rows arrive in.</typeparam>
internal sealed class PngScanlineSink<TPixel> : IScanlineSink<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    private readonly Stream output;
    private readonly PngEncoder encoder;
    private readonly PngColorType colorType;
    private readonly int bitDepth;
    private readonly int channels;
    private readonly int filterBpp;
    private readonly bool clearTransparent;

    private IdatChunkStream? idat;
    private ZLibStream? zlib;
    private Rgba32[]? rgba;
    private byte[]? current;
    private byte[]? previous;
    private byte[]? candidate;

    /// <summary>The filter byte followed by the filtered row: exactly what one compressor write takes.</summary>
    private byte[]? filtered;

    private int width;
    private int height;
    private int bytesPerRow;
    private int rowsWritten;
    private State state;

    internal PngScanlineSink(Stream output, PngEncoder encoder, PngColorType colorType, int bitDepth)
    {
        this.output = output;
        this.encoder = encoder;
        this.colorType = colorType;
        this.bitDepth = bitDepth;
        this.channels = colorType switch
        {
            PngColorType.Grayscale => 1,
            PngColorType.GrayscaleWithAlpha => 2,
            PngColorType.Rgb => 3,
            _ => 4,
        };
        this.filterBpp = Math.Max(1, this.channels * bitDepth / 8);
        this.clearTransparent = encoder.TransparentColorMode == PngTransparentColorMode.Clear
            && colorType is PngColorType.RgbWithAlpha or PngColorType.GrayscaleWithAlpha;
    }

    private enum State
    {
        Created,
        Writing,
        Completed,
        Disposed,
    }

    public void Begin(int width, int height, ImageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        this.EnsureState(State.Created, nameof(this.Begin));
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        long rowBytes = (((long)width * this.channels * this.bitDepth) + 7) / 8;
        if (rowBytes + 1 > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "PNG scanlines this wide exceed the largest addressable buffer.");
        }

        this.width = width;
        this.height = height;
        this.bytesPerRow = (int)rowBytes;

        // Signature, IHDR and the metadata chunks, byte for byte as PngEncoder writes them for a still image.
        this.output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = (byte)this.bitDepth;
        ihdr[9] = (byte)this.colorType;
        ihdr[10] = 0; // Compression method: deflate.
        ihdr[11] = 0; // Filter method: adaptive.
        ihdr[12] = 0; // No interlacing.
        PngMetadataChunks.WriteChunk(this.output, "IHDR"u8, ihdr);
        PngMetadataChunks.Write(this.output, metadata);

        this.rgba = ArrayPool<Rgba32>.Shared.Rent(width);
        this.current = ArrayPool<byte>.Shared.Rent(this.bytesPerRow);
        this.previous = ArrayPool<byte>.Shared.Rent(this.bytesPerRow);
        this.candidate = ArrayPool<byte>.Shared.Rent(this.bytesPerRow);
        this.filtered = ArrayPool<byte>.Shared.Rent(this.bytesPerRow + 1);
        this.idat = new IdatChunkStream(this.output);
        this.zlib = new ZLibStream(this.idat, this.encoder.CompressionLevel, leaveOpen: true);
        this.state = State.Writing;
    }

    public void WriteRow(ReadOnlySpan<TPixel> row)
    {
        this.EnsureState(State.Writing, nameof(this.WriteRow));
        if (row.Length < this.width)
        {
            throw new ArgumentException($"The row holds {row.Length} pixels but the image is {this.width} wide.", nameof(row));
        }

        if (this.rowsWritten >= this.height)
        {
            throw new InvalidOperationException($"All {this.height} rows have already been written.");
        }

        Span<Rgba32> pixels = this.rgba.AsSpan(0, this.width);
        PixelOps.ToRgba32(row[..this.width], pixels);
        if (this.clearTransparent)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].A == 0)
                {
                    pixels[i] = default;
                }
            }
        }

        Span<byte> samples = this.current.AsSpan(0, this.bytesPerRow);
        this.Pack(pixels, samples);
        Span<byte> filteredRow = this.filtered.AsSpan(0, this.bytesPerRow + 1);
        filteredRow[0] = (byte)PngEncoder.FilterScanline(
            this.encoder.FilterMethod,
            samples,
            this.rowsWritten == 0 ? default : this.previous.AsSpan(0, this.bytesPerRow),
            this.filterBpp,
            this.candidate.AsSpan(0, this.bytesPerRow),
            filteredRow[1..]);
        this.zlib!.Write(filteredRow);
        (this.previous, this.current) = (this.current, this.previous);
        this.rowsWritten++;
    }

    public void Complete()
    {
        this.EnsureState(State.Writing, nameof(this.Complete));
        if (this.rowsWritten != this.height)
        {
            throw new InvalidOperationException($"Only {this.rowsWritten} of {this.height} rows were written.");
        }

        this.zlib!.Dispose(); // Writes the final deflate block and the ADLER-32 trailer into the IDAT buffer.
        this.zlib = null;
        this.idat!.FlushChunk();
        PngMetadataChunks.WriteChunk(this.output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        this.ReleaseBuffers();
        this.state = State.Completed;
    }

    public void Dispose()
    {
        if (this.state == State.Disposed)
        {
            return;
        }

        this.state = State.Disposed;

        // Abandoned part-way: the compressor's final block must not reach the output (which may be what failed).
        this.idat?.Abandon();
        this.zlib?.Dispose();
        this.zlib = null;
        this.ReleaseBuffers();
    }

    /// <summary>
    /// Writes the samples of one row for the output colour type, exactly as <see cref="PngEncoder"/>'s
    /// scanline source does: grayscale from <see cref="PixelOps.Luminance8"/>, 16-bit samples by widening the
    /// 8-bit value (<c>v * 257</c>).
    /// </summary>
    private void Pack(ReadOnlySpan<Rgba32> pixels, Span<byte> destination)
    {
        bool wide = this.bitDepth == 16;
        int o = 0;
        switch (this.colorType)
        {
            case PngColorType.Grayscale:
                foreach (Rgba32 p in pixels)
                {
                    o = WriteSample(destination, o, PixelOps.Luminance8(p), wide);
                }

                break;
            case PngColorType.GrayscaleWithAlpha:
                foreach (Rgba32 p in pixels)
                {
                    o = WriteSample(destination, o, PixelOps.Luminance8(p), wide);
                    o = WriteSample(destination, o, p.A, wide);
                }

                break;
            case PngColorType.Rgb:
                foreach (Rgba32 p in pixels)
                {
                    o = WriteSample(destination, o, p.R, wide);
                    o = WriteSample(destination, o, p.G, wide);
                    o = WriteSample(destination, o, p.B, wide);
                }

                break;
            default:
                if (!wide)
                {
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels).CopyTo(destination);
                    break;
                }

                foreach (Rgba32 p in pixels)
                {
                    o = WriteSample(destination, o, p.R, wide);
                    o = WriteSample(destination, o, p.G, wide);
                    o = WriteSample(destination, o, p.B, wide);
                    o = WriteSample(destination, o, p.A, wide);
                }

                break;
        }
    }

    private static int WriteSample(Span<byte> destination, int offset, byte value, bool wide)
    {
        destination[offset++] = value;
        if (wide)
        {
            destination[offset++] = value;
        }

        return offset;
    }

    private void EnsureState(State expected, string operation)
    {
        ObjectDisposedException.ThrowIf(this.state == State.Disposed, this);
        if (this.state != expected)
        {
            throw new InvalidOperationException($"{operation} is not valid once the sink is {this.state}.");
        }
    }

    private void ReleaseBuffers()
    {
        this.idat?.Dispose();
        this.idat = null;
        Return(ref this.rgba);
        Return(ref this.current);
        Return(ref this.previous);
        Return(ref this.candidate);
        Return(ref this.filtered);
    }

    private static void Return<T>(ref T[]? array)
    {
        if (array is not null)
        {
            ArrayPool<T>.Shared.Return(array);
            array = null;
        }
    }

    /// <summary>
    /// Collects the compressor's output and writes it to the PNG as IDAT chunks of
    /// <see cref="PngScanlineSink.IdatChunkBytes"/> bytes, plus one shorter chunk for the remainder.
    /// </summary>
    private sealed class IdatChunkStream : Stream
    {
        private readonly Stream output;
        private byte[]? buffer = ArrayPool<byte>.Shared.Rent(PngScanlineSink.IdatChunkBytes);
        private int count;
        private bool abandoned;

        public IdatChunkStream(Stream output) => this.output = output;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            this.Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> data)
        {
            if (this.abandoned)
            {
                return;
            }

            ObjectDisposedException.ThrowIf(this.buffer is null, this);
            while (!data.IsEmpty)
            {
                int n = Math.Min(data.Length, PngScanlineSink.IdatChunkBytes - this.count);
                data[..n].CopyTo(this.buffer.AsSpan(this.count));
                this.count += n;
                data = data[n..];
                if (this.count == PngScanlineSink.IdatChunkBytes)
                {
                    this.FlushChunk();
                }
            }
        }

        /// <summary>Discards everything written from now on; used when the image is abandoned.</summary>
        public void Abandon() => this.abandoned = true;

        /// <summary>Writes whatever is buffered as one IDAT chunk.</summary>
        public void FlushChunk()
        {
            if (!this.abandoned && this.buffer is not null && this.count > 0)
            {
                PngMetadataChunks.WriteChunk(this.output, "IDAT"u8, this.buffer.AsSpan(0, this.count));
                this.count = 0;
            }
        }

        // The compressor flushes its own buffer as it goes; chunks are only cut when full or at the end.
        public override void Flush()
        {
        }

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
}
