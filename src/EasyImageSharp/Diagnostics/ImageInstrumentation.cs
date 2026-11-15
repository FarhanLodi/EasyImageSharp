using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using EasyImageSharp.ColorSpaces;
using EasyImageSharp.Formats;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Diagnostics;

/// <summary>
/// The meter, activity source and instruments behind <see cref="ImageTelemetry"/>, plus the low-cardinality names
/// the measurements are tagged with. Everything here is created on first use and is cheap to consult when nobody
/// listens: callers check <see cref="Instrument.Enabled"/> or <see cref="ActivitySource.HasListeners"/> first.
/// </summary>
internal static class ImageInstrumentation
{
    private static readonly string? Version = typeof(ImageInstrumentation).Assembly.GetName().Version?.ToString(3);

#if NET9_0_OR_GREATER
    /// <summary>
    /// Bucket boundaries advised for the duration histograms: the OpenTelemetry default boundaries are tuned for
    /// milliseconds, which would put almost every image operation, measured in seconds, into the first bucket.
    /// </summary>
    private static readonly double[] DurationBuckets =
        [0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];
#endif

    private static readonly ConcurrentDictionary<Type, string> ImagePixelTypes = new();
    private static readonly ConcurrentDictionary<Type, string> EncoderFormats = new();

    /// <summary>The activity source named <see cref="ImageTelemetry.ActivitySourceName"/>.</summary>
    public static readonly ActivitySource Source = new(ImageTelemetry.ActivitySourceName, Version);

    /// <summary>The meter named <see cref="ImageTelemetry.MeterName"/>.</summary>
    public static readonly Meter Meter = new(ImageTelemetry.MeterName, Version);

    public static readonly Histogram<double> DecodeDuration = CreateDuration(
        ImageTelemetry.Instruments.DecodeDuration, "Time to decode an image, including reading a stream and any colour conversion.");

    public static readonly Histogram<double> EncodeDuration = CreateDuration(
        ImageTelemetry.Instruments.EncodeDuration, "Time to encode and write an image.");

    public static readonly Histogram<double> ProcessDuration = CreateDuration(
        ImageTelemetry.Instruments.ProcessDuration, "Time spent in one Mutate or Clone call.");

    public static readonly Counter<long> DecodedPixels = Meter.CreateCounter<long>(
        ImageTelemetry.Instruments.DecodedPixels, "{pixel}", "Pixels produced by successful decodes, over every frame.");

    public static readonly Counter<long> DecodedBytes = Meter.CreateCounter<long>(
        ImageTelemetry.Instruments.DecodedBytes, "By", "Encoded bytes consumed by successful decodes.");

    public static readonly Counter<long> EncodedBytes = Meter.CreateCounter<long>(
        ImageTelemetry.Instruments.EncodedBytes, "By", "Encoded bytes written by successful encodes.");

    public static readonly Counter<long> Rejections = Meter.CreateCounter<long>(
        ImageTelemetry.Instruments.Rejections, "{rejection}", "Loads and identifications refused under the exception contract.");

    /// <summary>
    /// Encodes <paramref name="image"/> exactly as <see cref="Image.AcceptEncoder"/> does, measuring the encode when a
    /// listener is attached. With no listener this is one extra branch around the original call.
    /// </summary>
    public static void Encode(Image image, IImageEncoder encoder, Stream stream)
    {
        OperationTelemetry telemetry = OperationTelemetry.Start(TelemetryOperation.Save, pixelType: null);
        if (!telemetry.IsRecording)
        {
            image.AcceptEncoder(encoder, stream);
            return;
        }

        try
        {
            telemetry.SetPixelType(PixelTypeOf(image));
            telemetry.SetFormat(FormatOf(encoder));
            long written;
            if (stream is not null && stream.CanSeek)
            {
                // No encoder seeks, so the position delta is the number of bytes written.
                long before = stream.Position;
                image.AcceptEncoder(encoder, stream);
                written = stream.Position - before;
            }
            else if (stream is not null)
            {
                var counting = new ByteCountingStream(stream);
                image.AcceptEncoder(encoder, counting);
                written = counting.BytesWritten;
            }
            else
            {
                // Let the encoder raise whatever it raises for a null stream, exactly as it would unmeasured.
                image.AcceptEncoder(encoder, stream!);
                written = 0;
            }

            telemetry.CompleteEncode(image, written);
        }
        catch (Exception ex) when (telemetry.Fail(ex))
        {
            // Unreachable: Fail records the failure and returns false, so the exception propagates unwound.
            throw;
        }
    }

    /// <summary>The pixel type name of a non-generic image reference, for example <c>Rgba32</c>.</summary>
    public static string PixelTypeOf(Image image)
        => ImagePixelTypes.GetOrAdd(image.GetType(), static type =>
            type.IsGenericType && type.GenericTypeArguments.Length == 1 ? type.GenericTypeArguments[0].Name : type.Name);

    /// <summary>
    /// The format name an encoder writes: the <see cref="ImageFormat.Name"/> of the registered format whose default
    /// encoder has the same type, else the encoder type's name without its <c>Encoder</c> suffix, upper-cased.
    /// </summary>
    public static string FormatOf(IImageEncoder encoder)
        => EncoderFormats.GetOrAdd(encoder.GetType(), static type =>
        {
            foreach (ImageFormat format in ImageFormat.All)
            {
                if (format.CanEncode && format.CreateEncoder().GetType() == type)
                {
                    return format.Name;
                }
            }

            string name = type.Name;
            return (name.EndsWith("Encoder", StringComparison.Ordinal) && name.Length > 7 ? name[..^7] : name)
                .ToUpperInvariant();
        });

    /// <summary>Maps an exception to one of the <see cref="ImageTelemetry.Outcomes"/> strings.</summary>
    public static string Classify(Exception exception)
    {
        if (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
        {
            exception = aggregate.InnerExceptions[0];
        }

        return exception switch
        {
            UnknownImageFormatException => ImageTelemetry.Outcomes.UnknownFormat,
            ImageSizeLimitExceededException => ImageTelemetry.Outcomes.SizeLimit,
            ImageFormatException => ImageTelemetry.Outcomes.InvalidContent,
            OperationCanceledException => ImageTelemetry.Outcomes.Cancelled,
            NotSupportedException => ImageTelemetry.Outcomes.Unsupported,
            _ => ImageTelemetry.Outcomes.Error,
        };
    }

    /// <summary>The <see cref="ImageTelemetry.Tags.ColorProfileConversion"/> value for a status.</summary>
    public static string ColorConversionName(ColorProfileConversionStatus status) => status switch
    {
        ColorProfileConversionStatus.NoProfile => "no_profile",
        ColorProfileConversionStatus.Converted => "converted",
        ColorProfileConversionStatus.AlreadySrgb => "already_srgb",
        ColorProfileConversionStatus.Unsupported => "unsupported",
        ColorProfileConversionStatus.Invalid => "invalid",
        _ => "unknown",
    };

    private static Histogram<double> CreateDuration(string name, string description)
#if NET9_0_OR_GREATER
        => Meter.CreateHistogram(name, "s", description, tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });
#else
        => Meter.CreateHistogram<double>(name, "s", description);
#endif
}

/// <summary>The pixel type name of <typeparamref name="TPixel"/>, computed once per type.</summary>
internal static class PixelTypeName<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    public static readonly string Value = typeof(TPixel).Name;
}
