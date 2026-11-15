using BenchmarkDotNet.Attributes;
using EasyImageSharp.Formats.Webp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.Benchmarks;

/// <summary>
/// The WebP code paths that the VP8 / VP8L vector kernels sit under: lossy decode (inverse DCT, intra
/// prediction, loop filter, fancy upsampling), lossy encode (forward DCT, error metric, prediction) and lossless
/// decode (inverse transforms), all on the canonical 3032x2008 photograph.
/// <para>
/// The corpus has no lossy WebP, so <see cref="Setup"/> encodes one from <c>photo.png</c> with this library's own
/// encoder at quality 75. That departs from the corpus rule that inputs come from a foreign encoder, which is
/// acceptable here because these rows measure the kernels against their own scalar baseline (the same benchmark
/// run on a build without them), not this library against another one. The lossless decode reads the
/// Pillow-written <c>photo.webp</c>.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class WebpKernelBenchmarks
{
    private Image<Rgba32>? photo;
    private byte[] lossy = [];
    private byte[] lossless = [];

    [GlobalSetup]
    public void Setup()
    {
        this.photo = Image.Load<Rgba32>(Corpus.Bytes($"{Corpus.Photo}.png"));
        using var stream = new MemoryStream();
        this.photo.Save(stream, new WebpEncoder { FileFormat = WebpFileFormat.Lossy, Quality = 75 });
        this.lossy = stream.ToArray();
        this.lossless = Corpus.Bytes($"{Corpus.Photo}.webp");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        this.photo?.Dispose();
        this.photo = null;
    }

    [Benchmark]
    public int DecodeLossy()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(this.lossy);
        return image.Width;
    }

    [Benchmark]
    public long EncodeLossy()
    {
        using var stream = new MemoryStream();
        this.photo!.Save(stream, new WebpEncoder { FileFormat = WebpFileFormat.Lossy, Quality = 75 });
        return stream.Length;
    }

    [Benchmark]
    public int DecodeLossless()
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(this.lossless);
        return image.Width;
    }
}

/// <summary>
/// The local thresholds and grey-level morphology that the document vector kernels sit under, on the 2480x3508
/// 8-bit grayscale page (A4 at 300 DPI). Like <see cref="ProcessingBenchmarks"/>, each iteration gets a fresh clone
/// of the pristine page because every operation mutates in place.
/// </summary>
[MemoryDiagnoser]
public class DocumentKernelBenchmarks
{
    private Image<L8>? pristine;
    private Image<L8>? working;

    [GlobalSetup]
    public void Setup() => this.pristine = Corpus.LoadL8(Corpus.Scan);

    [IterationSetup]
    public void IterationSetup()
    {
        this.working?.Dispose();
        this.working = this.pristine!.Clone();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        this.working?.Dispose();
        this.working = null;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        this.pristine?.Dispose();
        this.pristine = null;
    }

    [Benchmark]
    public void NiblackThreshold() => this.working!.Mutate(c => c.NiblackThreshold(25, -0.2f));

    [Benchmark]
    public void WolfJolionThreshold() => this.working!.Mutate(c => c.WolfJolionThreshold(25, 0.5f));

    [Benchmark]
    public void PhansalkarThreshold() => this.working!.Mutate(c => c.PhansalkarThreshold(25, 0.25f, 2f, 10f));

    [Benchmark]
    public void NickThreshold() => this.working!.Mutate(c => c.NickThreshold(25, -0.1f));

    [Benchmark]
    public void Erode3x3() => this.working!.Mutate(c => c.Erode(StructuringElement.Square(1)));

    [Benchmark]
    public void Dilate15x15() => this.working!.Mutate(c => c.Dilate(StructuringElement.Square(7)));

    [Benchmark]
    public void Dilate51x51() => this.working!.Mutate(c => c.Dilate(StructuringElement.Rectangle(51, 51)));

    [Benchmark]
    public void CloseDisk5() => this.working!.Mutate(c => c.Close(StructuringElement.Disk(5)));
}
