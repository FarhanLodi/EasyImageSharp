using EasyImageSharp.Extensions.DependencyInjection;

namespace EasyImageSharp.Web;

/// <summary>
/// Settings of the image middleware registered by <c>services.AddEasyImageSharpWeb(...)</c> and added to the
/// pipeline by <c>app.UseEasyImageSharp()</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every property is a scalar or a list of strings so the type binds from an <c>IConfiguration</c> section (by
/// convention <c>"EasyImageSharp:Web"</c>, see <see cref="DefaultSectionName"/>) through the configuration binding
/// source generator, without reflection. The values are validated when the host starts.
/// </para>
/// <para>
/// The list properties are empty by default and an empty list means "use the built-in default" (documented on each
/// property). That is deliberate: the configuration binder appends to a list that already has items, so pre-filled
/// defaults could never be replaced from configuration.
/// </para>
/// <para>
/// Decoder limits other than <see cref="MaxSourcePixels"/> and <see cref="MaxSourceBytes"/> (frames, total pixels,
/// the pixel-buffer budget and parallelism) come from <see cref="EasyImageSharpOptions"/>, which
/// <c>AddEasyImageSharpWeb</c> registers as well.
/// </para>
/// <para>
/// Options are read through <c>IOptionsMonitor</c>, so a configuration reload (a rotated signing key, a new
/// limit) applies to the next request, with the exception of <see cref="MaxConcurrentProcessing"/> and
/// <see cref="MaxQueueLength"/>, which size the concurrency limiter once at start-up.
/// </para>
/// </remarks>
public sealed class EasyImageSharpWebOptions
{
    /// <summary>The configuration section the settings are conventionally read from: <c>"EasyImageSharp:Web"</c>.</summary>
    public const string DefaultSectionName = "EasyImageSharp:Web";

    /// <summary>
    /// Only requests whose path starts with this prefix are considered, for example <c>"/images"</c>. The prefix is
    /// removed before the path is handed to the source providers. Must start with <c>/</c>. Defaults to <c>"/"</c>.
    /// </summary>
    public string RequestPathPrefix { get; set; } = "/";

    /// <summary>
    /// The directory the default source provider reads original images from. When <see langword="null"/> (the
    /// default) the application's web root (<c>wwwroot</c>) is used. The directory must exist; it is read once at
    /// start-up. Ignored when a source provider is registered explicitly with <c>AddSourceProvider</c>. Keep originals
    /// that must only be served through signed URLs outside the web root, where static files cannot reach them.
    /// </summary>
    public string? SourceRootPath { get; set; }

    /// <summary>
    /// Base64-encoded HMAC-SHA256 keys, each at least 32 bytes long once decoded. When the list is non-empty, every
    /// request that carries a processing command must carry a valid <c>sig</c> parameter or it is answered with
    /// 403. The first key signs new URLs (<see cref="ImageUrlSigner"/>); every key verifies, so a key is rotated
    /// by adding the new one first and removing the old one once the URLs signed with it have expired.
    /// </summary>
    public IList<string> SigningKeys { get; set; } = new List<string>();

    /// <summary>
    /// The output formats a request may produce, by name (<c>jpeg</c>, <c>png</c>, <c>webp</c>, <c>gif</c>,
    /// <c>bmp</c>, <c>tiff</c>, or the name of a format added with <c>AddOutputFormat</c>). Empty (the default) means
    /// <c>jpeg</c>, <c>png</c>, <c>webp</c> and <c>gif</c>. Requesting any other format is answered with 422.
    /// </summary>
    public IList<string> AllowedFormats { get; set; } = new List<string>();

    /// <summary>
    /// When <see langword="true"/> (the default) a request without an explicit <c>format</c> command is served in the
    /// first of <see cref="NegotiatedFormats"/> that the client lists by name in its <c>Accept</c> header, and every
    /// such response carries <c>Vary: Accept</c>. Wildcards such as <c>image/*</c> never select a negotiated format.
    /// </summary>
    public bool EnableFormatNegotiation { get; set; } = true;

    /// <summary>
    /// The formats format negotiation may choose, in order of preference. Empty (the default) means <c>webp</c>.
    /// Register an AVIF or JPEG XL encoder with <c>AddOutputFormat</c> and list it first to prefer it. Only
    /// single-frame images are negotiated; animations keep their own format.
    /// </summary>
    public IList<string> NegotiatedFormats { get; set; } = new List<string>();

    /// <summary>
    /// An optional allowlist of output sizes as <c>"WIDTHxHEIGHT"</c> strings, where a missing or zero side means
    /// "not specified" (<c>"320x240"</c>, <c>"640x"</c>, <c>"x480"</c>). When the list is non-empty, a request whose
    /// <c>width</c>/<c>height</c> pair is not listed is answered with 422; requests without either command (a pure
    /// transcode) are still bounded by the output limits. Empty (the default) allows any size within the other limits.
    /// </summary>
    public IList<string> AllowedSizes { get; set; } = new List<string>();

    /// <summary>The largest output width in pixels. Larger requested or resulting widths are answered with 422. Defaults to 4096.</summary>
    public int MaxWidth { get; set; } = 4096;

    /// <summary>The largest output height in pixels. Larger requested or resulting heights are answered with 422. Defaults to 4096.</summary>
    public int MaxHeight { get; set; } = 4096;

    /// <summary>
    /// The largest output canvas, in pixels (width × height). Larger outputs are answered with 422. Defaults to
    /// 16 777 216 (16 megapixels).
    /// </summary>
    public long MaxOutputPixels { get; set; } = 16L * 1024 * 1024;

    /// <summary>
    /// The largest encoded source image the middleware reads, in bytes. Larger sources are answered with 413.
    /// Defaults to 33 554 432 (32 MiB).
    /// </summary>
    public long MaxSourceBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>
    /// The largest source frame the middleware decodes, in pixels. The stricter of this and
    /// <see cref="EasyImageSharpOptions.MaxPixels"/> applies; larger sources are answered with 413 before any pixel
    /// memory is allocated. Defaults to 40 000 000 (about 160 MB of RGBA pixels), far below the library's
    /// general-purpose default, because a web server decodes untrusted dimensions concurrently.
    /// </summary>
    public long MaxSourcePixels { get; set; } = 40_000_000;

    /// <summary>
    /// When <see langword="false"/> (the default) a requested box larger than the source is scaled down until it
    /// fits inside the source, so images are never enlarged; <c>pad</c> then keeps the requested canvas and pads
    /// around the unscaled image. Set <see langword="true"/> to allow enlarging.
    /// </summary>
    public bool AllowUpscaling { get; set; }

    /// <summary>
    /// The default of the <c>autoorient</c> command: when <see langword="true"/> (the default) images are rotated
    /// upright according to their EXIF orientation before any other command runs.
    /// </summary>
    public bool AutoOrient { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/> (the default) EXIF and XMP metadata (camera details, GPS position) are removed
    /// from every output; the ICC colour profile is kept. Removing EXIF also removes the orientation tag, so leave
    /// <see cref="AutoOrient"/> on.
    /// </summary>
    public bool StripMetadata { get; set; } = true;

    /// <summary>
    /// The quality (1-100) used when a request has no <c>quality</c> command, or <see langword="null"/> (the default)
    /// for each encoder's own default. Applies to JPEG, WebP and any added format that has a quality setting.
    /// </summary>
    public int? DefaultQuality { get; set; }

    /// <summary>
    /// The colour transparent areas are flattened onto when the output format has no alpha channel (JPEG, BMP) and
    /// the request has no <c>bgcolor</c> command. A CSS colour name or hex string. Defaults to <c>"white"</c>.
    /// </summary>
    public string DefaultBackgroundColor { get; set; } = "white";

    /// <summary>
    /// The <c>Cache-Control: public, max-age</c> sent with processed images. A signed URL with an expiry is never
    /// cached past it. Zero sends <c>no-cache</c>. Defaults to 7 days.
    /// </summary>
    public TimeSpan BrowserCacheMaxAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a processed image stays valid in the server-side cache (<c>UsePhysicalCache</c> /
    /// <c>UseDistributedCache</c>). Entries are also invalidated automatically when the source changes, because the
    /// source's version is part of the cache key. Defaults to 365 days.
    /// </summary>
    public TimeSpan CacheMaxAge { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// How many images may be decoded and processed at the same time. Cache hits and 304 responses do not count.
    /// Defaults to <see cref="Environment.ProcessorCount"/>. Read once at start-up.
    /// </summary>
    public int MaxConcurrentProcessing { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// How many requests may wait for a processing slot. A request arriving when every slot is busy and the queue is
    /// full is answered with 503 and <c>Retry-After</c>. Defaults to 64. Read once at start-up.
    /// </summary>
    public int MaxQueueLength { get; set; } = 64;
}
