using EasyImageSharp.Formats;

namespace EasyImageSharp.Extensions.DependencyInjection;

/// <summary>
/// Application-wide EasyImageSharp settings registered by
/// <c>services.AddEasyImageSharp(...)</c>: the decoder resource limits, the per-operation pixel-buffer
/// budget and the parallelism applied to every <see cref="DecoderOptions"/> and
/// <see cref="ImageExecutionOptions"/> that <see cref="IImageOptionsFactory"/> creates.
/// </summary>
/// <remarks>
/// <para>
/// Every property is a plain scalar so the whole type binds from an <c>IConfiguration</c> section
/// (by default <c>"EasyImageSharp"</c>, see <see cref="DefaultSectionName"/>) through the configuration
/// binding source generator, without reflection. The defaults equal the library defaults, so registering
/// the services without configuring anything changes no behaviour.
/// </para>
/// <para>
/// The values are validated when the host starts: every limit must be positive, and the parallelism
/// settings must be at least 1 when they are set. An invalid value fails start-up with an
/// <c>OptionsValidationException</c> rather than surfacing on the first image request.
/// </para>
/// </remarks>
public sealed class EasyImageSharpOptions
{
    /// <summary>The configuration section the settings are conventionally read from: <c>"EasyImageSharp"</c>.</summary>
    public const string DefaultSectionName = "EasyImageSharp";

    /// <summary>
    /// The maximum number of pixels one decoded frame may contain; see <see cref="DecoderOptions.MaxPixels"/>.
    /// Defaults to <see cref="DecoderOptions.DefaultMaxPixels"/> (256 megapixels).
    /// </summary>
    public long MaxPixels { get; set; } = DecoderOptions.DefaultMaxPixels;

    /// <summary>
    /// The maximum number of pixels one decode may allocate across all of its frames; see
    /// <see cref="DecoderOptions.MaxTotalPixels"/>. Defaults to <see cref="DecoderOptions.DefaultMaxTotalPixels"/>.
    /// </summary>
    public long MaxTotalPixels { get; set; } = DecoderOptions.DefaultMaxTotalPixels;

    /// <summary>
    /// The maximum number of frames (animation frames, TIFF pages) a decode reads; see
    /// <see cref="DecoderOptions.MaxFrames"/>. Defaults to <see cref="int.MaxValue"/> (unlimited).
    /// </summary>
    public int MaxFrames { get; set; } = int.MaxValue;

    /// <summary>
    /// The maximum number of encoded bytes a decode accepts, including from non-seekable streams; see
    /// <see cref="DecoderOptions.MaxEncodedBytes"/>. Defaults to <see cref="int.MaxValue"/>.
    /// </summary>
    public long MaxEncodedBytes { get; set; } = int.MaxValue;

    /// <summary>
    /// The cumulative pixel-buffer allocation budget of one operation, in bytes; see
    /// <see cref="ImageExecutionOptions.MaxPixelBufferBytes"/>. It applies separately to each decode and to
    /// each processing call that receives the created options. Defaults to <see cref="long.MaxValue"/> (no budget).
    /// </summary>
    public long MaxPixelBufferBytes { get; set; } = long.MaxValue;

    /// <summary>
    /// The maximum number of threads one operation may use for row-parallel work, or <see langword="null"/> (the
    /// default) to use the process-wide <see cref="Configuration.Default"/>. Set 1 for fully serial processing,
    /// which is often the right choice in a web server where requests already run in parallel.
    /// </summary>
    public int? MaxDegreeOfParallelism { get; set; }

    /// <summary>
    /// The minimum number of pixels per parallel work item, or <see langword="null"/> (the default) to use the
    /// process-wide <see cref="Configuration.Default"/>; see <see cref="Configuration.MinimumPixelsPerTask"/>.
    /// </summary>
    public int? MinimumPixelsPerTask { get; set; }
}
