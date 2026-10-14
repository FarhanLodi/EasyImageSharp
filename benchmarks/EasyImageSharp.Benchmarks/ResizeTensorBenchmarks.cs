using BenchmarkDotNet.Attributes;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Tensors;

namespace EasyImageSharp.Benchmarks;

[MemoryDiagnoser]
public class ResizeTensorBenchmarks
{
    private Image<Rgb24> image = null!;
    private float[] output = null!;
    private ResizeOptions resize = null!;

    [Params(320, 960)]
    public int Width { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        this.image = new Image<Rgb24>(1200, 800);
        this.resize = new ResizeOptions { Size = new Size(this.Width, this.Width == 320 ? 48 : 960), Mode = ResizeMode.Pad };
        this.output = new float[this.resize.Size.Width * this.resize.Size.Height * 3];
    }

    [Benchmark(Baseline = true)]
    public void Materialized()
    {
        using Image<Rgb24> resized = this.image.Clone(context => context.Resize(this.resize));
        resized.WriteTensor(this.output);
    }

    [Benchmark]
    public TensorResizeMapping Fused() => this.image.ResizeToTensor(this.output, this.resize);

    [GlobalCleanup]
    public void Cleanup() => this.image.Dispose();
}
