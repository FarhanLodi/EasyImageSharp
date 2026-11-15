using System.Diagnostics;
using System.Diagnostics.Metrics;
using EasyImageSharp.ColorSpaces;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Diagnostics;

/// <summary>The entry points <see cref="OperationTelemetry"/> measures.</summary>
internal enum TelemetryOperation : byte
{
    Load,

    /// <summary>One page of a lazy TIFF decode: metrics only, no activity.</summary>
    LoadPage,
    Identify,
    Save,
    Mutate,
    Clone,
}

/// <summary>
/// Measures one library call: its duration histogram, its counters and its activity. A mutable struct held in a
/// local, so a call with no listener allocates nothing; <see cref="Start"/> decides once what is being recorded.
/// </summary>
/// <remarks>
/// The intended shape is a <c>try</c> whose last statement calls one of the <c>Complete</c> methods, followed by
/// <c>catch (Exception ex) when (telemetry.Fail(ex)) { throw; }</c>. <see cref="Fail"/> always returns
/// <see langword="false"/>, so the exception filter records the failure without catching or unwinding anything.
/// Every method is a no-op after the first <c>Complete</c> or <see cref="Fail"/>.
/// </remarks>
internal struct OperationTelemetry
{
    private readonly TelemetryOperation operation;
    private readonly bool timed;
    private readonly long startTimestamp;
    private readonly Activity? activity;
    private string? pixelType;
    private string? format;
    private bool finished;

    private OperationTelemetry(TelemetryOperation operation, string? pixelType, bool timed, long startTimestamp, Activity? activity)
    {
        this.operation = operation;
        this.pixelType = pixelType;
        this.timed = timed;
        this.startTimestamp = startTimestamp;
        this.activity = activity;
        this.format = null;
        this.finished = false;
    }

    /// <summary>True when anything about this call will be recorded.</summary>
    public readonly bool IsRecording => this.timed || this.activity is not null || this.operation switch
    {
        TelemetryOperation.Load or TelemetryOperation.LoadPage => ImageInstrumentation.DecodedPixels.Enabled
            || ImageInstrumentation.DecodedBytes.Enabled || ImageInstrumentation.Rejections.Enabled,
        TelemetryOperation.Identify => ImageInstrumentation.Rejections.Enabled,
        TelemetryOperation.Save => ImageInstrumentation.EncodedBytes.Enabled,
        _ => false,
    };

    private readonly bool HasActivityData => this.activity is { IsAllDataRequested: true };

    /// <summary>Starts measuring a generic-pixel entry point.</summary>
    public static OperationTelemetry Start<TPixel>(TelemetryOperation operation)
        where TPixel : unmanaged, IPixel<TPixel>
        => Start(operation, PixelTypeName<TPixel>.Value);

    /// <summary>Starts measuring an entry point; the activity, if any, becomes <see cref="Activity.Current"/>.</summary>
    public static OperationTelemetry Start(TelemetryOperation operation, string? pixelType)
    {
        Histogram<double>? duration = operation switch
        {
            TelemetryOperation.Load or TelemetryOperation.LoadPage => ImageInstrumentation.DecodeDuration,
            TelemetryOperation.Save => ImageInstrumentation.EncodeDuration,
            TelemetryOperation.Mutate or TelemetryOperation.Clone => ImageInstrumentation.ProcessDuration,
            _ => null,
        };
        bool timed = duration is not null && duration.Enabled;
        long start = timed ? Stopwatch.GetTimestamp() : 0;
        Activity? activity = null;
        if (operation != TelemetryOperation.LoadPage && ImageInstrumentation.Source.HasListeners())
        {
            activity = ImageInstrumentation.Source.StartActivity(ActivityName(operation), ActivityKind.Internal);
            if (activity is { IsAllDataRequested: true } && pixelType is not null)
            {
                activity.SetTag(ImageTelemetry.Tags.PixelType, pixelType);
            }
        }

        return new OperationTelemetry(operation, pixelType, timed, start, activity);
    }

    /// <summary>Records the pixel type once it is known (saves take a non-generic image).</summary>
    public void SetPixelType(string name)
    {
        this.pixelType = name;
        if (this.HasActivityData)
        {
            this.activity!.SetTag(ImageTelemetry.Tags.PixelType, name);
        }
    }

    /// <summary>Records the detected or written format.</summary>
    public void SetFormat(string name)
    {
        this.format = name;
        if (this.HasActivityData)
        {
            this.activity!.SetTag(ImageTelemetry.Tags.Format, name);
        }
    }

    /// <summary>Completes a successful decode of <paramref name="encodedBytes"/> bytes into <paramref name="image"/>.</summary>
    public void CompleteDecode<TPixel>(Image<TPixel> image, long encodedBytes, bool countEncodedBytes = true)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (this.finished)
        {
            return;
        }

        this.finished = true;
        if (ImageInstrumentation.DecodedPixels.Enabled)
        {
            long pixels = 0;
            foreach (ImageFrame<TPixel> frame in image.Frames.InnerList)
            {
                pixels += (long)frame.Width * frame.Height;
            }

            ImageInstrumentation.DecodedPixels.Add(pixels, this.FormatAndPixelTags());
        }

        if (countEncodedBytes && ImageInstrumentation.DecodedBytes.Enabled)
        {
            ImageInstrumentation.DecodedBytes.Add(encodedBytes, this.FormatTag());
        }

        this.RecordDuration(ImageTelemetry.Outcomes.Success);
        if (this.activity is not null)
        {
            if (this.activity.IsAllDataRequested)
            {
                this.TagImage(image.Width, image.Height, image.Frames.Count, encodedBytes);
                if (image.Metadata.TryGetFormatMetadata(out ColorProfileConversionMetadata? conversion))
                {
                    this.activity.SetTag(
                        ImageTelemetry.Tags.ColorProfileConversion, ImageInstrumentation.ColorConversionName(conversion.Status));
                }
            }

            this.activity.Dispose();
        }
    }

    /// <summary>Completes a successful identification.</summary>
    public void CompleteIdentify(ImageInfo info, long encodedBytes)
    {
        if (this.finished)
        {
            return;
        }

        this.finished = true;
        if (this.activity is not null)
        {
            if (this.activity.IsAllDataRequested)
            {
                this.TagImage(info.Width, info.Height, info.FrameCount, encodedBytes);
            }

            this.activity.Dispose();
        }
    }

    /// <summary>Completes a successful encode that wrote <paramref name="bytesWritten"/> bytes.</summary>
    public void CompleteEncode(Image image, long bytesWritten)
    {
        if (this.finished)
        {
            return;
        }

        this.finished = true;
        if (ImageInstrumentation.EncodedBytes.Enabled)
        {
            ImageInstrumentation.EncodedBytes.Add(bytesWritten, this.FormatTag());
        }

        this.RecordDuration(ImageTelemetry.Outcomes.Success);
        if (this.activity is not null)
        {
            if (this.activity.IsAllDataRequested)
            {
                this.TagImage(image.Width, image.Height, FrameCountOf(image), bytesWritten);
            }

            this.activity.Dispose();
        }
    }

    /// <summary>Completes a successful <c>Mutate</c> or <c>Clone</c> whose result is <paramref name="image"/>.</summary>
    public void CompleteProcess<TPixel>(Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (this.finished)
        {
            return;
        }

        this.finished = true;
        this.RecordDuration(ImageTelemetry.Outcomes.Success);
        if (this.activity is not null)
        {
            if (this.activity.IsAllDataRequested)
            {
                this.activity.SetTag(ImageTelemetry.Tags.Width, image.Width);
                this.activity.SetTag(ImageTelemetry.Tags.Height, image.Height);
                this.activity.SetTag(ImageTelemetry.Tags.FrameCount, image.Frames.Count);
                this.activity.SetTag(ImageTelemetry.Tags.Outcome, ImageTelemetry.Outcomes.Success);
            }

            this.activity.Dispose();
        }
    }

    /// <summary>
    /// Records a failed call: the duration with its outcome, a rejection for loads and identifications refused under
    /// the exception contract, and an error status on the activity. Always returns <see langword="false"/> so it can be
    /// used as an exception filter that never catches.
    /// </summary>
    public bool Fail(Exception exception)
    {
        if (this.finished)
        {
            return false;
        }

        this.finished = true;
        string outcome = ImageInstrumentation.Classify(exception);
        this.RecordDuration(outcome);
        if (this.operation is TelemetryOperation.Load or TelemetryOperation.LoadPage or TelemetryOperation.Identify
            && outcome is not ImageTelemetry.Outcomes.Error && ImageInstrumentation.Rejections.Enabled)
        {
            var tags = new TagList
            {
                { ImageTelemetry.Tags.Operation, this.operation == TelemetryOperation.Identify ? "identify" : "load" },
                { ImageTelemetry.Tags.RejectionReason, outcome },
            };
            if (this.format is not null)
            {
                tags.Add(ImageTelemetry.Tags.Format, this.format);
            }

            ImageInstrumentation.Rejections.Add(1, tags);
        }

        if (this.activity is not null)
        {
            if (this.activity.IsAllDataRequested)
            {
                this.activity.SetTag(ImageTelemetry.Tags.Outcome, outcome);
                this.activity.SetTag(ImageTelemetry.Tags.ErrorType, exception.GetType().FullName);
#if NET9_0_OR_GREATER
                this.activity.AddException(exception);
#else
                this.activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                {
                    { "exception.type", exception.GetType().FullName },
                    { "exception.message", exception.Message },
                    { "exception.stacktrace", exception.ToString() },
                }));
#endif
            }

            this.activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            this.activity.Dispose();
        }

        return false;
    }

    private static string ActivityName(TelemetryOperation operation) => operation switch
    {
        TelemetryOperation.Identify => ImageTelemetry.Activities.Identify,
        TelemetryOperation.Save => ImageTelemetry.Activities.Save,
        TelemetryOperation.Mutate => ImageTelemetry.Activities.Mutate,
        TelemetryOperation.Clone => ImageTelemetry.Activities.Clone,
        _ => ImageTelemetry.Activities.Load,
    };

    /// <summary>
    /// The frame count of a non-generic image without reflection: every pixel format the library defines is matched
    /// directly. An image of a caller-defined pixel format reports -1 and the tag is left off.
    /// </summary>
    private static int FrameCountOf(Image image) => image switch
    {
        Image<Rgba32> i => i.Frames.Count,
        Image<Rgb24> i => i.Frames.Count,
        Image<Bgra32> i => i.Frames.Count,
        Image<Bgr24> i => i.Frames.Count,
        Image<Argb32> i => i.Frames.Count,
        Image<Abgr32> i => i.Frames.Count,
        Image<L8> i => i.Frames.Count,
        Image<L16> i => i.Frames.Count,
        Image<La16> i => i.Frames.Count,
        Image<La32> i => i.Frames.Count,
        Image<A8> i => i.Frames.Count,
        Image<Rgb48> i => i.Frames.Count,
        Image<Rgba64> i => i.Frames.Count,
        Image<RgbaVector> i => i.Frames.Count,
        _ => -1,
    };

    private readonly void RecordDuration(string outcome)
    {
        if (!this.timed)
        {
            return;
        }

        double seconds = Stopwatch.GetElapsedTime(this.startTimestamp).TotalSeconds;
        Histogram<double> histogram = this.operation switch
        {
            TelemetryOperation.Save => ImageInstrumentation.EncodeDuration,
            TelemetryOperation.Mutate or TelemetryOperation.Clone => ImageInstrumentation.ProcessDuration,
            _ => ImageInstrumentation.DecodeDuration,
        };
        TagList tags = this.FormatAndPixelTags();
        if (this.operation is TelemetryOperation.Mutate or TelemetryOperation.Clone)
        {
            tags.Add(ImageTelemetry.Tags.Operation, this.operation == TelemetryOperation.Mutate ? "mutate" : "clone");
        }

        tags.Add(ImageTelemetry.Tags.Outcome, outcome);
        histogram.Record(seconds, tags);
    }

    private readonly TagList FormatAndPixelTags()
    {
        var tags = new TagList();
        if (this.format is not null)
        {
            tags.Add(ImageTelemetry.Tags.Format, this.format);
        }

        if (this.pixelType is not null)
        {
            tags.Add(ImageTelemetry.Tags.PixelType, this.pixelType);
        }

        return tags;
    }

    private readonly TagList FormatTag()
    {
        var tags = new TagList();
        if (this.format is not null)
        {
            tags.Add(ImageTelemetry.Tags.Format, this.format);
        }

        return tags;
    }

    private readonly void TagImage(int width, int height, int frameCount, long encodedBytes)
    {
        Activity activity = this.activity!;
        activity.SetTag(ImageTelemetry.Tags.Width, width);
        activity.SetTag(ImageTelemetry.Tags.Height, height);
        if (frameCount >= 0)
        {
            activity.SetTag(ImageTelemetry.Tags.FrameCount, frameCount);
        }

        activity.SetTag(ImageTelemetry.Tags.EncodedSize, encodedBytes);
        activity.SetTag(ImageTelemetry.Tags.Outcome, ImageTelemetry.Outcomes.Success);
    }
}
