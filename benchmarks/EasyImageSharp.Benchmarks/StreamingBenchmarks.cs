using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using BenchmarkDotNet.Attributes;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Streaming;

namespace EasyImageSharp.Benchmarks;

/// <summary>
/// Thumbnailing a large PNG: <see cref="Image.Transcode(Stream, Stream, TranscodeOptions?, CancellationToken)"/>,
/// which decodes, resizes and encodes row by row, against the in-memory Load -> Resize -> Save it replaces.
/// <para>
/// The input is a non-interlaced RGBA PNG of <see cref="Megapixels"/> million pixels, written to a temporary
/// file once per parameter by the minimal PNG writer at the bottom of this file - the runtime's
/// <see cref="ZLibStream"/> and a hand-written chunk layout, not this library's encoder - so the decode reads a
/// foreign encoder's output, as the corpus does. It is too large to commit and too slow for Pillow to be worth
/// adding to the corpus script, and it depicts a smooth gradient with a little noise, which compresses about as
/// well as a photograph does in PNG.
/// </para>
/// <para>
/// Both methods read the file through a <see cref="FileStream"/> and write the 320x240 thumbnail to
/// <see cref="Stream.Null"/>. BenchmarkDotNet's Allocated column is the managed allocation per operation. The
/// peak managed heap of one run of each method - which is what decides whether a thumbnail service fits in its
/// container - is sampled in the global setup and printed to the log as <c>// peak managed heap</c>; it counts
/// pooled buffers, which the Allocated column does not see once the pools are warm.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class StreamingBenchmarks
{
    private static readonly ResizeOptions Thumbnail = new() { Size = new Size(320, 240), Mode = ResizeMode.Max };

    private static readonly TranscodeOptions TranscodeThumbnail = new() { Resize = Thumbnail, Encoder = new PngEncoder() };

    private string path = string.Empty;

    /// <summary>The source size: 24 MP is 6000x4000, 96 MP is 12000x8000.</summary>
    [Params(24, 96)]
    public int Megapixels { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        (int width, int height) = this.Megapixels == 96 ? (12000, 8000) : (6000, 4000);
        this.path = Path.Combine(Path.GetTempPath(), $"easyimagesharp-streaming-{width}x{height}.png");
        if (!File.Exists(this.path))
        {
            LargePngWriter.Write(this.path, width, height);
        }

        Report("Load+Resize+Save", PeakHeap.Measure(this.LoadResizeSave));
        Report("Transcode", PeakHeap.Measure(this.StreamedTranscode));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (File.Exists(this.path))
        {
            File.Delete(this.path);
        }
    }

    [Benchmark(Baseline = true)]
    public void LoadResizeSave()
    {
        using FileStream input = File.OpenRead(this.path);
        using Image<Rgba32> image = Image.Load<Rgba32>(input);
        image.Mutate(c => c.Resize(Thumbnail));
        image.Save(Stream.Null, new PngEncoder());
    }

    [Benchmark]
    public void StreamedTranscode()
    {
        using FileStream input = File.OpenRead(this.path);
        Image.Transcode(input, Stream.Null, TranscodeThumbnail);
    }

    private void Report(string method, long bytes)
        => Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"// peak managed heap, {method}, {this.Megapixels} MP: {bytes / (1024.0 * 1024.0):F1} MiB above the idle heap"));

    /// <summary>
    /// Samples <see cref="GC.GetTotalMemory(bool)"/> on a background thread while an action runs, after a full
    /// collection, and returns the highest reading above the starting heap. Sampling every millisecond can miss a
    /// short spike, so the figure is a lower bound on the true peak - but the frame a full decode allocates lives
    /// for the whole operation, so it is never missed.
    /// </summary>
    private static class PeakHeap
    {
        public static long Measure(Action action)
        {
            action(); // Warm the JIT and the pools first.
            long baseline = GC.GetTotalMemory(forceFullCollection: true);
            long peak = baseline;
            using var stop = new ManualResetEventSlim();
            var sampler = new Thread(() =>
            {
                while (!stop.IsSet)
                {
                    peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
                    stop.Wait(1);
                }
            })
            {
                IsBackground = true,
            };

            sampler.Start();
            action();
            peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
            stop.Set();
            sampler.Join();
            return Math.Max(0, peak - baseline);
        }
    }

    /// <summary>
    /// Writes an 8-bit RGBA PNG straight from the specification: signature, IHDR, IDAT chunks of 64 KiB from one
    /// <see cref="ZLibStream"/>, IEND. Rows are produced and compressed one at a time.
    /// </summary>
    private static class LargePngWriter
    {
        public static void Write(string path, int width, int height)
        {
            using FileStream file = File.Create(path);
            file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            var ihdr = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
            ihdr[8] = 8; // Bit depth.
            ihdr[9] = 6; // Truecolour with alpha.
            WriteChunk(file, "IHDR"u8, ihdr);

            using (var idat = new IdatWriter(file))
            {
                using var zlib = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true);
                var row = new byte[1 + (width * 4)];
                uint state = 2463534242;
                for (int y = 0; y < height; y++)
                {
                    row[0] = 0; // Filter type None; the compressor still finds the gradient's redundancy.
                    for (int x = 0; x < width; x++)
                    {
                        state ^= state << 13;
                        state ^= state >> 17;
                        state ^= state << 5;
                        int noise = (int)(state & 7) - 4;
                        int i = 1 + (x * 4);
                        row[i] = (byte)Math.Clamp((x * 255 / width) + noise, 0, 255);
                        row[i + 1] = (byte)Math.Clamp((y * 255 / height) + noise, 0, 255);
                        row[i + 2] = (byte)Math.Clamp(((x + y) * 127 / (width + height)) + 64 + noise, 0, 255);
                        row[i + 3] = 255;
                    }

                    zlib.Write(row);
                }
            }

            WriteChunk(file, "IEND"u8, []);
        }

        private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
            type.CopyTo(header[4..]);
            stream.Write(header);
            stream.Write(data);
            uint crc = Crc32(Crc32(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
            Span<byte> trailer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(trailer, crc);
            stream.Write(trailer);
        }

        /// <summary>The PNG CRC-32 register update, bit by bit; slow, but it only runs over chunk payloads once.</summary>
        private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte value in data)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }

            return crc;
        }

        /// <summary>Cuts the compressed stream into IDAT chunks of 64 KiB.</summary>
        private sealed class IdatWriter(Stream file) : Stream
        {
            private readonly byte[] buffer = new byte[1 << 16];
            private int count;

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> data)
            {
                while (!data.IsEmpty)
                {
                    int n = Math.Min(data.Length, this.buffer.Length - this.count);
                    data[..n].CopyTo(this.buffer.AsSpan(this.count));
                    this.count += n;
                    data = data[n..];
                    if (this.count == this.buffer.Length)
                    {
                        this.Flush();
                    }
                }
            }

            public override void Flush()
            {
                if (this.count > 0)
                {
                    WriteChunk(file, "IDAT"u8, this.buffer.AsSpan(0, this.count));
                    this.count = 0;
                }
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    this.Flush();
                }

                base.Dispose(disposing);
            }
        }
    }
}
