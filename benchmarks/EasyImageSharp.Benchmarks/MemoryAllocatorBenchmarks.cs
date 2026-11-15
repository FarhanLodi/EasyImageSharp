using BenchmarkDotNet.Attributes;
using EasyImageSharp.Formats;
using EasyImageSharp.Memory;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.Benchmarks;

/// <summary>
/// The request loop of a thumbnailing service - decode the 3032x2008 photograph, resize it to 800 pixels wide,
/// dispose everything - once per pixel allocator.
/// <para>
/// What this measures is the Allocated column and the Gen2 count, not the mean: with
/// <see cref="GCMemoryAllocator"/> every iteration puts a fresh 24 MB decode buffer and a 2.5 MB resize buffer on
/// the large-object heap, while <see cref="PooledMemoryAllocator"/> hands the same two arrays back on every
/// iteration after the first, so only the codec's own scratch memory is allocated. The native allocator is there
/// for comparison: its buffers never touch the managed heap at all.
/// </para>
/// <para>
/// Each case gets its own allocator instance, created in setup and passed through a request-local
/// <see cref="ImageExecutionOptions.Configuration"/>, so the process-wide pool is never involved and the cases
/// cannot warm each other up.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class MemoryAllocatorBenchmarks
{
    private byte[] jpeg = [];
    private byte[] png = [];
    private MemoryAllocator? allocator;
    private ImageExecutionOptions? execution;
    private DecoderOptions? decoderOptions;

    /// <summary>The allocators compared.</summary>
    public enum PixelAllocator
    {
        /// <summary>A new managed array per frame: the behaviour before pooling.</summary>
        GC,

        /// <summary>The default pool.</summary>
        Pooled,

        /// <summary>Native memory, freed when the image is disposed.</summary>
        Unmanaged,
    }

    [Params(PixelAllocator.GC, PixelAllocator.Pooled, PixelAllocator.Unmanaged)]
    public PixelAllocator Allocator { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        this.jpeg = Corpus.Bytes($"{Corpus.Photo}.jpeg");
        this.png = Corpus.Bytes($"{Corpus.Photo}.png");
        this.allocator = this.Allocator switch
        {
            PixelAllocator.GC => new GCMemoryAllocator(),
            PixelAllocator.Pooled => new PooledMemoryAllocator(),
            _ => new UnmanagedMemoryAllocator(),
        };
        this.execution = new ImageExecutionOptions { Configuration = new Configuration { MemoryAllocator = this.allocator } };
        this.decoderOptions = new DecoderOptions { ExecutionOptions = this.execution };
    }

    [GlobalCleanup]
    public void Cleanup() => this.allocator?.ReleaseRetainedResources();

    [Benchmark]
    public int JpegDecodeResizeDispose()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(this.jpeg, this.decoderOptions!);
        image.Mutate(this.execution!, c => c.Resize(800, 0));
        return image.Width;
    }

    [Benchmark]
    public int PngDecodeResizeDispose()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(this.png, this.decoderOptions!);
        image.Mutate(this.execution!, c => c.Resize(800, 0));
        return image.Width;
    }

    /// <summary>Decode, then a copy-on-write clone per output size, as a service producing two renditions does.</summary>
    [Benchmark]
    public int JpegDecodeTwoRenditionsDispose()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(this.jpeg, this.decoderOptions!);
        using Image<Rgba32> large = image.Clone(this.execution!, c => c.Resize(1600, 0));
        using Image<Rgba32> small = image.Clone(this.execution!, c => c.Resize(new ResizeOptions { Size = new Size(320, 320), Mode = ResizeMode.Crop }));
        return large.Width + small.Width;
    }
}
