using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using BenchmarkDotNet.Attributes;

namespace EasyImageSharp.Benchmarks;

/// <summary>
/// The PNG inflate backend: the library's managed <c>Inflater</c> against the runtime's
/// <see cref="ZLibStream"/>, on the IDAT stream of a real PNG.
/// <para>
/// This is the measurement the backend decision rests on, and it has to be taken per target framework:
/// .NET 8's <see cref="ZLibStream"/> calls the classic native zlib, .NET 10's calls zlib-ng, which is
/// hand-written SIMD and much faster on some inputs. The same managed inflater can therefore be the right
/// choice on one framework and the wrong one on the next, and the answer moves again whenever the runtime's
/// zlib does. Do not carry a ratio forward from a previous release; re-run both frameworks:
/// </para>
/// <code>
/// dotnet run -c Release -f net8.0  --project benchmarks/EasyImageSharp.Benchmarks -- --filter "*Inflate*"
/// dotnet run -c Release -f net10.0 --project benchmarks/EasyImageSharp.Benchmarks -- --filter "*Inflate*"
/// </code>
/// <para>
/// The comparison is deliberately unfair to the managed inflater: <see cref="ZLibStreamRows"/> is the shape
/// the decoder used before the managed backend existed - every IDAT chunk concatenated into one pooled
/// buffer, then read a scanline at a time - and <see cref="InflaterRows"/> is the streaming shape, which
/// takes each scanline as a span into the inflater's own window and never copies. The concatenation copy
/// the streaming path removes is thus counted in ZLibStream's favour, so a win here is a lower bound.
/// </para>
/// <para>
/// <c>Inflater</c>, <c>InflateTables</c>, <c>Adler32</c> and <c>SimdConfig</c> are internal to EasyImageSharp;
/// the project file compiles those four source files into this assembly as well, so this benchmark can never
/// drift from the implementation the library ships. <see cref="Setup"/> checks the two backends produce
/// identical bytes before either is timed.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class InflateBenchmarks
{
    /// <summary>
    /// A photograph and a document page: the two ends of the compressibility range, which is exactly the
    /// axis the two backends differ on. photo.png is 10 MiB, where inflate is dominated by literal decoding;
    /// scan.png compresses about 13:1, where it is dominated by match copying. A decision taken from only
    /// one of them is not a decision.
    /// </summary>
    public static IEnumerable<string> Sources => ["photo.png", "scan.png"];

    [ParamsSource(nameof(Sources))]
    public string Source { get; set; } = "photo.png";

    private byte[][] idat = [];
    private int stride;
    private int height;

    [GlobalSetup]
    public void Setup()
    {
        byte[] file = Corpus.Bytes(this.Source);
        PngImageData image = PngImageData.Parse(file, this.Source);
        this.idat = image.Idat;
        this.stride = image.Stride;
        this.height = image.Height;

        // Both backends must agree before either is timed. A backend that is fast and wrong is worthless,
        // and this is the cheapest place to say so.
        byte[] viaZlib = this.DecompressWithZLibStream();
        byte[] viaInflater = this.DecompressWithInflater();
        if (!viaZlib.AsSpan().SequenceEqual(viaInflater))
        {
            throw new InvalidOperationException(
                $"The two inflate backends disagree on {this.Source}: {viaZlib.Length} bytes from ZLibStream, " +
                $"{viaInflater.Length} from Inflater.");
        }
    }

    /// <summary>Concatenate every IDAT chunk, then read scanlines out of a <see cref="ZLibStream"/>.</summary>
    [Benchmark(Baseline = true)]
    public long ZLibStreamRows()
    {
        byte[] compressed = ArrayPool<byte>.Shared.Rent(this.TotalIdatBytes);
        byte[] row = ArrayPool<byte>.Shared.Rent(this.stride);
        try
        {
            int length = this.Concatenate(compressed);
            using var input = new MemoryStream(compressed, 0, length, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);

            long total = 0;
            Span<byte> scanline = row.AsSpan(0, this.stride);
            for (int y = 0; y < this.height; y++)
            {
                total += zlib.ReadAtLeast(scanline, this.stride, throwOnEndOfStream: true);
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(row);
            ArrayPool<byte>.Shared.Return(compressed);
        }
    }

    /// <summary>Feed the IDAT chunks straight to the inflater and take scanlines without copying them.</summary>
    [Benchmark]
    public long InflaterRows()
    {
        var inflater = new Inflater(this.stride, "PNG image");
        try
        {
            long total = 0;
            int segment = 0;
            inflater.SetInput(this.idat[0]);

            for (int y = 0; y < this.height; y++)
            {
                InflateStatus status = inflater.Fill(this.stride);
                while (status == InflateStatus.NeedInput && segment + 1 < this.idat.Length)
                {
                    inflater.SetInput(this.idat[++segment]);
                    status = inflater.Fill(this.stride);
                }

                if (status != InflateStatus.Output)
                {
                    throw new InvalidOperationException(
                        $"{this.Source} ended after {y} of {this.height} scanlines.");
                }

                total += inflater.Take(this.stride).Length;
            }

            return total;
        }
        finally
        {
            inflater.Dispose();
        }
    }

    private int TotalIdatBytes
    {
        get
        {
            int total = 0;
            foreach (byte[] segment in this.idat)
            {
                total += segment.Length;
            }

            return total;
        }
    }

    private int Concatenate(byte[] destination)
    {
        int written = 0;
        foreach (byte[] segment in this.idat)
        {
            segment.CopyTo(destination.AsSpan(written));
            written += segment.Length;
        }

        return written;
    }

    private byte[] DecompressWithZLibStream()
    {
        byte[] compressed = new byte[this.TotalIdatBytes];
        this.Concatenate(compressed);
        using var input = new MemoryStream(compressed, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        byte[] output = new byte[(long)this.stride * this.height];
        zlib.ReadExactly(output);
        return output;
    }

    private byte[] DecompressWithInflater()
    {
        byte[] output = new byte[(long)this.stride * this.height];
        var inflater = new Inflater(this.stride, "PNG image");
        try
        {
            int segment = 0;
            inflater.SetInput(this.idat[0]);
            int written = 0;
            while (written < output.Length)
            {
                int read = inflater.ReadInto(output.AsSpan(written));
                written += read;
                if (written >= output.Length)
                {
                    break;
                }

                // A finished stream that produced nothing has nothing left to give, and feeding it further
                // segments would spin forever rather than fail.
                if (read == 0 && inflater.Finished)
                {
                    throw new InvalidOperationException(
                        $"{this.Source} decompressed to {written} bytes, not the {output.Length} its header claims.");
                }

                if (segment + 1 >= this.idat.Length)
                {
                    throw new InvalidOperationException($"{this.Source} has fewer scanlines than its header claims.");
                }

                inflater.SetInput(this.idat[++segment]);
            }

            return output;
        }
        finally
        {
            inflater.Dispose();
        }
    }
}

/// <summary>
/// The few bytes of a PNG file this benchmark needs: the IHDR geometry and the IDAT chunks, untouched.
/// <para>
/// It is hand-rolled rather than taken from the library's decoder because the point of the benchmark is to
/// time the decompressor in isolation. Reading the chunk framing with the decoder would drag the decoder's
/// own buffering into the measurement, and the framing itself is twenty lines.
/// </para>
/// </summary>
internal sealed class PngImageData
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private PngImageData(int width, int height, int stride, byte[][] idat)
    {
        this.Width = width;
        this.Height = height;
        this.Stride = stride;
        this.Idat = idat;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Bytes per scanline including the leading filter byte, which is what the decoder takes at a time.</summary>
    public int Stride { get; }

    /// <summary>The IDAT chunk payloads, in file order; concatenated they are one zlib stream.</summary>
    public byte[][] Idat { get; }

    public static PngImageData Parse(byte[] file, string name)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Length < 8 || !file.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new InvalidOperationException($"{name} is not a PNG file.");
        }

        int width = 0;
        int height = 0;
        int stride = 0;
        List<byte[]> chunks = [];

        int position = 8;
        while (position + 8 <= file.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(position, 4));
            string type = System.Text.Encoding.ASCII.GetString(file, position + 4, 4);
            int payload = position + 8;
            if (length < 0 || payload + length + 4 > file.Length)
            {
                throw new InvalidOperationException($"{name} has a truncated {type} chunk.");
            }

            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(payload, 4));
                height = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(payload + 4, 4));
                int bitDepth = file[payload + 8];
                int colorType = file[payload + 9];
                if (file[payload + 12] != 0)
                {
                    throw new InvalidOperationException(
                        $"{name} is interlaced; the inflate benchmark wants one scanline stream, not seven passes.");
                }

                int channels = colorType switch
                {
                    0 or 3 => 1,
                    2 => 3,
                    4 => 2,
                    6 => 4,
                    _ => throw new InvalidOperationException($"{name} has an unknown colour type {colorType}."),
                };

                stride = 1 + (int)((((long)width * channels * bitDepth) + 7) / 8);
            }
            else if (type == "IDAT")
            {
                chunks.Add(file.AsSpan(payload, length).ToArray());
            }
            else if (type == "IEND")
            {
                break;
            }

            position = payload + length + 4;
        }

        if (stride == 0 || chunks.Count == 0)
        {
            throw new InvalidOperationException($"{name} carries no IHDR or no IDAT.");
        }

        return new PngImageData(width, height, stride, [.. chunks]);
    }
}
