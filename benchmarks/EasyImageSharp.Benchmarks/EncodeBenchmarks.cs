using BenchmarkDotNet.Attributes;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Bmp;
using EasyImageSharp.Formats.Gif;
using EasyImageSharp.Formats.Ico;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Formats.Pbm;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Formats.Qoi;
using EasyImageSharp.Formats.Tga;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.Formats.Webp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyImageSharp.Benchmarks;

/// <summary>
/// Encoding the same 3032x2008 photograph with every encoder the library ships, at the settings a caller
/// would plausibly choose rather than at each encoder's defaults.
/// <para>
/// <see cref="Encode"/> writes to <see cref="Stream.Null"/>, so the Allocated column is the encoder's own
/// allocation and not the doubling of a <see cref="MemoryStream"/> buffer. <see cref="EncodeToMemory"/> is
/// the same work into a pre-sized <see cref="MemoryStream"/>, which is the figure a caller who keeps the
/// bytes actually pays. No row here appears in the README's Performance table.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class EncodeBenchmarks
{
    /// <summary>
    /// WebP appears twice because its two bitstreams are different encoders in every respect that matters;
    /// ICO appears because it is the one encoder with a hard size limit, and it gets its own 256x256 subject.
    /// </summary>
    public static IEnumerable<string> Formats =>
    [
        "png", "jpeg", "bmp", "tiff", "gif", "webp-lossless", "webp-lossy", "tga", "qoi", "pbm", "ico",
    ];

    [ParamsSource(nameof(Formats))]
    public string Format { get; set; } = "png";

    private Image<Rgba32>? source;
    private Image<Rgba32>? icon;
    private IImageEncoder encoder = new PngEncoder();
    private int expectedBytes;

    [GlobalSetup]
    public void Setup()
    {
        this.source = Corpus.LoadRgba32($"{Corpus.Photo}.png");
        this.encoder = EncoderFor(this.Format);

        if (this.Format == "ico")
        {
            // IcoEncoder rejects anything larger than 256x256, so the ICO row measures a 256x256 crop of
            // the same pixels. Its numbers are therefore not comparable with the other rows, by design.
            int side = Math.Min(256, Math.Min(this.source.Width, this.source.Height));
            this.icon = this.source.Clone(c => c.Crop(side, side));
        }

        // One encode up front, so EncodeToMemory can pre-size its buffer to the real output length instead
        // of measuring MemoryStream's doubling.
        using var probe = new MemoryStream();
        this.Subject.Save(probe, this.encoder);
        this.expectedBytes = (int)probe.Length;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        this.icon?.Dispose();
        this.source?.Dispose();
        this.icon = null;
        this.source = null;
    }

    /// <summary>Encode into a sink that keeps nothing: the library's own allocation, isolated.</summary>
    [Benchmark]
    public void Encode() => this.Subject.Save(Stream.Null, this.encoder);

    /// <summary>Encode into a pre-sized buffer the caller keeps: what a real caller pays.</summary>
    [Benchmark]
    public long EncodeToMemory()
    {
        using var stream = new MemoryStream(this.expectedBytes);
        this.Subject.Save(stream, this.encoder);
        return stream.Length;
    }

    private Image<Rgba32> Subject => this.icon ?? this.source!;

    private static IImageEncoder EncoderFor(string format) => format switch
    {
        "png" => new PngEncoder(),
        "jpeg" => new JpegEncoder { Quality = 90 },
        "bmp" => new BmpEncoder(),
        "tiff" => new TiffEncoder { Compression = TiffCompression.Deflate },
        "gif" => new GifEncoder(),
        "webp-lossless" => new WebpEncoder { FileFormat = WebpFileFormat.Lossless },
        "webp-lossy" => new WebpEncoder { FileFormat = WebpFileFormat.Lossy, Quality = 80 },
        "tga" => new TgaEncoder(),
        "qoi" => new QoiEncoder(),
        "pbm" => new PbmEncoder(),
        "ico" => new IcoEncoder(),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown encoder."),
    };
}
