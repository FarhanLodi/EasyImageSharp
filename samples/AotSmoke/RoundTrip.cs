using EasyImageSharp;
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
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace AotSmoke;

/// <summary>
/// The encode/decode matrix. Every format the library can both encode and decode gets at least one row,
/// and the animated containers get a second, multi-frame row. Each row encodes a synthesised source into
/// memory, checks the bytes are detected and identified as that format, decodes them back and compares
/// every frame and every pixel against what went in.
/// </summary>
internal static class RoundTrip
{
    /// <summary>Passed as <c>MaxChannelError</c> by the two lossy rows, whose contract is the mean error alone.</summary>
    private const int AnyChannelError = 255;

    /// <summary>One row of the matrix.</summary>
    /// <remarks>
    /// <c>MaxChannelError</c> bounds the worst single channel of any pixel and <c>MaxMeanError</c> the mean
    /// absolute error over every channel of every frame; a lossless row sets both to zero, so a one-bit
    /// change anywhere fails the run.
    /// </remarks>
    internal readonly record struct Case(
        string Name,
        Func<Image<Rgba32>> Source,
        Action<Image<Rgba32>, Stream> Save,
        ImageFormat Format,
        int MaxChannelError,
        double MaxMeanError);

    /// <summary>
    /// The ten formats the library can both encode and decode, plus the option combinations whose code
    /// paths differ enough to be worth compiling separately.
    /// </summary>
    internal static Case[] Cases() => new Case[]
    {
        new("png", static () => Synth.Photo(97, 61), static (image, stream) => image.Save(stream, new PngEncoder()), ImageFormat.Png, 0, 0.0),
        new(
            "png-interlaced",
            static () => Synth.Photo(53, 37),
            static (image, stream) => image.Save(stream, new PngEncoder { InterlaceMethod = PngInterlaceMethod.Adam7 }),
            ImageFormat.Png,
            0,
            0.0),
        new(
            "apng",
            static () => Synth.Animation(64, 41, 4),
            static (image, stream) => image.Save(stream, new PngEncoder { RepeatCount = 3 }),
            ImageFormat.Png,
            0,
            0.0),
        new(
            "bmp-32",
            static () => Synth.Photo(31, 17),
            static (image, stream) => image.Save(stream, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel32 }),
            ImageFormat.Bmp,
            0,
            0.0),
        new("bmp-24", static () => Synth.Opaque(31, 17), static (image, stream) => image.Save(stream, new BmpEncoder()), ImageFormat.Bmp, 0, 0.0),
        new(
            "tiff-none",
            static () => Synth.Photo(59, 43),
            static (image, stream) => image.Save(stream, new TiffEncoder { Compression = TiffCompression.None }),
            ImageFormat.Tiff,
            0,
            0.0),
        new(
            "tiff-deflate",
            static () => Synth.Photo(59, 43),
            static (image, stream) => image.Save(stream, new TiffEncoder { Compression = TiffCompression.Deflate }),
            ImageFormat.Tiff,
            0,
            0.0),
        new(
            "tiff-lzw",
            static () => Synth.Photo(59, 43),
            static (image, stream) => image.Save(stream, new TiffEncoder { Compression = TiffCompression.Lzw }),
            ImageFormat.Tiff,
            0,
            0.0),
        new(
            "tiff-multipage",
            static () => Synth.TwoFrames(48, 33),
            static (image, stream) => image.Save(stream, new TiffEncoder { Compression = TiffCompression.Deflate }),
            ImageFormat.Tiff,
            0,
            0.0),
        new("qoi", static () => Synth.Photo(97, 61), static (image, stream) => image.Save(stream, new QoiEncoder()), ImageFormat.Qoi, 0, 0.0),
        new(
            "tga-rle",
            static () => Synth.Photo(63, 29),
            static (image, stream) => image.Save(stream, new TgaEncoder { BitsPerPixel = TgaBitsPerPixel.Pixel32, Compression = TgaCompression.RunLength }),
            ImageFormat.Tga,
            0,
            0.0),
        new(
            "tga-raw",
            static () => Synth.Photo(63, 29),
            static (image, stream) => image.Save(stream, new TgaEncoder { BitsPerPixel = TgaBitsPerPixel.Pixel32, Compression = TgaCompression.None }),
            ImageFormat.Tga,
            0,
            0.0),
        new(
            "pbm-binary",
            static () => Synth.Opaque(45, 23),
            static (image, stream) => image.Save(stream, new PbmEncoder { ColorType = PbmColorType.Rgb, Encoding = PbmEncoding.Binary }),
            ImageFormat.Pbm,
            0,
            0.0),
        new(
            "pbm-plain",
            static () => Synth.Opaque(45, 23),
            static (image, stream) => image.Save(stream, new PbmEncoder { ColorType = PbmColorType.Rgb, Encoding = PbmEncoding.Plain }),
            ImageFormat.Pbm,
            0,
            0.0),
        new(
            "webp-lossless",
            static () => Synth.Photo(71, 47),
            static (image, stream) => image.Save(stream, new WebpEncoder { FileFormat = WebpFileFormat.Lossless }),
            ImageFormat.Webp,
            0,
            0.0),
        new(
            "webp-animation",
            static () => Synth.Animation(48, 33, 3),
            static (image, stream) => image.Save(stream, new WebpEncoder { FileFormat = WebpFileFormat.Lossless }),
            ImageFormat.Webp,
            0,
            0.0),
        new("ico", static () => Synth.Square(64), static (image, stream) => image.Save(stream, new IcoEncoder()), ImageFormat.Ico, 0, 0.0),
        new(
            "ico-multi",
            static () => Synth.Animation(64, 64, 2),
            static (image, stream) => image.Save(stream, new IcoEncoder { EntryFormat = IcoEntryFormat.Png }),
            ImageFormat.Ico,
            0,
            0.0),
        new(
            "gif",
            static () => Synth.Flat(64, 48, 200),
            static (image, stream) => image.Save(stream, new GifEncoder()),
            ImageFormat.Gif,
            AnyChannelError,
            12.0),
        new(
            "gif-animation",
            static () => Synth.FlatFrames(64, 48, 200, 3),
            static (image, stream) => image.Save(stream, new GifEncoder()),
            ImageFormat.Gif,
            AnyChannelError,
            12.0),
        new(
            "jpeg",
            static () => Synth.Gradient(160, 120),
            static (image, stream) => image.Save(stream, new JpegEncoder { Quality = 90 }),
            ImageFormat.Jpeg,
            AnyChannelError,
            6.0),
        new(
            "jpeg-progressive",
            static () => Synth.Gradient(65, 49),
            static (image, stream) => image.Save(stream, new JpegEncoder { Quality = 90, Progressive = true }),
            ImageFormat.Jpeg,
            AnyChannelError,
            6.0),
    };

    /// <summary>Runs every row and every generic pixel-format round trip, recording one check each.</summary>
    internal static void RunAll(Report report)
    {
        foreach (Case testCase in Cases())
        {
            Case current = testCase;
            report.Record("roundtrip " + current.Name, () => Run(current));
        }

        report.Record("roundtrip pixel-formats", PixelFormats);
    }

    private static string? Run(Case testCase)
    {
        using Image<Rgba32> source = testCase.Source();
        using var stream = new MemoryStream();
        testCase.Save(source, stream);
        byte[] encoded = stream.ToArray();
        if (encoded.Length == 0)
        {
            return "the encoder wrote no bytes";
        }

        ImageFormat detected = Image.DetectFormat(encoded);
        if (!ReferenceEquals(detected, testCase.Format))
        {
            return $"DetectFormat reported {detected.Name}, expected {testCase.Format.Name}";
        }

        ImageInfo info = Image.Identify(encoded);
        if (info.Width != source.Width || info.Height != source.Height)
        {
            return $"Identify reported {info.Width}x{info.Height}, expected {source.Width}x{source.Height}";
        }

        if (info.FrameCount != source.Frames.Count)
        {
            return $"Identify reported {info.FrameCount} frame(s), expected {source.Frames.Count}";
        }

        using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);
        return Compare(source, decoded, testCase.MaxChannelError, testCase.MaxMeanError);
    }

    /// <summary>
    /// Round-trips the same bytes through pixel formats other than <see cref="Rgba32"/>, so the generic
    /// codec instantiations for those formats are rooted and executed rather than merely compiled.
    /// </summary>
    private static string? PixelFormats()
    {
        (string Name, IImageEncoder Encoder)[] opaqueEncoders =
        {
            ("png", new PngEncoder()),
            ("bmp", new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel32 }),
            ("tiff", new TiffEncoder { Compression = TiffCompression.Deflate }),
            ("pbm", new PbmEncoder { Encoding = PbmEncoding.Binary }),
        };

        (string Name, IImageEncoder Encoder)[] alphaEncoders =
        {
            ("png", new PngEncoder()),
            ("bmp", new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel32 }),
            ("tiff", new TiffEncoder { Compression = TiffCompression.Deflate }),
        };

        using (Image<L8> gray = Synth.Gray(53, 29))
        {
            string? problem = Generic(gray, "L8", opaqueEncoders);
            if (problem is not null)
            {
                return problem;
            }
        }

        using (Image<Rgba32> opaque = Synth.Opaque(53, 29))
        using (Image<Rgb24> rgb = opaque.CloneAs<Rgb24>())
        {
            string? problem = Generic(rgb, "Rgb24", opaqueEncoders);
            if (problem is not null)
            {
                return problem;
            }
        }

        using (Image<Rgba32> photo = Synth.Photo(53, 29))
        using (Image<Bgra32> bgra = photo.CloneAs<Bgra32>())
        {
            string? problem = Generic(bgra, "Bgra32", alphaEncoders);
            if (problem is not null)
            {
                return problem;
            }
        }

        return null;
    }

    private static string? Generic<TPixel>(Image<TPixel> source, string label, (string Name, IImageEncoder Encoder)[] encoders)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        foreach ((string name, IImageEncoder encoder) in encoders)
        {
            using var stream = new MemoryStream();
            source.Save(stream, encoder);
            using Image<TPixel> decoded = Image.Load<TPixel>(stream.ToArray());
            string? problem = CompareGeneric(source, decoded);
            if (problem is not null)
            {
                return $"{label} through {name}: {problem}";
            }
        }

        return null;
    }

    private static string? CompareGeneric<TPixel>(Image<TPixel> expected, Image<TPixel> actual)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            return $"decoded {actual.Width}x{actual.Height}, expected {expected.Width}x{expected.Height}";
        }

        for (int y = 0; y < expected.Height; y++)
        {
            Span<TPixel> expectedRow = expected.Frames.RootFrame.GetRowSpan(y);
            Span<TPixel> actualRow = actual.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < expectedRow.Length; x++)
            {
                Rgba32 a = expectedRow[x].ToRgba32();
                Rgba32 b = actualRow[x].ToRgba32();
                if (a.R != b.R || a.G != b.G || a.B != b.B || a.A != b.A)
                {
                    return $"pixel ({x},{y}): expected {Describe(a)} got {Describe(b)}";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Walks every frame and every pixel. Returns a description of the first pixel outside
    /// <paramref name="maxChannelError"/>, or of a mean error above <paramref name="maxMeanError"/>, or
    /// <see langword="null"/> when the decoded image is within both bounds.
    /// </summary>
    private static string? Compare(Image<Rgba32> expected, Image<Rgba32> actual, int maxChannelError, double maxMeanError)
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            return $"decoded {actual.Width}x{actual.Height}, expected {expected.Width}x{expected.Height}";
        }

        if (expected.Frames.Count != actual.Frames.Count)
        {
            return $"decoded {actual.Frames.Count} frame(s), expected {expected.Frames.Count}";
        }

        double total = 0;
        long samples = 0;
        string? worst = null;
        for (int f = 0; f < expected.Frames.Count; f++)
        {
            ImageFrame<Rgba32> expectedFrame = expected.Frames[f];
            ImageFrame<Rgba32> actualFrame = actual.Frames[f];
            if (expectedFrame.Width != actualFrame.Width || expectedFrame.Height != actualFrame.Height)
            {
                return $"frame {f} decoded {actualFrame.Width}x{actualFrame.Height}, expected {expectedFrame.Width}x{expectedFrame.Height}";
            }

            for (int y = 0; y < expectedFrame.Height; y++)
            {
                Span<Rgba32> expectedRow = expectedFrame.GetRowSpan(y);
                Span<Rgba32> actualRow = actualFrame.GetRowSpan(y);
                for (int x = 0; x < expectedRow.Length; x++)
                {
                    Rgba32 a = expectedRow[x];
                    Rgba32 b = actualRow[x];
                    int dr = Math.Abs(a.R - b.R);
                    int dg = Math.Abs(a.G - b.G);
                    int db = Math.Abs(a.B - b.B);
                    int da = Math.Abs(a.A - b.A);
                    total += dr + dg + db + da;
                    samples += 4;
                    if (worst is null && Math.Max(Math.Max(dr, dg), Math.Max(db, da)) > maxChannelError)
                    {
                        worst = $"frame {f} pixel ({x},{y}): expected {Describe(a)} got {Describe(b)}, over the {maxChannelError}-per-channel bound";
                    }
                }
            }
        }

        if (worst is not null)
        {
            return worst;
        }

        double mean = samples == 0 ? 0.0 : total / samples;
        return mean > maxMeanError ? $"mean channel error {mean:F3} exceeds {maxMeanError:F3}" : null;
    }

    private static string Describe(Rgba32 pixel) => $"{pixel.R},{pixel.G},{pixel.B},{pixel.A}";
}
