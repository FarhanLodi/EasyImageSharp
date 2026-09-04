using BenchmarkDotNet.Attributes;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Benchmarks;

/// <summary>
/// Decoding one 3032x2008 photograph out of each of the nine containers the library reads.
/// <para>
/// Two rows of the README's Performance table come from here: "JPEG decode, 3032x2008 -> Rgba32" is
/// <see cref="Decode"/> at <c>Format=jpeg</c> and "PNG decode, 3032x2008 -> Rgba32" is <see cref="Decode"/>
/// at <c>Format=png</c>. ReadmeTable.cs maps them by that exact (class, method, parameter) triple, so
/// renaming either one breaks the table generator rather than silently dropping a README row.
/// </para>
/// <para>
/// Every file is read into memory once in <see cref="Setup"/>: the measurement is the codec, not the disk.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class DecodeBenchmarks
{
    public static IEnumerable<string> Formats => Corpus.Formats;

    [ParamsSource(nameof(Formats))]
    public string Format { get; set; } = "png";

    private byte[] bytes = [];

    [GlobalSetup]
    public void Setup() => this.bytes = Corpus.Bytes($"{Corpus.Photo}.{this.Format}");

    /// <summary>Full decode to 32-bit RGBA, the operation almost every caller actually performs.</summary>
    [Benchmark]
    public int Decode()
    {
        // The width is returned so the JIT cannot decide the decode is dead, and the image is disposed
        // inside the method so its frame buffer is not still live when MemoryDiagnoser takes its reading.
        using Image<Rgba32> image = Image.Load<Rgba32>(this.bytes);
        return image.Width;
    }

    /// <summary>Full decode to 8-bit grayscale, the path a document or OCR pipeline takes.</summary>
    [Benchmark]
    public int DecodeL8()
    {
        using Image<L8> image = Image.Load<L8>(this.bytes);
        return image.Width;
    }

    /// <summary>
    /// Header-only identification. It is measured separately because it is exempt from the DecoderOptions
    /// pixel budget and is the only work an upload validator has to pay for.
    /// </summary>
    [Benchmark]
    public int Identify() => Image.Identify(this.bytes).Width;
}
