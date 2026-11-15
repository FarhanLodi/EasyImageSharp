using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using EasyImageSharp.ColorSpaces;
using EasyImageSharp.Diagnostics;
using EasyImageSharp.Formats;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

// Listeners are process-wide: a MeterListener or ActivityListener attached here would also see measurements from
// tests running in parallel, and enable instruments under them. Run these tests on their own.
[CollectionDefinition("Telemetry isolation", DisableParallelization = true)]
public sealed class TelemetryIsolationCollection { }

[Collection("Telemetry isolation")]
public class TelemetryTests
{
    [Fact]
    public void Names_AreStableConstants()
    {
        Assert.Equal("EasyImageSharp", ImageTelemetry.MeterName);
        Assert.Equal("EasyImageSharp", ImageTelemetry.ActivitySourceName);
        Assert.Equal(ImageTelemetry.MeterName, ImageInstrumentation.Meter.Name);
        Assert.Equal(ImageTelemetry.ActivitySourceName, ImageInstrumentation.Source.Name);
        foreach (string name in new[]
        {
            ImageTelemetry.Instruments.DecodeDuration, ImageTelemetry.Instruments.EncodeDuration,
            ImageTelemetry.Instruments.ProcessDuration, ImageTelemetry.Instruments.DecodedPixels,
            ImageTelemetry.Instruments.DecodedBytes, ImageTelemetry.Instruments.EncodedBytes,
            ImageTelemetry.Instruments.Rejections,
        })
        {
            Assert.Matches("^easyimagesharp(\\.[a-z_]+)+$", name);
        }

        Assert.Equal("s", ImageInstrumentation.DecodeDuration.Unit);
        Assert.Equal("By", ImageInstrumentation.EncodedBytes.Unit);
        Assert.Equal("{pixel}", ImageInstrumentation.DecodedPixels.Unit);
    }

    [Fact]
    public void Load_RecordsDurationPixelsBytesAndAnActivity()
    {
        byte[] png = EncodePng(new Image<Rgba32>(7, 5, new Rgba32(1, 2, 3, 255)));
        using var metrics = new MetricCapture();
        using var traces = new ActivityCapture();

        using (Image<Rgba32> image = Image.Load<Rgba32>(png))
        {
            Assert.Equal(7, image.Width);
        }

        Measurement duration = Assert.Single(metrics.Named(ImageTelemetry.Instruments.DecodeDuration));
        Assert.True(duration.Value >= 0);
        Assert.Equal("PNG", duration.Tags[ImageTelemetry.Tags.Format]);
        Assert.Equal("Rgba32", duration.Tags[ImageTelemetry.Tags.PixelType]);
        Assert.Equal(ImageTelemetry.Outcomes.Success, duration.Tags[ImageTelemetry.Tags.Outcome]);
        Assert.Equal(35, Assert.Single(metrics.Named(ImageTelemetry.Instruments.DecodedPixels)).Value);
        Assert.Equal(png.Length, Assert.Single(metrics.Named(ImageTelemetry.Instruments.DecodedBytes)).Value);
        Assert.Empty(metrics.Named(ImageTelemetry.Instruments.Rejections));

        Activity activity = Assert.Single(traces.Stopped);
        Assert.Equal(ImageTelemetry.Activities.Load, activity.OperationName);
        Assert.Equal(ActivityKind.Internal, activity.Kind);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Equal("PNG", activity.GetTagItem(ImageTelemetry.Tags.Format));
        Assert.Equal(7, activity.GetTagItem(ImageTelemetry.Tags.Width));
        Assert.Equal(5, activity.GetTagItem(ImageTelemetry.Tags.Height));
        Assert.Equal(1, activity.GetTagItem(ImageTelemetry.Tags.FrameCount));
        Assert.Equal((long)png.Length, activity.GetTagItem(ImageTelemetry.Tags.EncodedSize));
        Assert.Null(activity.GetTagItem(ImageTelemetry.Tags.ColorProfileConversion));
    }

    [Fact]
    public async Task LoadAsyncAndStreams_AreMeasuredOnce()
    {
        byte[] png = EncodePng(new Image<L8>(3, 2));
        using var metrics = new MetricCapture();
        using var traces = new ActivityCapture();

        using (Image<L8> image = await Image.LoadAsync<L8>(new MemoryStream(png)))
        using (Image image2 = Image.Load(new MemoryStream(png)))
        {
            Assert.Equal(3, image.Width);
            Assert.Equal(3, image2.Width);
        }

        Measurement[] durations = metrics.Named(ImageTelemetry.Instruments.DecodeDuration).ToArray();
        Assert.Equal(2, durations.Length);
        Assert.Equal(new[] { "L8", "Rgba32" }, durations.Select(m => (string)m.Tags[ImageTelemetry.Tags.PixelType]!).Order());
        Assert.Equal(2, traces.Stopped.Count(a => a.OperationName == ImageTelemetry.Activities.Load));
        Assert.All(metrics.Named(ImageTelemetry.Instruments.DecodedBytes), m => Assert.Equal(png.Length, m.Value));
    }

    [Fact]
    public void Rejections_AreCountedByReason_AndActivitiesCarryTheError()
    {
        byte[] png = EncodePng(new Image<Rgba32>(16, 16));
        using var metrics = new MetricCapture();
        using var traces = new ActivityCapture();

        Assert.Throws<UnknownImageFormatException>(() => Image.Load<Rgba32>(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }));
        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(png.AsSpan(0, 40)));
        Assert.Throws<ImageSizeLimitExceededException>(() => Image.Load<Rgba32>(png, new DecoderOptions { MaxPixels = 10 }));
        Assert.Throws<ImageSizeLimitExceededException>(
            () => Image.Load<Rgba32>(new MemoryStream(png), new DecoderOptions { MaxEncodedBytes = 10 }));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => Image.Load<Rgba32>(
                png, new DecoderOptions { ExecutionOptions = new ImageExecutionOptions { CancellationToken = cancelled.Token } }));
        }

        Assert.Throws<UnknownImageFormatException>(() => Image.Identify(new byte[16]));

        Measurement[] rejections = metrics.Named(ImageTelemetry.Instruments.Rejections).ToArray();
        Assert.Equal(
            new[] { "unknown_format", "invalid_content", "size_limit", "size_limit", "cancelled", "unknown_format" },
            rejections.Select(r => (string)r.Tags[ImageTelemetry.Tags.RejectionReason]!));
        Assert.Equal(new[] { "load", "load", "load", "load", "load", "identify" }, rejections.Select(r => (string)r.Tags[ImageTelemetry.Tags.Operation]!));
        Assert.All(rejections, r => Assert.Equal(1, r.Value));
        Assert.False(rejections[0].Tags.ContainsKey(ImageTelemetry.Tags.Format));
        Assert.Equal("PNG", rejections[1].Tags[ImageTelemetry.Tags.Format]);
        Assert.Equal("PNG", rejections[2].Tags[ImageTelemetry.Tags.Format]);
        Assert.Empty(metrics.Named(ImageTelemetry.Instruments.DecodedPixels));
        Assert.Equal(
            new[] { "unknown_format", "invalid_content", "size_limit", "size_limit", "cancelled" },
            metrics.Named(ImageTelemetry.Instruments.DecodeDuration).Select(m => (string)m.Tags[ImageTelemetry.Tags.Outcome]!));

        Assert.Equal(6, traces.Stopped.Count);
        Assert.All(traces.Stopped, a => Assert.Equal(ActivityStatusCode.Error, a.Status));
        Activity unknown = traces.Stopped[0];
        Assert.Equal(typeof(UnknownImageFormatException).FullName, unknown.GetTagItem(ImageTelemetry.Tags.ErrorType));
        Assert.Equal("unknown_format", unknown.GetTagItem(ImageTelemetry.Tags.Outcome));
        Assert.Contains(unknown.Events, e => e.Name == "exception");
        Assert.Equal(ImageTelemetry.Activities.Identify, traces.Stopped[5].OperationName);
    }

    [Fact]
    public void Identify_StartsAnActivityButRecordsNoDecodeMetrics()
    {
        byte[] png = EncodePng(new Image<Rgba32>(9, 4));
        using var metrics = new MetricCapture();
        using var traces = new ActivityCapture();

        ImageInfo info = Image.Identify(png);
        Assert.Equal(9, info.Width);
        Assert.Empty(metrics.Named(ImageTelemetry.Instruments.DecodeDuration));
        Activity activity = Assert.Single(traces.Stopped);
        Assert.Equal(ImageTelemetry.Activities.Identify, activity.OperationName);
        Assert.Equal(9, activity.GetTagItem(ImageTelemetry.Tags.Width));
        Assert.Equal(4, activity.GetTagItem(ImageTelemetry.Tags.Height));
        Assert.Equal("PNG", activity.GetTagItem(ImageTelemetry.Tags.Format));
        Assert.Null(activity.GetTagItem(ImageTelemetry.Tags.PixelType));
    }

    [Fact]
    public async Task Save_RecordsEncodedBytesForSeekableAndNonSeekableStreams()
    {
        using var image = new Image<Rgb24>(12, 6, new Rgb24(10, 20, 30));
        using var metrics = new MetricCapture();
        using var traces = new ActivityCapture();

        using var seekable = new MemoryStream();
        image.SaveAsPng(seekable);
        var forwardOnly = new ForwardOnlyStream();
        image.SaveAsJpeg(forwardOnly);
        string base64 = image.ToBase64String(new Formats.Bmp.BmpEncoder());
        using var asyncTarget = new MemoryStream();
        await image.SaveAsGifAsync(asyncTarget);

        Measurement[] bytes = metrics.Named(ImageTelemetry.Instruments.EncodedBytes).ToArray();
        Assert.Equal(new[] { "PNG", "JPEG", "BMP", "GIF" }, bytes.Select(b => (string)b.Tags[ImageTelemetry.Tags.Format]!));
        Assert.Equal(seekable.Length, bytes[0].Value);
        Assert.Equal(forwardOnly.Written, bytes[1].Value);
        Assert.Equal(Convert.FromBase64String(base64).Length, bytes[2].Value);
        Assert.Equal(asyncTarget.Length, bytes[3].Value);
        Assert.True(forwardOnly.Written > 0);

        Measurement[] durations = metrics.Named(ImageTelemetry.Instruments.EncodeDuration).ToArray();
        Assert.Equal(4, durations.Length);
        Assert.All(durations, d => Assert.Equal("Rgb24", d.Tags[ImageTelemetry.Tags.PixelType]));
        Assert.All(durations, d => Assert.Equal(ImageTelemetry.Outcomes.Success, d.Tags[ImageTelemetry.Tags.Outcome]));

        Activity[] saves = traces.Stopped.Where(a => a.OperationName == ImageTelemetry.Activities.Save).ToArray();
        Assert.Equal(4, saves.Length);
        Assert.Equal(12, saves[0].GetTagItem(ImageTelemetry.Tags.Width));
        Assert.Equal(1, saves[0].GetTagItem(ImageTelemetry.Tags.FrameCount));
        Assert.Equal(seekable.Length, saves[0].GetTagItem(ImageTelemetry.Tags.EncodedSize));
        Assert.Equal("Rgb24", saves[0].GetTagItem(ImageTelemetry.Tags.PixelType));
    }

    [Fact]
    public void FailedSave_IsRecordedAsAnErrorAndRethrown()
    {
        using var image = new Image<Rgba32>(4, 4);
        using var metrics = new MetricCapture();
        using var traces = new ActivityCapture();

        Assert.Throws<IOException>(() => image.SaveAsPng(new FailingStream()));
        Measurement duration = Assert.Single(metrics.Named(ImageTelemetry.Instruments.EncodeDuration));
        Assert.Equal(ImageTelemetry.Outcomes.Error, duration.Tags[ImageTelemetry.Tags.Outcome]);
        Assert.Empty(metrics.Named(ImageTelemetry.Instruments.EncodedBytes));
        Activity activity = Assert.Single(traces.Stopped);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(typeof(IOException).FullName, activity.GetTagItem(ImageTelemetry.Tags.ErrorType));
    }

    [Fact]
    public void MutateAndClone_RecordProcessDurationAndNestedActivities()
    {
        using var image = new Image<Rgba32>(20, 10);
        using var metrics = new MetricCapture();
        using var traces = new ActivityCapture();
        using var outer = new ActivitySource("EasyImageSharp.Tests.Outer");
        using var outerListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == outer.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(outerListener);

        using (Activity? request = outer.StartActivity("request"))
        {
            image.Mutate(ctx => ctx.Resize(10, 5));
            using Image<Rgba32> copy = image.Clone(ctx => ctx.Flip(FlipMode.Horizontal));
            Assert.Throws<InvalidOperationException>(() => image.Mutate(_ => throw new InvalidOperationException("boom")));
            Assert.Same(request, Activity.Current);

            Assert.All(traces.Stopped, a => Assert.Equal(request!.SpanId, a.ParentSpanId));
        }

        Measurement[] durations = metrics.Named(ImageTelemetry.Instruments.ProcessDuration).ToArray();
        Assert.Equal(new[] { "mutate", "clone", "mutate" }, durations.Select(d => (string)d.Tags[ImageTelemetry.Tags.Operation]!));
        Assert.Equal(new[] { "success", "success", "error" }, durations.Select(d => (string)d.Tags[ImageTelemetry.Tags.Outcome]!));
        Assert.Equal(
            new[] { ImageTelemetry.Activities.Mutate, ImageTelemetry.Activities.Clone, ImageTelemetry.Activities.Mutate },
            traces.Stopped.Select(a => a.OperationName));
        Assert.Equal(10, traces.Stopped[0].GetTagItem(ImageTelemetry.Tags.Width));
        Assert.Equal(5, traces.Stopped[0].GetTagItem(ImageTelemetry.Tags.Height));
        Assert.Equal(ActivityStatusCode.Error, traces.Stopped[2].Status);
        Assert.Empty(metrics.Named(ImageTelemetry.Instruments.Rejections));
    }

    [Fact]
    public void ColorProfileConversion_IsTaggedOnTheLoadActivity()
    {
        byte[] tagged = FixturePath.Read("colormanagement/display_p3_tagged.png");
        using var traces = new ActivityCapture();
        using (Image.Load<Rgba32>(tagged, new DecoderOptions { ColorProfileHandling = ColorProfileHandling.ConvertToSrgb }))
        {
        }

        using (Image.Load<Rgba32>(FixturePath.Read("colormanagement/gray_tagged.png"),
            new DecoderOptions { ColorProfileHandling = ColorProfileHandling.ConvertToSrgb }))
        {
        }

        Assert.Equal("converted", traces.Stopped[0].GetTagItem(ImageTelemetry.Tags.ColorProfileConversion));
        Assert.Equal("unsupported", traces.Stopped[1].GetTagItem(ImageTelemetry.Tags.ColorProfileConversion));
    }

    [Fact]
    public void TiffPages_AreMeasuredPerPageWithoutActivities()
    {
        byte[] tiff = FixturePath.Read("colormanagement/multipage_tagged.tif");
        using var metrics = new MetricCapture();
        using var traces = new ActivityCapture();
        int pages = 0;
        foreach (Image<Rgba32> page in Image.LoadTiffPages<Rgba32>(new MemoryStream(tiff)))
        {
            page.Dispose();
            pages++;
        }

        Assert.Equal(3, pages);
        Assert.Equal(3, metrics.Named(ImageTelemetry.Instruments.DecodeDuration).Count());
        Assert.All(metrics.Named(ImageTelemetry.Instruments.DecodeDuration), m => Assert.Equal("TIFF", m.Tags[ImageTelemetry.Tags.Format]));
        Assert.Equal(new double[] { 64 * 48, 64 * 48, 64 * 48 }, metrics.Named(ImageTelemetry.Instruments.DecodedPixels).Select(m => m.Value));
        Assert.Equal(tiff.Length, Assert.Single(metrics.Named(ImageTelemetry.Instruments.DecodedBytes)).Value);
        Assert.Empty(traces.Stopped);

        Assert.Throws<InvalidImageContentException>(() => Image.LoadTiffPages<Rgba32>(new MemoryStream(tiff.AsSpan(0, 64).ToArray())).ToList());
        Measurement rejection = Assert.Single(metrics.Named(ImageTelemetry.Instruments.Rejections));
        Assert.Equal("invalid_content", rejection.Tags[ImageTelemetry.Tags.RejectionReason]);
        Assert.Equal("TIFF", rejection.Tags[ImageTelemetry.Tags.Format]);
    }

    [Fact]
    public void Outputs_AreIdenticalWithAndWithoutListeners()
    {
        byte[] tagged = FixturePath.Read("colormanagement/adobe_rgb_tagged.png");
        var options = new DecoderOptions { ColorProfileHandling = ColorProfileHandling.ConvertToSrgb };
        (byte[] Pixels, byte[] Encoded) Run()
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(tagged, options);
            image.Mutate(ctx => ctx.GaussianBlur(1.2f));
            var pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);
            using var encoded = new MemoryStream();
            image.SaveAsPng(encoded);
            return (pixels, encoded.ToArray());
        }

        Assert.Null(Activity.Current);
        (byte[] Pixels, byte[] Encoded) quiet = Run();
        Assert.Null(Activity.Current);
        (byte[] Pixels, byte[] Encoded) observed;
        using (new MetricCapture())
        using (new ActivityCapture())
        {
            observed = Run();
            Assert.Null(Activity.Current);
        }

        Assert.Equal(quiet.Pixels, observed.Pixels);
        Assert.Equal(quiet.Encoded, observed.Encoded);
    }

    [Fact]
    public void WithoutListeners_MeasuringAllocatesNothing()
    {
        using var image = new Image<Rgba32>(2, 2);
        Assert.False(ImageInstrumentation.Source.HasListeners(), "An activity listener is still attached.");
        Assert.False(ImageInstrumentation.DecodeDuration.Enabled, "A meter listener still enables the decode histogram.");

        // Warm up the static state, then count what a burst of measured calls allocates on this thread.
        OperationTelemetry warm = OperationTelemetry.Start<Rgba32>(TelemetryOperation.Load);
        warm.CompleteDecode(image, 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            OperationTelemetry load = OperationTelemetry.Start<Rgba32>(TelemetryOperation.Load);
            load.SetFormat("PNG");
            load.CompleteDecode(image, 100);
            OperationTelemetry mutate = OperationTelemetry.Start<Rgba32>(TelemetryOperation.Mutate);
            mutate.CompleteProcess(image);
            OperationTelemetry save = OperationTelemetry.Start(TelemetryOperation.Save, null);
            Assert.False(save.IsRecording);
            save.SetFormat("PNG");
            save.CompleteEncode(image, 100);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static byte[] EncodePng<TPixel>(Image<TPixel> image)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using (image)
        {
            using var stream = new MemoryStream();
            image.SaveAsPng(stream);
            return stream.ToArray();
        }
    }

    private sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    /// <summary>Collects every measurement of the library's meter while alive.</summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly ConcurrentQueue<Measurement> measurements = new();

        public MetricCapture()
        {
            this.listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ImageTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            this.listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => this.Add(instrument, value, tags));
            this.listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => this.Add(instrument, value, tags));

            // On .NET 8 MeterListener.Dispose only unsubscribes from its instruments when MeasurementsCompleted is set
            // (the runtime's disable loop sits inside that check); without it the listener keeps receiving measurements
            // and the instruments stay Enabled for the rest of the process. OpenTelemetry's SDK always sets it.
            this.listener.MeasurementsCompleted = static (_, _) => { };
            this.listener.Start();
        }

        public IEnumerable<Measurement> Named(string instrument) => this.measurements.Where(m => m.Instrument == instrument);

        public void Dispose() => this.listener.Dispose();

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = new Dictionary<string, object?>();
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            this.measurements.Enqueue(new Measurement(instrument.Name, value, copy));
        }
    }

    /// <summary>Records every activity of the library's source, fully sampled, while alive.</summary>
    private sealed class ActivityCapture : IDisposable
    {
        private readonly ActivityListener listener;
        private readonly ConcurrentQueue<Activity> stopped = new();

        public ActivityCapture()
        {
            this.listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ImageTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => this.stopped.Enqueue(activity),
            };
            ActivitySource.AddActivityListener(this.listener);
        }

        public IReadOnlyList<Activity> Stopped => this.stopped.ToArray();

        public void Dispose() => this.listener.Dispose();
    }

    /// <summary>A write-only stream that cannot seek, like an HTTP response body.</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        public long Written { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => this.Written += count;
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new IOException("disk full");

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("disk full");
    }
}
