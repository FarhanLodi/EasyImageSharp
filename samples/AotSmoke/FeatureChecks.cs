using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using EasyImageSharp;
using EasyImageSharp.ColorSpaces;
using EasyImageSharp.Diagnostics;
using EasyImageSharp.Formats;
using EasyImageSharp.Formats.Exr;
using EasyImageSharp.Formats.Png;
using EasyImageSharp.Formats.Tiff;
using EasyImageSharp.Fonts;
using EasyImageSharp.Metadata.Icc;
using EasyImageSharp.Metadata.Iptc;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using EasyImageSharp.Streaming;

namespace AotSmoke;

/// <summary>
/// The 1.3 feature surface under Native AOT: IPTC and BigTIFF writing, ICC conversion to sRGB, telemetry, the
/// managed font engine, the HDR and OpenEXR codecs with tone mapping, streaming transcode, colour LUTs and the
/// placeholder hashes. The two inputs that cannot be synthesised (a Display P3 profile and a font) are embedded
/// in the binary as manifest resources, so the published executable still needs no files beside it.
/// </summary>
internal static class FeatureChecks
{
    internal static void RunAll(Report report)
    {
        report.Record("iptc survives jpeg and bigtiff", IptcRoundTrip);
        report.Record("display p3 converts to srgb", ColorManagement);
        report.Record("telemetry meter and activity", Telemetry);
        report.Record("truetype text and watermark", Fonts);
        report.Record("hdr and exr round trip with tone map", HighDynamicRange);
        report.Record("streaming transcode and thumbnail", Streaming);
        report.Record("cube lut, blurhash and thumbhash", LutAndPlaceholders);
    }

    private static string? IptcRoundTrip()
    {
        using Image<Rgba32> source = Synth.Opaque(32, 24);
        var iptc = new IptcProfile();
        iptc.SetValue(IptcTag.Headline, "AOT headline");
        iptc.AddValue(IptcTag.Keywords, "café");
        source.Metadata.IptcProfile = iptc;

        using var jpegStream = new MemoryStream();
        source.SaveAsJpeg(jpegStream);
        using (Image<Rgba32> jpeg = Image.Load<Rgba32>(jpegStream.ToArray()))
        {
            if (Missing(jpeg.Metadata.IptcProfile) is string problem)
            {
                return "JPEG: " + problem;
            }
        }

        using var tiffStream = new MemoryStream();
        source.Save(tiffStream, new TiffEncoder { Format = TiffFormatType.BigTiff, RowsPerStrip = 8 });
        byte[] tiff = tiffStream.ToArray();
        int version = tiff[0] == (byte)'I' ? tiff[2] | (tiff[3] << 8) : (tiff[2] << 8) | tiff[3];
        if (version != 43)
        {
            return $"the TIFF encoder wrote version {version}, not BigTIFF (43)";
        }

        using Image<Rgba32> reloaded = Image.Load<Rgba32>(tiff);
        if (Missing(reloaded.Metadata.IptcProfile) is string tiffProblem)
        {
            return "BigTIFF: " + tiffProblem;
        }

        return MaxChannelError(source, reloaded) == 0 ? null : "BigTIFF changed the pixels";

        static string? Missing(IptcProfile? profile)
        {
            if (profile is null)
            {
                return "the IPTC profile was not read back";
            }

            return profile.GetValues(IptcTag.Headline).Contains("AOT headline") && profile.GetValues(IptcTag.Keywords).Contains("café")
                ? null
                : "the headline or the non-ASCII keyword did not survive";
        }
    }

    private static string? ColorManagement()
    {
        var profile = new IccProfile(Resource("display_p3_v4.icc"));
        using var source = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255));
        source.Metadata.IccProfile = profile;
        using var png = new MemoryStream();
        source.SaveAsPng(png);

        using Image<Rgba32> converted = Image.Load<Rgba32>(png.ToArray(), new DecoderOptions { ColorProfileHandling = ColorProfileHandling.ConvertToSrgb });
        if (converted.Metadata.IccProfile is not null)
        {
            return "the profile was kept after conversion";
        }

        if (!converted.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? record) || record.Status != ColorProfileConversionStatus.Converted)
        {
            return "the conversion was not recorded as Converted";
        }

        if (converted[0, 0].Equals(source[0, 0]))
        {
            return "the pixels were not converted";
        }

        using Image<Rgba32> mutated = source.Clone();
        mutated.Mutate(static ctx => ctx.ConvertToSrgb());
        return mutated.Metadata.IccProfile is null && MaxChannelError(converted, mutated) <= 1
            ? null
            : "ConvertToSrgb() disagrees with the convert-on-load path";
    }

    private static string? Telemetry()
    {
        var instruments = new HashSet<string>();
        var activities = new HashSet<string>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == ImageTelemetry.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>((instrument, _, _, _) => { lock (instruments) { instruments.Add(instrument.Name); } });
        meterListener.SetMeasurementEventCallback<long>((instrument, _, _, _) => { lock (instruments) { instruments.Add(instrument.Name); } });
        meterListener.Start();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == ImageTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (activities) { activities.Add(activity.OperationName); } },
        };
        ActivitySource.AddActivityListener(activityListener);

        using (Image<Rgba32> source = Synth.Opaque(16, 16))
        {
            using var png = new MemoryStream();
            source.SaveAsPng(png);
            using Image<Rgba32> loaded = Image.Load<Rgba32>(png.ToArray());
        }

        meterListener.RecordObservableInstruments();
        lock (instruments)
        {
            if (!instruments.Contains(ImageTelemetry.Instruments.DecodeDuration))
            {
                return $"no {ImageTelemetry.Instruments.DecodeDuration} measurement (saw: {string.Join(", ", instruments)})";
            }
        }

        lock (activities)
        {
            return activities.Contains(ImageTelemetry.Activities.Load)
                ? null
                : $"no {ImageTelemetry.Activities.Load} activity (saw: {string.Join(", ", activities)})";
        }
    }

    private static string? Fonts()
    {
        FontFace face = FontFace.Load(Resource("DejaVuSans-Subset.ttf"));
        var font = new Font(face, 24);
        TextMeasurement measured = TextMeasurer.Measure("AOT", font);
        if (measured.InkBounds.Width <= 0 || measured.InkBounds.Height <= 0)
        {
            return "the text measured as empty";
        }

        using var canvas = new Image<Rgba32>(80, 40, new Rgba32(255, 255, 255, 255));
        canvas.Mutate(ctx => ctx.DrawText("AOT", font, Color.Black, new PointF(2, 2)));
        if (Darkest(canvas) > 64)
        {
            return "DrawText left the canvas without dark ink";
        }

        using var photo = new Image<Rgba32>(120, 80, new Rgba32(255, 255, 255, 255));
        photo.Mutate(ctx => ctx.Watermark("AOT", font, Color.Black, new WatermarkOptions { Opacity = 1f }));
        return Darkest(photo) < 128 ? null : "the TrueType watermark drew nothing";

        static int Darkest(Image<Rgba32> image)
        {
            int darkest = 255;
            for (int y = 0; y < image.Height; y++)
            {
                for (int x = 0; x < image.Width; x++)
                {
                    darkest = Math.Min(darkest, image[x, y].R);
                }
            }

            return darkest;
        }
    }

    private static string? HighDynamicRange()
    {
        using var source = new Image<RgbaVector>(16, 8);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                source[x, y] = new RgbaVector(0.25f + (x * 0.5f), 0.5f + (y * 0.25f), 0.125f, 1f);
            }
        }

        using var hdr = new MemoryStream();
        source.SaveAsHdr(hdr);
        using Image<RgbaVector> fromHdr = Image.Load<RgbaVector>(hdr.ToArray());
        if (MaxRelativeError(source, fromHdr) > 0.01)
        {
            return "Radiance HDR round trip drifted more than RGBE precision allows";
        }

        using var exr = new MemoryStream();
        source.SaveAsExr(exr, new ExrEncoder { Compression = ExrCompression.Piz });
        using Image<RgbaVector> fromExr = Image.Load<RgbaVector>(exr.ToArray());
        if (MaxRelativeError(source, fromExr) > 0.001)
        {
            return "OpenEXR (PIZ) round trip drifted";
        }

        fromExr.Mutate(static ctx => ctx.ToneMap(ToneMapper.AcesFilmic));
        for (int y = 0; y < fromExr.Height; y++)
        {
            for (int x = 0; x < fromExr.Width; x++)
            {
                RgbaVector p = fromExr[x, y];
                if (p.R is < 0f or > 1f || p.G is < 0f or > 1f || p.B is < 0f or > 1f)
                {
                    return $"tone mapping left ({p.R}, {p.G}, {p.B}) outside [0, 1]";
                }
            }
        }

        return null;

        static double MaxRelativeError(Image<RgbaVector> expected, Image<RgbaVector> actual)
        {
            if (expected.Width != actual.Width || expected.Height != actual.Height)
            {
                return double.PositiveInfinity;
            }

            double worst = 0;
            for (int y = 0; y < expected.Height; y++)
            {
                for (int x = 0; x < expected.Width; x++)
                {
                    RgbaVector e = expected[x, y], a = actual[x, y];
                    worst = Math.Max(worst, Math.Abs(e.R - a.R) / e.R);
                    worst = Math.Max(worst, Math.Abs(e.G - a.G) / e.G);
                    worst = Math.Max(worst, Math.Abs(e.B - a.B) / e.B);
                }
            }

            return worst;
        }
    }

    private static string? Streaming()
    {
        using Image<Rgba32> source = Synth.Opaque(256, 192);
        using var jpeg = new MemoryStream();
        source.SaveAsJpeg(jpeg);
        byte[] encoded = jpeg.ToArray();

        using var output = new MemoryStream();
        TranscodeResult result = Image.Transcode(
            new MemoryStream(encoded),
            output,
            new TranscodeOptions { Resize = new ResizeOptions { Size = new Size(32, 24), Mode = ResizeMode.Max }, Encoder = new PngEncoder() });
        if (!result.StreamedDecode || !result.StreamedEncode)
        {
            return $"baseline JPEG to PNG did not stream (decode {result.StreamedDecode}, encode {result.StreamedEncode})";
        }

        ImageInfo transcoded = Image.Identify(output.ToArray());
        if (transcoded.Width != 32 || transcoded.Height != 24 || result.OutputSize != new Size(32, 24))
        {
            return $"transcode produced {transcoded.Width}x{transcoded.Height}";
        }

        using var thumbnail = new MemoryStream();
        Image.Thumbnail(new MemoryStream(encoded), thumbnail, 64, 64);
        ImageInfo thumb = Image.Identify(thumbnail.ToArray());
        return thumb.Width == 64 && thumb.Height == 48 ? null : $"thumbnail is {thumb.Width}x{thumb.Height}, expected 64x48";
    }

    private static string? LutAndPlaceholders()
    {
        const string Identity = "LUT_3D_SIZE 2\n0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 1\n";
        ColorLut lut = ColorLut.ParseCube(Identity);
        using Image<Rgba32> source = Synth.Opaque(24, 16);
        using Image<Rgba32> graded = source.Clone(ctx => ctx.ApplyLut(lut));
        if (MaxChannelError(source, graded) > 1)
        {
            return "an identity LUT changed the pixels";
        }

        string blurHash = BlurHash.Encode(source);
        using Image<Rgba32> blurred = BlurHash.Decode(blurHash, 12, 8);
        if (blurHash.Length != 28 || blurred.Width != 12 || blurred.Height != 8)
        {
            return $"BlurHash produced a {blurHash.Length}-character hash / {blurred.Width}x{blurred.Height} image";
        }

        byte[] thumbHash = ThumbHash.Encode(source);
        using Image<Rgba32> preview = ThumbHash.Decode(thumbHash);
        return thumbHash.Length > 0 && preview.Width > 0 && preview.Height > 0 ? null : "ThumbHash produced nothing";
    }

    private static int MaxChannelError(Image<Rgba32> expected, Image<Rgba32> actual)
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            return int.MaxValue;
        }

        int worst = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Rgba32 e = expected[x, y], a = actual[x, y];
                worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(e.R - a.R), Math.Abs(e.G - a.G)), Math.Max(Math.Abs(e.B - a.B), Math.Abs(e.A - a.A))));
            }
        }

        return worst;
    }

    private static byte[] Resource(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AotSmoke." + name)
            ?? throw new InvalidOperationException($"The embedded resource {name} is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
