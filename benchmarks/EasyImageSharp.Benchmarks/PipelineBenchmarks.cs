using BenchmarkDotNet.Attributes;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.Benchmarks;

/// <summary>
/// The thumbnailer: load a JPEG, resize it to fit 320x240, re-encode it. Twenty distinct 1920x1280
/// photographs at quality 88, written by libjpeg through Pillow.
/// <para>
/// This is the README's last Performance row, "Load -> resize -> save, 20 JPEGs".
/// <c>OperationsPerInvoke = 20</c> makes BenchmarkDotNet report the per-image time directly, which is the
/// "19.6 ms each" half of that row; the "51 img/s" half is 1000 divided by the mean and is computed by
/// ReadmeTable.cs, not by BenchmarkDotNet.
/// </para>
/// <para>
/// The published figure was measured against a corpus that no longer exists - that is the whole reason
/// benchmarks/ was restored - so this row must be re-measured before the README is edited, not copied
/// forward.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class PipelineBenchmarks
{
    /// <summary>
    /// Must equal the number of files generate.py writes into corpus/batch. It is a compile-time constant
    /// because <c>OperationsPerInvoke</c> is an attribute argument; <see cref="Setup"/> checks the corpus
    /// agrees rather than letting a mismatch silently scale every number in this class.
    /// </summary>
    private const int BatchCount = 20;

    private static readonly JpegEncoder Encoder = new() { Quality = 82 };

    private static readonly ResizeOptions Thumbnail = new()
    {
        Size = new Size(320, 240),
        Mode = ResizeMode.Max,
    };

    private byte[][] batch = [];

    [GlobalSetup]
    public void Setup()
    {
        string[] paths = Corpus.BatchJpegs();
        if (paths.Length != BatchCount)
        {
            throw new InvalidOperationException(
                $"corpus/batch holds {paths.Length} JPEGs but this benchmark divides by {BatchCount}. " +
                $"Run: {Corpus.GenerateCommand} --force");
        }

        this.batch = new byte[paths.Length][];
        for (int i = 0; i < paths.Length; i++)
        {
            this.batch[i] = File.ReadAllBytes(paths[i]);
        }
    }

    [Benchmark(OperationsPerInvoke = BatchCount)]
    public void LoadResizeSave()
    {
        foreach (byte[] bytes in this.batch)
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(bytes);
            using Image<Rgba32> thumb = image.Clone(c => c.Resize(Thumbnail));
            thumb.Save(Stream.Null, Encoder);
        }
    }

    /// <summary>
    /// The same pipeline through the async surface. It is worth publishing because the async save is a
    /// wrapper over the synchronous encoder rather than an independently asynchronous implementation, and
    /// the gap between these two rows is the honest measure of what that wrapper costs.
    /// </summary>
    [Benchmark(OperationsPerInvoke = BatchCount)]
    public async Task LoadResizeSaveAsync()
    {
        foreach (byte[] bytes in this.batch)
        {
            using var input = new MemoryStream(bytes, writable: false);
            using Image<Rgba32> image = await Image.LoadAsync<Rgba32>(input).ConfigureAwait(false);
            using Image<Rgba32> thumb = image.Clone(c => c.Resize(Thumbnail));
            await thumb.SaveAsync(Stream.Null, Encoder).ConfigureAwait(false);
        }
    }
}
