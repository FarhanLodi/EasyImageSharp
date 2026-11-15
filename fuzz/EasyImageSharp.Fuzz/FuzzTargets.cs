using System.IO.Compression;
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

namespace EasyImageSharp.Fuzz;

/// <summary>One fuzz target: a name to select it by, and the function libFuzzer calls with each input.</summary>
/// <param name="Name">The command-line name, e.g. <c>png</c>.</param>
/// <param name="Description">One line for <c>list</c>.</param>
/// <param name="Dictionaries">The <c>fuzz/dictionaries/*.dict</c> files that suit this target, merged by the scripts.</param>
/// <param name="Run">Runs the target on one input; throws <see cref="FuzzContractViolationException"/> on a bug.</param>
internal sealed record FuzzTarget(string Name, string Description, string[] Dictionaries, SharpFuzz.ReadOnlySpanAction Run);

/// <summary>
/// The fuzz targets. Each per-format target calls that format's decoder directly, so libFuzzer does not spend
/// its time rediscovering the signature of a format through <c>Image.Load</c>'s detection; the
/// <c>identify</c>, <c>identify-header</c> and <c>roundtrip</c> targets go through the public auto-detecting
/// entry points instead.
/// </summary>
internal static class FuzzTargets
{
    /// <summary>Every per-format dictionary, for the targets that accept any format.</summary>
    public static readonly string[] AllDictionaries =
        { "png", "jpeg", "webp", "gif", "bmp", "tiff", "tga", "pnm", "qoi", "ico" };

    /// <summary>Every target, in the order <c>list</c> prints them and the scripts run them.</summary>
    public static IReadOnlyList<FuzzTarget> All { get; } = new FuzzTarget[]
    {
        Decoder<PngDecoder>("png", "PNG and APNG (Identify, Decode<Rgba32>, and Decode<Rgba64> on success)", wide: true, "png"),
        Decoder<JpegDecoder>("jpeg", "baseline, progressive, restart intervals, CMYK/YCCK", wide: false, "jpeg"),
        Decoder<WebpDecoder>("webp", "VP8, VP8L, ALPH, animation", wide: false, "webp"),
        Decoder<GifDecoder>("gif", "LZW, interlacing, animation and disposal", wide: false, "gif"),
        Decoder<BmpDecoder>("bmp", "every header version, bitfields, RLE4/RLE8", wide: false, "bmp"),
        Decoder<TiffDecoder>("tiff", "strips, tiles, LZW/Deflate/PackBits/CCITT/JPEG, BigTIFF, multi-page", wide: true, "tiff"),
        Decoder<TgaDecoder>("tga", "raw and RLE, palettes, origins (TGA has no signature)", wide: false, "tga"),
        Decoder<PbmDecoder>("pnm", "Netpbm P1-P7 (PBM, PGM, PPM, PAM)", wide: false, "pnm"),
        Decoder<QoiDecoder>("qoi", "QOI", wide: false, "qoi"),
        Decoder<IcoDecoder>("ico", "ICO and CUR with BMP and PNG entries", wide: false, "ico", "png", "bmp"),
        new FuzzTarget("identify", "Image.Identify over every format (auto-detected)", AllDictionaries, Identify),
        new FuzzTarget("identify-header", "Image.IdentifyHeader on a stream (PNG, JPEG, GIF, BMP, QOI, WebP)", AllDictionaries, IdentifyHeader),
        new FuzzTarget("roundtrip", "Image.Load, then PNG and QOI encode -> decode must reproduce the first frame exactly", AllDictionaries, RoundTrip),
    };

    /// <summary>Finds a target by name, case-insensitively.</summary>
    public static FuzzTarget? Find(string name)
        => All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <remarks>
    /// The decoder is created per input rather than once up front. Its constructor is library code, and once the
    /// library is instrumented, library code must not run before <c>Fuzzer.LibFuzzer.Run</c> has mapped the
    /// coverage memory (see <see cref="TraceMap"/>); decoders are stateless, so a fresh one costs nothing.
    /// </remarks>
    private static FuzzTarget Decoder<TDecoder>(string name, string description, bool wide, params string[] dictionaries)
        where TDecoder : IImageDecoder, new()
    {
        string type = typeof(TDecoder).Name;
        string identify = type + ".Identify";
        string decode = type + ".Decode<Rgba32>";
        string decodeWide = type + ".Decode<Rgba64>";
        return new FuzzTarget(name, $"{type}: {description}", dictionaries, data =>
        {
            var decoder = new TDecoder();
            FuzzContract.Guard(identify, data, d => decoder.Identify(d, FuzzContract.Options));
            bool decoded = FuzzContract.Guard(decode, data, d => decoder.Decode<Rgba32>(d, FuzzContract.Options).Dispose());

            // PNG and TIFF keep 16-bit samples on a separate, wider path when the destination can hold them. It
            // is only worth the second decode once the first has shown the input gets that far.
            if (decoded && wide)
            {
                FuzzContract.Guard(decodeWide, data, d => decoder.Decode<Rgba64>(d, FuzzContract.Options).Dispose());
            }
        });
    }

    private static void Identify(ReadOnlySpan<byte> data)
        => FuzzContract.Guard("Image.Identify", data, static d => Image.Identify(d, FuzzContract.Options));

    private static void IdentifyHeader(ReadOnlySpan<byte> data)
    {
        // IdentifyHeader takes a stream, so this target pays one copy per input; it is the only one that does.
        byte[] copy = data.ToArray();
        FuzzContract.Guard("Image.IdentifyHeader", data, _ =>
        {
            using var stream = new MemoryStream(copy, writable: false);
            Image.IdentifyHeader(stream, maxHeaderBytes: 64 * 1024);
        });
    }

    /// <summary>
    /// Decodes with auto-detection, then encodes the first frame with two lossless encoders and decodes the
    /// result again. The library must read back its own output (any exception at all is a violation there,
    /// including the ones the decode contract allows) and must reproduce every pixel exactly.
    /// </summary>
    private static void RoundTrip(ReadOnlySpan<byte> data)
    {
        Image<Rgba32>? decoded = null;
        if (!FuzzContract.Guard("Image.Load<Rgba32>", data, d => decoded = Image.Load<Rgba32>(d, FuzzContract.Options)))
        {
            return;
        }

        using Image<Rgba32> source = decoded!;
        using Image<Rgba32> first = source.Frames.CloneFrame(0);

        // Colour type, depth and transparency handling are pinned: left to the source image's metadata the PNG
        // encoder may legitimately pick a palette or grayscale layout, and a lossy choice there would read as a
        // round-trip failure that is not one.
        var png = new PngEncoder
        {
            ColorType = PngColorType.RgbWithAlpha,
            BitDepth = PngBitDepth.Bit8,
            TransparentColorMode = PngTransparentColorMode.Preserve,
            CompressionLevel = CompressionLevel.Fastest,
        };
        RoundTripThrough("PNG", first, png, new PngDecoder());
        RoundTripThrough("QOI", first, new QoiEncoder { Channels = QoiChannels.Rgba }, new QoiDecoder());
    }

    private static void RoundTripThrough(string format, Image<Rgba32> image, IImageEncoder encoder, IImageDecoder decoder)
    {
        byte[] encoded;
        try
        {
            using var stream = new MemoryStream();
            encoder.Encode(image, stream);
            encoded = stream.ToArray();
        }
        catch (Exception ex)
        {
            throw new FuzzContractViolationException(
                $"{format} encoder threw {ex.GetType().FullName} on a {image.Width}x{image.Height} image the decoder produced: {ex.Message}", ex);
        }

        Image<Rgba32> again;
        try
        {
            again = decoder.Decode<Rgba32>(encoded, FuzzContract.Options);
        }
        catch (Exception ex)
        {
            throw new FuzzContractViolationException(
                $"{format} decoder rejected {encoded.Length} bytes the {format} encoder wrote for a {image.Width}x{image.Height} image: "
                + $"{ex.GetType().FullName}: {ex.Message}", ex);
        }

        using (again)
        {
            FuzzContract.Require(
                again.Width == image.Width && again.Height == image.Height,
                $"{format} round trip changed the size from {image.Width}x{image.Height} to {again.Width}x{again.Height}.");

            int length = image.Width * image.Height * 4;
            byte[] want = new byte[length];
            byte[] got = new byte[length];
            image.Frames.RootFrame.CopyPixelDataTo(want);
            again.Frames.RootFrame.CopyPixelDataTo(got);
            int prefix = want.AsSpan().CommonPrefixLength(got);
            if (prefix != length)
            {
                int pixel = prefix / 4;
                throw new FuzzContractViolationException(
                    $"{format} round trip changed pixel ({pixel % image.Width},{pixel / image.Width}) of a {image.Width}x{image.Height} image: "
                    + $"wrote {Hex(want, pixel)}, read back {Hex(got, pixel)}.");
            }
        }
    }

    private static string Hex(byte[] rgba, int pixel) => Convert.ToHexString(rgba, pixel * 4, 4);
}
