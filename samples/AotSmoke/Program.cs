using EasyImageSharp;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Ico;
using EasyImageSharp.Formats.Jpeg;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.Metadata;
using EasyImageSharp.Metadata.Exif;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Xmp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace AotSmoke;

/// <summary>
/// Collects the outcome of every check. A failure never stops the run, so one invocation reports every
/// broken format rather than only the first.
/// </summary>
internal sealed class Report
{
    private int checks;
    private int failures;

    /// <summary>How many checks have run.</summary>
    internal int Checks => this.checks;

    /// <summary>How many of them failed.</summary>
    internal int Failures => this.failures;

    /// <summary>Records one check whose outcome is already known; a null problem means it passed.</summary>
    internal void Record(string name, string? problem)
    {
        this.checks++;
        if (problem is null)
        {
            Console.Out.WriteLine($"PASS {name}");
        }
        else
        {
            this.failures++;
            Console.Error.WriteLine($"FAIL {name}: {problem}");
        }
    }

    /// <summary>Runs one check, turning an escaping exception into a failure rather than into a crash.</summary>
    internal void Record(string name, Func<string?> check)
    {
        string? problem;
        try
        {
            problem = check();
        }
        catch (Exception ex)
        {
            problem = $"threw {ex.GetType().Name}: {ex.Message}";
        }

        this.Record(name, problem);
    }
}

/// <summary>
/// Proves the library still works once it has been statically compiled. Publishing under PublishAot shows
/// there is no reflection the trimmer cannot follow; running the published binary shows the codecs, the
/// SIMD kernels, the metadata writers and the resource limits all still behave. Every input is synthesised
/// in process, so the sample takes no arguments and needs no fixture files.
/// </summary>
internal static class Program
{
    internal static async Task<int> Main()
    {
        var report = new Report();

        RoundTrip.RunAll(report);
        DetectionAndIdentify(report);
        ProcessingPipeline(report);
        MetadataSurvival(report);
        await StreamAndAsyncSurface(report).ConfigureAwait(false);
        ResourceLimits(report);

        Console.Out.WriteLine($"AotSmoke: {report.Checks} checks, {report.Failures} failures");
        return report.Failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Every registered format encodes through <see cref="ImageFormat"/> itself, and the bytes it produces
    /// are detected as that same format instance and identified with the source geometry.
    /// </summary>
    private static void DetectionAndIdentify(Report report)
    {
        foreach (ImageFormat format in ImageFormat.All)
        {
            ImageFormat current = format;
            report.Record("detect " + current.Name.ToLowerInvariant(), () => DetectOne(current));
        }
    }

    private static string? DetectOne(ImageFormat format)
    {
        if (!format.CanEncode || !format.CanDecode)
        {
            return $"{format.Name} reports CanEncode={format.CanEncode}, CanDecode={format.CanDecode}; this sample covers the round-trippable formats";
        }

        // 64x64 keeps every format happy: ICO entries may be at most 256x256, and the size is a multiple
        // of the JPEG MCU so no format has to pad.
        using Image<Rgba32> source = Synth.Opaque(64, 64);
        byte[] encoded = Convert.FromBase64String(source.ToBase64String(format));
        if (encoded.Length == 0)
        {
            return "the encoder wrote no bytes";
        }

        if (!format.Matches(encoded))
        {
            return $"{format.Name}.Matches rejected the bytes its own encoder wrote";
        }

        ImageFormat detected = Image.DetectFormat(encoded);
        if (!ReferenceEquals(detected, format))
        {
            return $"DetectFormat reported {detected.Name}";
        }

        ImageInfo info = Image.Identify(encoded);
        if (info.Width != source.Width || info.Height != source.Height)
        {
            return $"Identify reported {info.Width}x{info.Height}, expected {source.Width}x{source.Height}";
        }

        if (info.FrameCount != 1)
        {
            return $"Identify reported {info.FrameCount} frames, expected 1";
        }

        if (!ReferenceEquals(info.Format, format))
        {
            return $"Identify reported format {info.Format.Name}";
        }

        return null;
    }

    /// <summary>
    /// Resizes and desaturates. This exists to force the vectorised kernels in the pixel and frame
    /// operations to be statically compiled and actually executed: an analyzer-clean build does not prove
    /// that a Vector128/Vector256 path survives ILC, only running one does.
    /// </summary>
    private static void ProcessingPipeline(Report report)
    {
        report.Record("processing resize+grayscale", static () =>
        {
            using Image<Rgba32> source = Synth.Photo(97, 61);
            using Image<Rgba32> result = source.Clone(context => context.Resize(48, 30, KnownResamplers.Bicubic).Grayscale());

            if (result.Width != 48 || result.Height != 30)
            {
                return $"result is {result.Width}x{result.Height}, expected 48x30";
            }

            bool anyNonZero = false;
            for (int y = 0; y < result.Height; y++)
            {
                Span<Rgba32> row = result.Frames.RootFrame.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    Rgba32 pixel = row[x];
                    if (pixel.R != pixel.G || pixel.G != pixel.B)
                    {
                        return $"pixel ({x},{y}) is {pixel.R},{pixel.G},{pixel.B} and so is not grayscale";
                    }

                    anyNonZero |= pixel.R != 0;
                }
            }

            return anyNonZero ? null : "every pixel is black, so the resize produced nothing";
        });
    }

    /// <summary>
    /// An EXIF profile using three different field types, an ICC profile and an XMP packet are attached,
    /// written and read back. The ICC and XMP payloads must come back byte-identical.
    /// </summary>
    /// <remarks>
    /// WebP is deliberately absent: its encoder writes ICCP/EXIF/XMP chunks, but its decoder skips them by
    /// design (see the class comment on WebpDecoder), so there is nothing to read back and asserting one
    /// would only encode the gap as a passing test.
    /// </remarks>
    private static void MetadataSurvival(Report report)
    {
        (string Name, IImageEncoder Encoder)[] encoders =
        {
            ("png", new PngEncoder()),
            ("jpeg", new JpegEncoder { Quality = 90 }),
            ("tiff", new TiffEncoder { Compression = TiffCompression.Deflate }),
        };

        foreach ((string name, IImageEncoder encoder) in encoders)
        {
            IImageEncoder current = encoder;
            report.Record("metadata " + name, () => MetadataOne(current));
        }
    }

    private static string? MetadataOne(IImageEncoder encoder)
    {
        byte[] icc = SyntheticIccProfile();
        byte[] xmp = SyntheticXmpPacket();

        using Image<Rgba32> source = Synth.Opaque(24, 16);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "EasyImageSharp");          // ASCII
        exif.SetValue(ExifTag.Orientation, (ushort)1);          // SHORT
        exif.SetValue(ExifTag.ExposureTime, new Rational(1, 125)); // RATIONAL
        source.Metadata.ExifProfile = exif;
        source.Metadata.IccProfile = new IccProfile(icc);
        source.Metadata.XmpProfile = new XmpProfile(xmp);

        using var stream = new MemoryStream();
        source.Save(stream, encoder);
        using Image<Rgba32> decoded = Image.Load<Rgba32>(stream.ToArray());

        ImageMetadata metadata = decoded.Metadata;
        if (metadata.ExifProfile is null)
        {
            return "the EXIF profile did not survive";
        }

        if (metadata.IccProfile is null)
        {
            return "the ICC profile did not survive";
        }

        if (metadata.XmpProfile is null)
        {
            return "the XMP packet did not survive";
        }

        if (!Same(icc, metadata.IccProfile.ToByteArray()))
        {
            return "the ICC payload came back with different bytes";
        }

        if (!Same(xmp, metadata.XmpProfile.ToByteArray()))
        {
            return "the XMP payload came back with different bytes";
        }

        if (!metadata.ExifProfile.TryGetValue(ExifTag.Make, out IExifValue<string>? make) || make.Value != "EasyImageSharp")
        {
            return "the EXIF ASCII tag did not survive";
        }

        if (!metadata.ExifProfile.TryGetValue(ExifTag.Orientation, out IExifValue<ushort>? orientation) || orientation.Value != 1)
        {
            return "the EXIF SHORT tag did not survive";
        }

        if (!metadata.ExifProfile.TryGetValue(ExifTag.ExposureTime, out IExifValue<Rational>? exposure)
            || exposure.Value.Numerator != 1 || exposure.Value.Denominator != 125)
        {
            return "the EXIF RATIONAL tag did not survive";
        }

        return null;
    }

    /// <summary>The stream and async entry points, plus the base64 helper, all exercised on a real image.</summary>
    private static async Task StreamAndAsyncSurface(Report report)
    {
        string? problem;
        try
        {
            using Image<Rgba32> source = Synth.Photo(37, 23);

            using var written = new MemoryStream();
            await source.SaveAsPngAsync(written).ConfigureAwait(false);
            written.Position = 0;

            using Image<Rgba32> fromStream = Image.Load<Rgba32>(written);
            written.Position = 0;
            using Image<Rgba32> fromAsync = await Image.LoadAsync<Rgba32>(written).ConfigureAwait(false);
            written.Position = 0;
            ImageInfo identified = await Image.IdentifyAsync(written).ConfigureAwait(false);

            string base64 = source.ToBase64String(new PngEncoder());
            byte[] decodedBase64 = Convert.FromBase64String(base64);

            if (fromStream.Width != source.Width || fromStream.Height != source.Height)
            {
                problem = $"Load(stream) produced {fromStream.Width}x{fromStream.Height}";
            }
            else if (fromAsync.Width != source.Width || fromAsync.Height != source.Height)
            {
                problem = $"LoadAsync(stream) produced {fromAsync.Width}x{fromAsync.Height}";
            }
            else if (identified.Width != source.Width || identified.Height != source.Height)
            {
                problem = $"IdentifyAsync(stream) reported {identified.Width}x{identified.Height}";
            }
            else if (!Same(written.ToArray(), decodedBase64))
            {
                problem = "ToBase64String produced different bytes from SaveAsPngAsync";
            }
            else
            {
                problem = null;
            }
        }
        catch (Exception ex)
        {
            problem = $"threw {ex.GetType().Name}: {ex.Message}";
        }

        report.Record("stream and async surface", problem);
    }

    /// <summary>
    /// The decoder exception contract still holds once statically compiled: an over-budget image raises
    /// <see cref="ImageSizeLimitExceededException"/>, unknown bytes raise
    /// <see cref="UnknownImageFormatException"/> and a truncated file raises an
    /// <see cref="ImageFormatException"/> rather than an arbitrary framework exception.
    /// </summary>
    private static void ResourceLimits(Report report)
    {
        report.Record("limits max-pixels", static () =>
        {
            using Image<Rgba32> source = Synth.Opaque(100, 100);
            using var stream = new MemoryStream();
            source.Save(stream, new PngEncoder());
            byte[] encoded = stream.ToArray();

            // Identify never applies the budget, so the header still reports the real size.
            ImageInfo info = Image.Identify(encoded, new DecoderOptions { MaxPixels = 1024 });
            if (info.Width != 100 || info.Height != 100)
            {
                return $"Identify reported {info.Width}x{info.Height} under a small budget";
            }

            try
            {
                using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded, new DecoderOptions { MaxPixels = 1024 });
                return "decoding 10,000 pixels under a 1,024 pixel budget was allowed";
            }
            catch (ImageSizeLimitExceededException)
            {
                return null;
            }
        });

        report.Record("limits unknown format", static () =>
        {
            try
            {
                using Image<Rgba32> decoded = Image.Load<Rgba32>(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
                return "ten arbitrary bytes decoded as an image";
            }
            catch (UnknownImageFormatException)
            {
                return null;
            }
        });

        report.Record("limits truncated file", static () =>
        {
            using Image<Rgba32> source = Synth.Opaque(32, 32);
            using var stream = new MemoryStream();
            source.Save(stream, new PngEncoder());
            byte[] truncated = stream.ToArray()[..40];

            try
            {
                using Image<Rgba32> decoded = Image.Load<Rgba32>(truncated);
                return "a truncated PNG decoded without complaint";
            }
            catch (ImageFormatException)
            {
                return null;
            }
        });

        report.Record("limits oversized ico entry", static () =>
        {
            using Image<Rgba32> source = Synth.Opaque(300, 300);
            using var stream = new MemoryStream();

            try
            {
                source.Save(stream, new IcoEncoder());
                return "a 300x300 frame was accepted as an ICO entry";
            }
            catch (NotSupportedException)
            {
                return null;
            }
        });
    }

    /// <summary>A 132-byte ICC profile: enough of a header for the encoders, which copy the bytes through verbatim.</summary>
    private static byte[] SyntheticIccProfile()
    {
        var data = new byte[132];
        data[0] = (byte)(data.Length >> 24);
        data[1] = (byte)(data.Length >> 16);
        data[2] = (byte)(data.Length >> 8);
        data[3] = (byte)data.Length;
        Write(data, 16, "RGB ");
        Write(data, 20, "XYZ ");
        Write(data, 36, "acsp");
        return data;
    }

    private static byte[] SyntheticXmpPacket()
    {
        const string Packet = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"/></x:xmpmeta>";
        var data = new byte[Packet.Length];
        for (int i = 0; i < Packet.Length; i++)
        {
            data[i] = (byte)Packet[i];
        }

        return data;
    }

    private static void Write(byte[] destination, int offset, string ascii)
    {
        for (int i = 0; i < ascii.Length; i++)
        {
            destination[offset + i] = (byte)ascii[i];
        }
    }

    private static bool Same(byte[] expected, byte[] actual)
    {
        if (expected.Length != actual.Length)
        {
            return false;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                return false;
            }
        }

        return true;
    }
}
