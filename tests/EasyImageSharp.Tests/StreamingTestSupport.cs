using System.Buffers.Binary;
using System.IO.Compression;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Streaming;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>Shared helpers for the row-streaming tests.</summary>
internal static class StreamingTestSupport
{
    /// <summary>Every row of a source, concatenated.</summary>
    public static TPixel[] ReadAll<TPixel>(IScanlineSource<TPixel> source)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var pixels = new TPixel[source.Width * source.Height];
        int y = 0;
        var row = new TPixel[source.Width + 3]; // Longer than a row on purpose: the source must only use Width.
        while (source.ReadRow(row))
        {
            row.AsSpan(0, source.Width).CopyTo(pixels.AsSpan(y * source.Width));
            y++;
            Assert.Equal(y, source.CurrentRow);
        }

        Assert.Equal(source.Height, y);
        Assert.False(source.ReadRow(row));
        return pixels;
    }

    /// <summary>The pixels of the root frame of an image.</summary>
    public static TPixel[] Pixels<TPixel>(Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var pixels = new TPixel[image.Width * image.Height];
        image.Frames.RootFrame.CopyPixelDataTo(pixels);
        return pixels;
    }

    /// <summary>A deterministic image with every channel varying and alpha from fully transparent to opaque.</summary>
    public static Image<TPixel> Noise<TPixel>(int width, int height, int seed, bool opaque = false)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var random = new Random(seed);
        var image = new Image<TPixel>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Smooth structure plus noise, so resampling has edges and gradients to work on.
                int r = ((x * 255 / Math.Max(1, width - 1)) + random.Next(-40, 41)) & 0xFF;
                int g = ((y * 255 / Math.Max(1, height - 1)) + random.Next(-40, 41)) & 0xFF;
                int b = random.Next(256);
                int a = opaque ? 255 : (x + y) % 7 == 0 ? 0 : random.Next(256);
                image[x, y] = TPixel.FromRgba32(new Rgba32((byte)r, (byte)g, (byte)b, (byte)a));
            }
        }

        return image;
    }

    /// <summary>Encodes an image with an encoder into a byte array.</summary>
    public static byte[] Encode(Image image, Formats.IImageEncoder encoder)
    {
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    /// <summary>The chunks of a PNG file, in order, as (type, payload).</summary>
    public static List<(string Type, byte[] Data)> Chunks(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        int pos = 8;
        while (pos + 12 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            chunks.Add((type, png.AsSpan(pos + 8, length).ToArray()));
            pos += 12 + length;
        }

        return chunks;
    }

    /// <summary>
    /// Asserts two PNG files hold the same chunks in the same order, except that the image data may be split
    /// differently: the concatenated IDAT payloads must be byte-identical. Every chunk's CRC must be right.
    /// </summary>
    public static void AssertSamePngModuloIdatSplit(byte[] expected, byte[] actual)
    {
        AssertCrcs(expected);
        AssertCrcs(actual);
        List<(string Type, byte[] Data)> left = Chunks(expected);
        List<(string Type, byte[] Data)> right = Chunks(actual);
        Assert.Equal(Merge(left).Select(c => c.Type), Merge(right).Select(c => c.Type));
        foreach (((string Type, byte[] Data) l, (string Type, byte[] Data) r) in Merge(left).Zip(Merge(right)))
        {
            Assert.True(l.Data.AsSpan().SequenceEqual(r.Data), $"{l.Type} chunks differ.");
        }
    }

    /// <summary>Asserts every chunk of a PNG carries the CRC-32 of its type and payload.</summary>
    public static void AssertCrcs(byte[] png)
    {
        int pos = 8;
        while (pos + 12 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + length));
            Assert.Equal(ReferenceCrc32(png.AsSpan(pos + 4, 4 + length)), stored);
            pos += 12 + length;
        }

        Assert.Equal(png.Length, pos);
    }

    /// <summary>
    /// A hand-assembled PNG - written byte by byte from the specification, not by the library - of the given
    /// IHDR facts and raw (already filtered, filter type 0) scanlines, with optional chunks before and after IDAT.
    /// </summary>
    public static byte[] AssemblePng(
        int width, int height, byte bitDepth, byte colorType, byte[] scanlines,
        IEnumerable<(string Type, byte[] Data)>? before = null, IEnumerable<(string Type, byte[] Data)>? after = null,
        int idatChunkBytes = int.MaxValue, bool interlaced = false)
    {
        using var file = new MemoryStream();
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        ihdr[12] = interlaced ? (byte)1 : (byte)0;
        WriteChunk(file, "IHDR", ihdr);
        foreach ((string type, byte[] data) in before ?? [])
        {
            WriteChunk(file, type, data);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(scanlines);
        }

        byte[] payload = compressed.ToArray();
        for (int offset = 0; offset < payload.Length; offset += idatChunkBytes)
        {
            WriteChunk(file, "IDAT", payload.AsSpan(offset, Math.Min(idatChunkBytes, payload.Length - offset)).ToArray());
        }

        foreach ((string type, byte[] data) in after ?? [])
        {
            WriteChunk(file, type, data);
        }

        WriteChunk(file, "IEND", []);
        return file.ToArray();
    }

    /// <summary>Raw RGBA8 scanlines (filter type 0) of a deterministic pattern.</summary>
    public static byte[] RgbaScanlines(int width, int height, int seed)
    {
        var random = new Random(seed);
        var data = new byte[height * ((width * 4) + 1)];
        for (int y = 0; y < height; y++)
        {
            int row = y * ((width * 4) + 1);
            for (int i = 1; i <= width * 4; i++)
            {
                data[row + i] = (byte)random.Next(256);
            }
        }

        return data;
    }

    public static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        System.Text.Encoding.ASCII.GetBytes(type, header[4..]);
        stream.Write(header);
        stream.Write(data);
        var crcInput = new byte[4 + data.Length];
        header[4..].CopyTo(crcInput);
        data.CopyTo(crcInput, 4);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, ReferenceCrc32(crcInput));
        stream.Write(crc);
    }

    /// <summary>CRC-32 (ISO-HDLC, as PNG uses it), written out from the specification rather than taken from the library.</summary>
    public static uint ReferenceCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static List<(string Type, byte[] Data)> Merge(List<(string Type, byte[] Data)> chunks)
    {
        var merged = new List<(string, byte[])>();
        foreach ((string type, byte[] data) in chunks)
        {
            if (type == "IDAT" && merged.Count > 0 && merged[^1].Item1 == "IDAT")
            {
                merged[^1] = ("IDAT", [.. merged[^1].Item2, .. data]);
            }
            else
            {
                merged.Add((type, data));
            }
        }

        return merged;
    }
}

/// <summary>A sink that keeps every row it is given, for inspecting what a pipeline stage produced.</summary>
internal sealed class CollectingSink<TPixel> : IScanlineSink<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    private readonly List<TPixel[]> rows = new();

    public int Width { get; private set; }

    public int Height { get; private set; }

    public ImageMetadata? Metadata { get; private set; }

    public bool Completed { get; private set; }

    public bool Disposed { get; private set; }

    public int RowsWritten => this.rows.Count;

    public void Begin(int width, int height, ImageMetadata metadata)
    {
        Assert.Null(this.Metadata);
        this.Width = width;
        this.Height = height;
        this.Metadata = metadata;
    }

    public void WriteRow(ReadOnlySpan<TPixel> row)
    {
        Assert.NotNull(this.Metadata);
        Assert.False(this.Completed);
        Assert.True(this.rows.Count < this.Height, "More rows than Begin announced.");
        Assert.True(row.Length >= this.Width);
        this.rows.Add(row[..this.Width].ToArray());
    }

    public void Complete()
    {
        Assert.Equal(this.Height, this.rows.Count);
        this.Completed = true;
    }

    public void Dispose() => this.Disposed = true;

    public TPixel[] Pixels() => this.rows.SelectMany(r => r).ToArray();
}

/// <summary>
/// A forward-only view of a byte array: not seekable, optionally handing out at most a few bytes per read, and
/// optionally failing or cancelling once a given number of bytes has been read.
/// </summary>
internal sealed class ForwardOnlyStream : Stream
{
    private readonly byte[] data;
    private readonly int maxChunk;
    private int position;

    public ForwardOnlyStream(byte[] data, int maxChunk = int.MaxValue)
    {
        this.data = data;
        this.maxChunk = maxChunk;
    }

    /// <summary>When set, a read that would pass this many bytes throws <see cref="FailWith"/>.</summary>
    public int FailAfter { get; init; } = int.MaxValue;

    public Exception FailWith { get; init; } = new IOException("The connection was reset.");

    /// <summary>When set, cancelled once this many bytes have been read.</summary>
    public CancellationTokenSource? CancelAfterSource { get; init; }

    public int CancelAfter { get; init; } = int.MaxValue;

    public int BytesRead => this.position;

    public int SyncReads { get; private set; }

    public int AsyncReads { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        this.SyncReads++;
        return this.ReadCore(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.AsyncReads++;
        return ValueTask.FromResult(this.ReadCore(buffer.Span));
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int ReadCore(Span<byte> buffer)
    {
        int count = Math.Min(Math.Min(buffer.Length, this.maxChunk), this.data.Length - this.position);
        if (this.position + count > this.FailAfter)
        {
            throw this.FailWith;
        }

        this.data.AsSpan(this.position, count).CopyTo(buffer);
        this.position += count;
        if (this.position >= this.CancelAfter)
        {
            this.CancelAfterSource?.Cancel();
        }

        return count;
    }
}

/// <summary>A write-only, non-seekable output that records what was written and counts synchronous writes.</summary>
internal sealed class RecordingOutputStream : Stream
{
    private readonly MemoryStream inner = new();

    public int SyncWrites { get; private set; }

    public int AsyncWrites { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public byte[] ToArray() => this.inner.ToArray();

    public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        this.SyncWrites++;
        this.inner.Write(buffer);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.AsyncWrites++;
        this.inner.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
