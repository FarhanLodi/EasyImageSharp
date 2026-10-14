using BenchmarkDotNet.Attributes;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Tensors;

namespace EasyImageSharp.Benchmarks;

/// <summary>Isolates tensor conversion from resize, inference, and model loading.</summary>
[MemoryDiagnoser]
public class TensorBenchmarks
{
    private Image<Rgb24> image = null!;
    private float[] destination = null!;
    private readonly float[] mean = [0.5f, 0.5f, 0.5f];
    private readonly float[] std = [0.5f, 0.5f, 0.5f];
    private readonly TensorWriteOptions bgr = new() { Channels = TensorChannels.Bgr };

    [Params("recognizer", "detector")]
    public string Workload { get; set; } = "recognizer";

    [GlobalSetup]
    public void Setup()
    {
        (int width, int height) = this.Workload == "recognizer" ? (320, 48) : (960, 960);
        this.image = new Image<Rgb24>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgb24> row = this.image.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < width; x++)
                row[x] = new Rgb24((byte)(x * 17 + y), (byte)(x + y * 31), (byte)(x * 7 + y * 3));
        }

        this.destination = new float[width * height * 3];
        float[] reference = this.image.ToChwTensor(this.mean, this.std);
        this.image.WriteTensor(this.destination, channelMean: this.mean, channelStd: this.std);
        for (int i = 0; i < reference.Length; i++)
            if (Math.Abs(reference[i] - this.destination[i]) > 1e-6f)
                throw new InvalidOperationException("RGB tensor paths differ beyond the normalization tolerance.");
    }

    [Benchmark(Baseline = true)]
    public float[] AllocatingRgb() => this.image.ToChwTensor(this.mean, this.std);

    [Benchmark]
    public void ReusableRgb() => this.image.WriteTensor(this.destination, channelMean: this.mean, channelStd: this.std);

    [Benchmark]
    public void ReusableBgr() => this.image.WriteTensor(this.destination, this.bgr, this.mean, this.std);

    [GlobalCleanup]
    public void Cleanup() => this.image.Dispose();
}
