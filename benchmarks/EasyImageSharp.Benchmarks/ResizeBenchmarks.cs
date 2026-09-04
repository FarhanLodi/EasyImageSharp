using BenchmarkDotNet.Attributes;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.Benchmarks;

/// <summary>
/// Geometry on the 3032x2008 photograph.
/// <para>
/// Two rows of the README's Performance table come from here: "Resize, bicubic x0.5, 3032x2008 Rgba32" is
/// <see cref="BicubicHalfRgba32"/> and "Resize, bicubic x0.5, 3032x2008 L8" is <see cref="BicubicHalfL8"/>.
/// The L8 row exists because the single-channel resize path in FrameOps is a separate implementation from
/// the four-channel one, and the ratio between the two rows is the only published evidence it is worth
/// having.
/// </para>
/// <para>
/// Every method returns the result's width so nothing is optimised away, and disposes its intermediate
/// image inside the method so the MemoryDiagnoser reading is per-operation rather than per-run.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class ResizeBenchmarks
{
    private Image<Rgba32>? rgba;
    private Image<L8>? gray;

    [GlobalSetup]
    public void Setup()
    {
        this.rgba = Corpus.LoadRgba32($"{Corpus.Photo}.png");
        this.gray = Corpus.LoadL8($"{Corpus.Photo}.png");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        this.rgba?.Dispose();
        this.gray?.Dispose();
        this.rgba = null;
        this.gray = null;
    }

    [Benchmark(Baseline = true)]
    public int BicubicHalfRgba32()
    {
        Image<Rgba32> source = this.rgba!;
        using Image<Rgba32> result = source.Clone(
            c => c.Resize(source.Width / 2, source.Height / 2, KnownResamplers.Bicubic));
        return result.Width;
    }

    [Benchmark]
    public int BicubicHalfL8()
    {
        Image<L8> source = this.gray!;
        using Image<L8> result = source.Clone(
            c => c.Resize(source.Width / 2, source.Height / 2, KnownResamplers.Bicubic));
        return result.Width;
    }

    /// <summary>The blocked-transpose path: a lossless 90 degree rotation followed by a horizontal flip.</summary>
    [Benchmark]
    public int RotateFlip()
    {
        using Image<Rgba32> result = this.rgba!.Clone(
            c => c.RotateFlip(RotateMode.Rotate90, FlipMode.Horizontal));
        return result.Width;
    }

    /// <summary>The crop path, which under copy-on-write cloning is the cheapest way to make a new buffer.</summary>
    [Benchmark]
    public int Crop()
    {
        Image<Rgba32> source = this.rgba!;
        using Image<Rgba32> result = source.Clone(c => c.Crop(source.Width / 2, source.Height / 2));
        return result.Width;
    }
}

/// <summary>
/// The same half-scale resize through every resampler a caller is likely to pick. No row here appears in
/// the README, but a change to <c>ResizeKernelMap</c> or to a kernel cannot be justified without it.
/// <para>
/// It is a separate class because BenchmarkDotNet parameters are class-scoped: folding the sweep into
/// <see cref="ResizeBenchmarks"/> would multiply that class's four benchmarks by five resamplers and
/// quintuple the cost of the two rows the README quotes.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class ResamplerBenchmarks
{
    public static IEnumerable<string> Resamplers =>
    [
        "NearestNeighbor", "Box", "Bilinear", "Bicubic", "Lanczos3",
    ];

    [ParamsSource(nameof(Resamplers))]
    public string Resampler { get; set; } = "Bicubic";

    private Image<Rgba32>? rgba;
    private IResampler sampler = KnownResamplers.Bicubic;

    [GlobalSetup]
    public void Setup()
    {
        this.rgba = Corpus.LoadRgba32($"{Corpus.Photo}.png");
        this.sampler = this.Resampler switch
        {
            "NearestNeighbor" => KnownResamplers.NearestNeighbor,
            "Box" => KnownResamplers.Box,
            "Bilinear" => KnownResamplers.Bilinear,
            "Bicubic" => KnownResamplers.Bicubic,
            "Lanczos3" => KnownResamplers.Lanczos3,
            _ => throw new ArgumentOutOfRangeException(nameof(this.Resampler), this.Resampler, "Unknown resampler."),
        };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        this.rgba?.Dispose();
        this.rgba = null;
    }

    [Benchmark]
    public int ResizeByResampler()
    {
        Image<Rgba32> source = this.rgba!;
        using Image<Rgba32> result = source.Clone(
            c => c.Resize(source.Width / 2, source.Height / 2, this.sampler));
        return result.Width;
    }
}
