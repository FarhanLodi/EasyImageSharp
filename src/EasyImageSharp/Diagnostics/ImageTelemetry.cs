namespace EasyImageSharp.Diagnostics;

/// <summary>
/// Names of the metrics and traces the library publishes through the BCL's <c>System.Diagnostics.Metrics</c> and
/// <c>System.Diagnostics.ActivitySource</c>, which OpenTelemetry and <c>dotnet-counters</c> consume directly:
/// <c>builder.AddMeter(ImageTelemetry.MeterName)</c> and <c>builder.AddSource(ImageTelemetry.ActivitySourceName)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is measured unless a listener is attached: every call site checks <c>Instrument.Enabled</c> and
/// <c>ActivitySource.HasListeners()</c> first, so an application that never subscribes pays a few field reads per
/// operation and no allocation. No package is referenced; the instruments are plain BCL types and are trimming and
/// Native AOT safe.
/// </para>
/// <para>
/// Instrument and attribute names follow the OpenTelemetry semantic-convention style: lower-case, dot-separated,
/// namespaced under <c>easyimagesharp</c>, with UCUM units (<c>s</c>, <c>By</c>, <c>{pixel}</c>). Durations are
/// histograms in seconds. Tag values are low-cardinality: format names as <see cref="Formats.ImageFormat.Name"/>
/// reports them (<c>PNG</c>, <c>JPEG</c>, ...), pixel type names such as <c>Rgba32</c>, and the outcome strings in
/// <see cref="Outcomes"/>.
/// </para>
/// <para>
/// Activities (spans) are started for <c>Image.Load</c>/<c>LoadAsync</c>, <c>Image.Identify</c>/<c>IdentifyAsync</c>,
/// every save, and <c>Mutate</c>/<c>Clone</c>, named as in <see cref="Activities"/>. A failed operation sets the
/// activity status to <c>Error</c>, adds the <c>error.type</c> tag and an <c>exception</c> event. Pages decoded lazily
/// by <c>Image.LoadTiffPages</c> are measured by the metrics but do not start activities, because each page is produced
/// between two calls into the caller's loop.
/// </para>
/// </remarks>
public static class ImageTelemetry
{
    /// <summary>The name of the <c>System.Diagnostics.Metrics.Meter</c> every instrument belongs to.</summary>
    public const string MeterName = "EasyImageSharp";

    /// <summary>The name of the <c>System.Diagnostics.ActivitySource</c> every activity is started from.</summary>
    public const string ActivitySourceName = "EasyImageSharp";

    /// <summary>Names of the metric instruments.</summary>
    public static class Instruments
    {
        /// <summary>
        /// Histogram, seconds: time to decode an image, including the bounded read of a stream and any requested colour
        /// conversion. Tags: <see cref="Tags.Format"/> (when detected), <see cref="Tags.PixelType"/>, <see cref="Tags.Outcome"/>.
        /// </summary>
        public const string DecodeDuration = "easyimagesharp.decode.duration";

        /// <summary>
        /// Histogram, seconds: time to encode and write an image. Tags: <see cref="Tags.Format"/>,
        /// <see cref="Tags.PixelType"/>, <see cref="Tags.Outcome"/>.
        /// </summary>
        public const string EncodeDuration = "easyimagesharp.encode.duration";

        /// <summary>
        /// Histogram, seconds: time spent in one <c>Mutate</c> or <c>Clone</c> call, all of its operations together.
        /// Tags: <see cref="Tags.Operation"/> (<c>mutate</c> or <c>clone</c>), <see cref="Tags.PixelType"/>,
        /// <see cref="Tags.Outcome"/>.
        /// </summary>
        public const string ProcessDuration = "easyimagesharp.process.duration";

        /// <summary>
        /// Counter, <c>{pixel}</c>: pixels produced by successful decodes, summed over every frame.
        /// Tags: <see cref="Tags.Format"/>, <see cref="Tags.PixelType"/>.
        /// </summary>
        public const string DecodedPixels = "easyimagesharp.decode.pixels";

        /// <summary>
        /// Counter, bytes: encoded input consumed by successful decodes (a lazily decoded TIFF counts its buffered input
        /// once, with its first page). Tags: <see cref="Tags.Format"/>.
        /// </summary>
        public const string DecodedBytes = "easyimagesharp.decode.bytes";

        /// <summary>Counter, bytes: encoded output written by successful encodes. Tags: <see cref="Tags.Format"/>.</summary>
        public const string EncodedBytes = "easyimagesharp.encode.bytes";

        /// <summary>
        /// Counter, <c>{rejection}</c>: loads and identifications refused under the library's exception contract.
        /// Tags: <see cref="Tags.Operation"/> (<c>load</c> or <c>identify</c>), <see cref="Tags.RejectionReason"/>
        /// (<see cref="Outcomes.UnknownFormat"/>, <see cref="Outcomes.InvalidContent"/>, <see cref="Outcomes.SizeLimit"/>,
        /// <see cref="Outcomes.Cancelled"/> or <see cref="Outcomes.Unsupported"/>), and <see cref="Tags.Format"/> when
        /// the format had been detected.
        /// </summary>
        public const string Rejections = "easyimagesharp.decode.rejections";
    }

    /// <summary>Names of the activities (spans).</summary>
    public static class Activities
    {
        /// <summary>An <c>Image.Load</c> or <c>Image.LoadAsync</c> call.</summary>
        public const string Load = "Image.Load";

        /// <summary>An <c>Image.Identify</c> or <c>Image.IdentifyAsync</c> call.</summary>
        public const string Identify = "Image.Identify";

        /// <summary>Any save or encode-to-string call.</summary>
        public const string Save = "Image.Save";

        /// <summary>A <c>Mutate</c> call.</summary>
        public const string Mutate = "Image.Mutate";

        /// <summary>A <c>Clone</c> call that applies processing operations.</summary>
        public const string Clone = "Image.Clone";
    }

    /// <summary>Names of the tags (attributes) on measurements and activities.</summary>
    public static class Tags
    {
        /// <summary>The image format name, for example <c>PNG</c> or <c>JPEG</c>.</summary>
        public const string Format = "easyimagesharp.image.format";

        /// <summary>The in-memory pixel type name, for example <c>Rgba32</c>.</summary>
        public const string PixelType = "easyimagesharp.pixel.type";

        /// <summary>One of the <see cref="Outcomes"/> strings.</summary>
        public const string Outcome = "easyimagesharp.outcome";

        /// <summary>On <see cref="Instruments.Rejections"/>: why the input was refused, one of the rejection <see cref="Outcomes"/>.</summary>
        public const string RejectionReason = "easyimagesharp.rejection.reason";

        /// <summary>The entry point: <c>load</c>, <c>identify</c>, <c>mutate</c> or <c>clone</c>.</summary>
        public const string Operation = "easyimagesharp.operation";

        /// <summary>On activities: the width of the (root frame of the) image, in pixels.</summary>
        public const string Width = "easyimagesharp.image.width";

        /// <summary>On activities: the height of the (root frame of the) image, in pixels.</summary>
        public const string Height = "easyimagesharp.image.height";

        /// <summary>On activities: the number of frames.</summary>
        public const string FrameCount = "easyimagesharp.image.frame_count";

        /// <summary>On activities: the encoded size in bytes - read by a load or identify, written by a save.</summary>
        public const string EncodedSize = "easyimagesharp.encoded.size";

        /// <summary>
        /// On load activities that requested <c>ColorProfileHandling.ConvertToSrgb</c>: the image-level outcome,
        /// <c>no_profile</c>, <c>converted</c>, <c>already_srgb</c>, <c>unsupported</c> or <c>invalid</c>.
        /// </summary>
        public const string ColorProfileConversion = "easyimagesharp.color_profile.conversion";

        /// <summary>On failed activities: the full name of the exception type (the OpenTelemetry <c>error.type</c> attribute).</summary>
        public const string ErrorType = "error.type";
    }

    /// <summary>Values of <see cref="Tags.Outcome"/> and <see cref="Tags.RejectionReason"/>.</summary>
    public static class Outcomes
    {
        /// <summary>The operation completed.</summary>
        public const string Success = "success";

        /// <summary>The input matched no known format (<see cref="UnknownImageFormatException"/>).</summary>
        public const string UnknownFormat = "unknown_format";

        /// <summary>The input was malformed (<see cref="InvalidImageContentException"/> or another <see cref="ImageFormatException"/>).</summary>
        public const string InvalidContent = "invalid_content";

        /// <summary>A configured limit was exceeded (<see cref="ImageSizeLimitExceededException"/>).</summary>
        public const string SizeLimit = "size_limit";

        /// <summary>The operation was cancelled (<see cref="OperationCanceledException"/>).</summary>
        public const string Cancelled = "cancelled";

        /// <summary>A recognised but unimplemented feature (<see cref="NotSupportedException"/>).</summary>
        public const string Unsupported = "unsupported";

        /// <summary>Any other exception, for example an I/O failure while saving.</summary>
        public const string Error = "error";
    }
}
