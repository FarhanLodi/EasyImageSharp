using BenchmarkDotNet.Attributes;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.Benchmarks;

/// <summary>
/// The document-imaging operations, on the 2480x3508 8-bit grayscale page - "A4 at 300 DPI, L8" in the
/// README's Performance table.
/// <para>
/// Two rows of that table come from here: "Grayscale, in place" is <see cref="Grayscale"/> and "Otsu
/// threshold, in place" is <see cref="OtsuThreshold"/>. The remaining methods are not quoted anywhere; they
/// are here because a change to the local-threshold, morphology or deskew code has to be defensible.
/// </para>
/// <para>
/// Every operation mutates in place, so <see cref="IterationSetup"/> hands each iteration a fresh clone of
/// the pristine page. BenchmarkDotNet warns that per-iteration setup costs precision on short benchmarks,
/// and that is the right trade here: cloning inside the benchmark method would charge a 8.7 MB copy to an
/// operation the README quotes at a few milliseconds, which is a far larger error than the one it avoids.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class ProcessingBenchmarks
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
    public void Grayscale() => this.working!.Mutate(c => c.Grayscale());

    [Benchmark]
    public void OtsuThreshold() => this.working!.Mutate(c => c.OtsuThreshold());

    [Benchmark]
    public void SauvolaThreshold() => this.working!.Mutate(c => c.SauvolaThreshold());

    [Benchmark]
    public void AdaptiveThreshold() => this.working!.Mutate(c => c.AdaptiveThreshold());

    [Benchmark]
    public void BoxBlur() => this.working!.Mutate(c => c.BoxBlur(3));

    [Benchmark]
    public void GaussianBlur() => this.working!.Mutate(c => c.GaussianBlur(3f));

    [Benchmark]
    public void Deskew() => this.working!.Mutate(c => c.Deskew());

    /// <summary>The whole document pipeline: what a caller feeding an OCR engine actually calls.</summary>
    [Benchmark]
    public void PrepareForOcr() => this.working!.Mutate(c => c.PrepareForOcr());
}
