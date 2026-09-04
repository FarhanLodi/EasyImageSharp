using EasyImageSharp.Formats.Bmp;
using EasyImageSharp.Formats.Gif;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Formats.Tiff;

namespace EasyImageSharp.Formats;

/// <summary>Tests whether encoded bytes start with the signature of a format.</summary>
internal delegate bool MagicMatcher(ReadOnlySpan<byte> data);

/// <summary>Describes an image format known to the library.</summary>
public sealed partial class ImageFormat
{
    private readonly Func<IImageDecoder>? decoderFactory;
    private readonly Func<IImageEncoder>? encoderFactory;
    private readonly MagicMatcher matcher;

    private ImageFormat(
        string name,
        string defaultMimeType,
        string[] fileExtensions,
        MagicMatcher matcher,
        Func<IImageDecoder>? decoderFactory,
        Func<IImageEncoder>? encoderFactory)
    {
        this.Name = name;
        this.DefaultMimeType = defaultMimeType;
        this.FileExtensions = fileExtensions;
        this.matcher = matcher;
        this.decoderFactory = decoderFactory;
        this.encoderFactory = encoderFactory;
    }

    public static ImageFormat Png { get; } = new(
        "PNG", "image/png", new[] { "png" },
        static data => data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        static () => new PngDecoder(), static () => new PngEncoder());

    public static ImageFormat Jpeg { get; } = new(
        "JPEG", "image/jpeg", new[] { "jpg", "jpeg", "jfif" },
        static data => data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF,
        static () => new JpegDecoder(), static () => new JpegEncoder());

    public static ImageFormat Bmp { get; } = new(
        "BMP", "image/bmp", new[] { "bmp", "dib" },
        static data => data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M',
        static () => new BmpDecoder(), static () => new BmpEncoder());

    public static ImageFormat Tiff { get; } = new(
        "TIFF", "image/tiff", new[] { "tif", "tiff" },
        // Classic TIFF is the byte-order mark plus version 42; BigTIFF is version 43 followed by the offset size
        // (always 8) and a zero reserved word, both of which are checked so a stray 0x2B does not claim the file.
        static data => (data.Length >= 4
                && ((data[0] == 0x49 && data[1] == 0x49 && data[2] == 0x2A && data[3] == 0x00)
                    || (data[0] == 0x4D && data[1] == 0x4D && data[2] == 0x00 && data[3] == 0x2A)))
            || (data.Length >= 8
                && ((data[0] == 0x49 && data[1] == 0x49 && data[2] == 0x2B && data[3] == 0x00
                        && data[4] == 0x08 && data[5] == 0x00 && data[6] == 0x00 && data[7] == 0x00)
                    || (data[0] == 0x4D && data[1] == 0x4D && data[2] == 0x00 && data[3] == 0x2B
                        && data[4] == 0x00 && data[5] == 0x08 && data[6] == 0x00 && data[7] == 0x00))),
        static () => new TiffDecoder(), static () => new TiffEncoder());

    public static ImageFormat Gif { get; } = new(
        "GIF", "image/gif", new[] { "gif" },
        static data => data.Length >= 6
            && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F'
            && data[3] == (byte)'8' && (data[4] == (byte)'7' || data[4] == (byte)'9') && data[5] == (byte)'a',
        static () => new GifDecoder(), static () => new GifEncoder());

    /// <summary>
    /// Every format the library knows, in detection order. Formats whose signature is a prefix of another
    /// format's signature must come after it. New codecs register themselves by adding an entry here.
    /// </summary>
    public static IReadOnlyList<ImageFormat> All { get; } = new[]
    {
        Png,
        Jpeg,
        Bmp,
        Tiff,
        Qoi,
        Pbm,
        Ico,
        Gif,
        Webp,
        Tga,
    };

    public string Name { get; }

    public string DefaultMimeType { get; }

    public IReadOnlyList<string> FileExtensions { get; }

    /// <summary>True when the library can decode this format.</summary>
    public bool CanDecode => this.decoderFactory is not null;

    /// <summary>True when the library can encode this format.</summary>
    public bool CanEncode => this.encoderFactory is not null;

    /// <summary>True when <paramref name="data"/> starts with this format's signature.</summary>
    public bool Matches(ReadOnlySpan<byte> data) => this.matcher(data);

    internal IImageDecoder CreateDecoder()
        => this.decoderFactory?.Invoke()
           ?? throw new NotSupportedException($"Decoding {this.Name} images is not supported in this version.");

    internal IImageEncoder CreateEncoder()
        => this.encoderFactory?.Invoke()
           ?? throw new NotSupportedException($"Encoding {this.Name} images is not supported in this version.");

    public override string ToString() => this.Name;
}
