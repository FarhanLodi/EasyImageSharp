using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using EasyImageSharp.Extensions.DependencyInjection;
using EasyImageSharp.Formats;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace EasyImageSharp.Web.Internal;

/// <summary>
/// The image middleware. A request is handled when it is a GET or HEAD under
/// <see cref="EasyImageSharpWebOptions.RequestPathPrefix"/>, its path ends in the extension of a decodable format,
/// and its query carries at least one command from <see cref="ImageCommandNames"/>; everything else goes to the
/// next middleware untouched (so <c>UseStaticFiles</c> placed after it still serves originals).
/// </summary>
/// <remarks>
/// A handled request runs, in order: path safety (404), signature and expiry (403), command parsing (400), request
/// limits (422), source lookup (404) and size (413), conditional request (304), server cache (200), concurrency
/// limit (503), decode and process (413 / 415 / 422), then the response. The processing slot is released before the
/// response body is written, so slow clients never hold one.
/// </remarks>
internal sealed class EasyImageSharpMiddleware
{
    /// <summary>The <c>Retry-After</c> sent with 503 responses, in seconds.</summary>
    private const string RetryAfterSeconds = "1";

    private static readonly string LibraryVersion = string.Join(
        "+",
        typeof(Image).Assembly.GetName().Version?.ToString() ?? "0",
        typeof(EasyImageSharpMiddleware).Assembly.GetName().Version?.ToString() ?? "0");

    private readonly RequestDelegate next;
    private readonly IOptionsMonitor<EasyImageSharpWebOptions> optionsMonitor;
    private readonly IImageOptionsFactory optionsFactory;
    private readonly IImageSourceProvider[] providers;
    private readonly IImageOutputFormat[] registeredFormats;
    private readonly IImageCache? cache;
    private readonly ImageProcessingLimiter limiter;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly HashSet<string> decodableExtensions;
    private WebSettings? settings;

    public EasyImageSharpMiddleware(RequestDelegate next, IServiceProvider services)
    {
        this.next = next;
        this.optionsMonitor = services.GetRequiredService<IOptionsMonitor<EasyImageSharpWebOptions>>();
        this.optionsFactory = services.GetRequiredService<IImageOptionsFactory>();
        this.registeredFormats = services.GetServices<IImageOutputFormat>().ToArray();
        this.cache = services.GetService<IImageCache>();
        this.limiter = services.GetRequiredService<ImageProcessingLimiter>();
        this.timeProvider = services.GetRequiredService<TimeProvider>();
        this.logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("EasyImageSharp.Web");

        IImageSourceProvider[] registered = services.GetServices<IImageSourceProvider>().ToArray();
        this.providers = registered.Length > 0 ? registered : new IImageSourceProvider[] { CreateDefaultProvider(services, this.optionsMonitor.CurrentValue) };

        this.decodableExtensions = new HashSet<string>(StringComparer.Ordinal);
        foreach (ImageFormat format in ImageFormat.All.Concat(this.optionsFactory.CreateDecoderOptions().AdditionalFormats))
        {
            if (format.CanDecode)
            {
                this.decodableExtensions.UnionWith(format.FileExtensions);
            }
        }

        // Fail at start-up, not on the first request, when the options are unusable.
        _ = this.GetSettings();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        HttpRequest request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            await this.next(context).ConfigureAwait(false);
            return;
        }

        WebSettings current = this.GetSettings();
        if (!request.Path.StartsWithSegments(current.RequestPathPrefix, out PathString remaining)
            || remaining.Value is not { Length: > 1 } path
            || !this.decodableExtensions.Contains(RequestPath.GetExtension(path))
            || !ImageCommands.HasAnyCommand(request.Query))
        {
            await this.next(context).ConfigureAwait(false);
            return;
        }

        try
        {
            await this.HandleAsync(context, current, path).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away; there is nobody to answer.
            Log.ClientDisconnected(this.logger, path);
        }
        catch (ImageRequestException ex) when (!context.Response.HasStarted)
        {
            Log.Rejected(this.logger, path, ex.StatusCode, ex.Message);
            await WriteErrorAsync(context, ex.StatusCode, ex.Message).ConfigureAwait(false);
        }
    }

    internal static string ComputeCacheKey(
        IImageSourceProvider provider,
        string path,
        IImageSource source,
        ImageCommands commands,
        WebSettings settings,
        bool negotiationActive,
        IReadOnlyList<IImageOutputFormat> accepted)
    {
        string output = commands.Format is IImageOutputFormat explicitFormat
            ? "format=" + explicitFormat.Name + ":" + explicitFormat.GetType().FullName
            : negotiationActive
                ? "auto=" + string.Join(",", accepted.Select(f => f.Name + ":" + f.GetType().FullName))
                : "auto=off";

        string material = string.Join(
            "\n",
            "EasyImageSharp.Web/1",
            LibraryVersion,
            settings.Fingerprint,
            provider.GetType().FullName,
            path,
            source.Version,
            commands.ToCanonicalString(),
            output);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(material), hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static IImageSourceProvider CreateDefaultProvider(IServiceProvider services, EasyImageSharpWebOptions options)
    {
        if (options.SourceRootPath is string root)
        {
            return new FileProviderImageSourceProvider(new PhysicalFileProvider(Path.GetFullPath(root)));
        }

        IWebHostEnvironment? environment = services.GetService<IWebHostEnvironment>();
        return new FileProviderImageSourceProvider(environment?.WebRootFileProvider ?? new NullFileProvider());
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string? message)
    {
        HttpResponse response = context.Response;
        response.Clear();
        response.StatusCode = statusCode;
        response.Headers[HeaderNames.CacheControl] = "no-store";
        if (statusCode == StatusCodes.Status503ServiceUnavailable)
        {
            response.Headers[HeaderNames.RetryAfter] = RetryAfterSeconds;
        }

        if (message is not null && !HttpMethods.IsHead(context.Request.Method))
        {
            response.ContentType = "text/plain; charset=utf-8";
            response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
            await response.WriteAsync(message, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<IImageOutputFormat> Negotiate(HttpRequest request, WebSettings settings)
    {
        IList<MediaTypeHeaderValue> accept = request.GetTypedHeaders().Accept;
        if (accept.Count == 0)
        {
            return Array.Empty<IImageOutputFormat>();
        }

        var accepted = new List<IImageOutputFormat>();
        foreach (IImageOutputFormat format in settings.Negotiated)
        {
            foreach (MediaTypeHeaderValue value in accept)
            {
                // Only an explicit listing counts: browsers send image/* and */* while supporting only a few formats.
                if (value.MediaType.Equals(format.MimeType, StringComparison.OrdinalIgnoreCase) && (value.Quality ?? 1) > 0)
                {
                    accepted.Add(format);
                    break;
                }
            }
        }

        return accepted;
    }

    private static bool IsNotModified(HttpRequest request, string etag, DateTimeOffset? lastModified)
    {
        RequestHeaders headers = request.GetTypedHeaders();

        // If-None-Match wins over If-Modified-Since whenever it is present (RFC 9110, 13.1.3).
        if (request.Headers.IfNoneMatch.Count > 0)
        {
            var current = new EntityTagHeaderValue(etag);
            foreach (EntityTagHeaderValue candidate in headers.IfNoneMatch)
            {
                if (candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(current, useStrongComparison: false))
                {
                    return true;
                }
            }

            return false;
        }

        return lastModified is DateTimeOffset modified
            && headers.IfModifiedSince is DateTimeOffset since
            && TruncateToSeconds(modified) <= since;
    }

    private static DateTimeOffset TruncateToSeconds(DateTimeOffset value)
        => new(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    private static async Task<(byte[] Buffer, int Length)> ReadSourceAsync(IImageSource source, long maxBytes, CancellationToken cancellationToken)
    {
        Stream stream;
        try
        {
            stream = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted between the lookup and the read.
            throw new ImageRequestException(StatusCodes.Status404NotFound, "The source image no longer exists.");
        }

        await using (stream.ConfigureAwait(false))
        {
            int capacity = (int)Math.Clamp(source.Length ?? 81_920, 0, Math.Min(maxBytes, Array.MaxLength));
            using var buffer = new MemoryStream(capacity);
            byte[] chunk = ArrayPool<byte>.Shared.Rent(81_920);
            try
            {
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > maxBytes)
                    {
                        throw new ImageRequestException(StatusCodes.Status413PayloadTooLarge, "The source image exceeds the configured size limit.");
                    }

                    buffer.Write(chunk, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }

            return (buffer.GetBuffer(), (int)buffer.Length);
        }
    }

    private WebSettings GetSettings()
    {
        EasyImageSharpWebOptions options = this.optionsMonitor.CurrentValue;
        WebSettings? cached = Volatile.Read(ref this.settings);
        if (cached is not null && ReferenceEquals(cached.Options, options))
        {
            return cached;
        }

        // A reloaded configuration produces a new options instance; rebuilding is cheap and racing threads build
        // equivalent settings, so the last write winning is fine.
        WebSettings created = WebSettings.Create(options, this.registeredFormats);
        Volatile.Write(ref this.settings, created);
        return created;
    }

    private async Task HandleAsync(HttpContext context, WebSettings settings, string path)
    {
        HttpRequest request = context.Request;
        CancellationToken aborted = context.RequestAborted;
        EasyImageSharpWebOptions options = settings.Options;

        if (!RequestPath.IsSafe(path))
        {
            Log.UnsafePath(this.logger);
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, null).ConfigureAwait(false);
            return;
        }

        DateTimeOffset? expiresAt = null;
        if (settings.SigningEnabled)
        {
            switch (this.VerifySignature(request, settings, out expiresAt))
            {
                case SignatureStatus.Valid:
                    break;
                case SignatureStatus.Expired:
                    Log.ExpiredSignature(this.logger, path);
                    await WriteErrorAsync(context, StatusCodes.Status403Forbidden, null).ConfigureAwait(false);
                    return;
                case SignatureStatus.MalformedExpiry:
                    throw new ImageRequestException(StatusCodes.Status400BadRequest, "The 'expires' parameter must be a single Unix time in seconds.");
                default:
                    Log.InvalidSignature(this.logger, path);
                    await WriteErrorAsync(context, StatusCodes.Status403Forbidden, null).ConfigureAwait(false);
                    return;
            }
        }

        if (ImageCommands.TryParse(request.Query, settings, out ImageCommands commands) is string error)
        {
            Log.BadCommand(this.logger, path, error);
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, error).ConfigureAwait(false);
            return;
        }

        EnsureRequestWithinLimits(commands, settings);

        bool negotiationActive = commands.Format is null && options.EnableFormatNegotiation && settings.Negotiated.Count > 0;
        IReadOnlyList<IImageOutputFormat> accepted = negotiationActive ? Negotiate(request, settings) : Array.Empty<IImageOutputFormat>();

        IImageSource? source = null;
        IImageSourceProvider? provider = null;
        foreach (IImageSourceProvider candidate in this.providers)
        {
            source = await candidate.GetSourceAsync(context, path, aborted).ConfigureAwait(false);
            if (source is not null)
            {
                provider = candidate;
                break;
            }
        }

        if (source is null || provider is null)
        {
            Log.SourceNotFound(this.logger, path);
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, null).ConfigureAwait(false);
            return;
        }

        if (source.Length > options.MaxSourceBytes)
        {
            throw new ImageRequestException(StatusCodes.Status413PayloadTooLarge, "The source image exceeds the configured size limit.");
        }

        string key = ComputeCacheKey(provider, path, source, commands, settings, negotiationActive, accepted);
        var headers = new SuccessHeaders(
            "\"" + key[..32] + "\"",
            source.LastModified,
            this.CacheControl(options, expiresAt),
            negotiationActive);

        if (IsNotModified(request, headers.ETag, source.LastModified))
        {
            Log.NotModified(this.logger, path);
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            headers.Apply(context.Response);
            return;
        }

        if (await this.TryServeFromCacheAsync(context, key, headers, path).ConfigureAwait(false))
        {
            return;
        }

        ProcessedImage processed;
        long started;
        using (RateLimitLease lease = await this.limiter.AcquireAsync(aborted).ConfigureAwait(false))
        {
            if (!lease.IsAcquired)
            {
                Log.Saturated(this.logger, path);
                await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, null).ConfigureAwait(false);
                return;
            }

            started = Stopwatch.GetTimestamp();
            (byte[] buffer, int length) = await ReadSourceAsync(source, options.MaxSourceBytes, aborted).ConfigureAwait(false);
            processed = this.Process(buffer.AsSpan(0, length), commands, settings, accepted, negotiationActive, path, aborted);
        }

        Log.Processed(this.logger, path, processed.Format.MimeType, processed.Length, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        if (this.cache is not null)
        {
            try
            {
                // Not tied to the request: the work is done, and storing it helps the next client even if this one left.
                await this.cache.SetAsync(key, processed.Format.MimeType, processed.Content, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.CacheWriteFailed(this.logger, path, ex);
            }
        }

        HttpResponse response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = processed.Format.MimeType;
        response.ContentLength = processed.Length;
        headers.Apply(response);
        if (!HttpMethods.IsHead(request.Method))
        {
            await response.Body.WriteAsync(processed.Content, aborted).ConfigureAwait(false);
        }
    }

    private ProcessedImage Process(
        ReadOnlySpan<byte> source,
        ImageCommands commands,
        WebSettings settings,
        IReadOnlyList<IImageOutputFormat> accepted,
        bool negotiationActive,
        string path,
        CancellationToken aborted)
    {
        EasyImageSharpWebOptions options = settings.Options;
        DecoderOptions configured = this.optionsFactory.CreateDecoderOptions(aborted);
        var decoderOptions = new DecoderOptions
        {
            MaxPixels = Math.Min(configured.MaxPixels, options.MaxSourcePixels),
            MaxTotalPixels = configured.MaxTotalPixels,
            MaxFrames = configured.MaxFrames,
            MaxEncodedBytes = Math.Min(configured.MaxEncodedBytes, options.MaxSourceBytes),
            ExecutionOptions = configured.ExecutionOptions,
            AdditionalFormats = configured.AdditionalFormats,
        };

        try
        {
            return ImageProcessor.Process(
                source,
                commands,
                settings,
                accepted,
                negotiationActive,
                decoderOptions,
                this.optionsFactory.CreateExecutionOptions(aborted));
        }
        catch (ImageSizeLimitExceededException ex)
        {
            Log.SourceRejected(this.logger, path, StatusCodes.Status413PayloadTooLarge, ex);
            throw new ImageRequestException(StatusCodes.Status413PayloadTooLarge, "The source image exceeds the configured decoding limits.");
        }
        catch (UnknownImageFormatException ex)
        {
            Log.SourceRejected(this.logger, path, StatusCodes.Status415UnsupportedMediaType, ex);
            throw new ImageRequestException(StatusCodes.Status415UnsupportedMediaType, "The source is not an image in a supported format.");
        }
        catch (NotSupportedException ex)
        {
            Log.SourceRejected(this.logger, path, StatusCodes.Status415UnsupportedMediaType, ex);
            throw new ImageRequestException(StatusCodes.Status415UnsupportedMediaType, "The source uses a feature this library does not support.");
        }
        catch (ImageFormatException ex)
        {
            Log.SourceRejected(this.logger, path, StatusCodes.Status422UnprocessableEntity, ex);
            throw new ImageRequestException(StatusCodes.Status422UnprocessableEntity, "The source image is corrupt.");
        }
    }

    private async Task<bool> TryServeFromCacheAsync(HttpContext context, string key, SuccessHeaders headers, string path)
    {
        if (this.cache is null)
        {
            return false;
        }

        CancellationToken aborted = context.RequestAborted;
        IImageCacheEntry? entry;
        try
        {
            entry = await this.cache.GetAsync(key, aborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !aborted.IsCancellationRequested)
        {
            Log.CacheReadFailed(this.logger, path, ex);
            return false;
        }

        if (entry is null)
        {
            return false;
        }

        await using (entry.ConfigureAwait(false))
        {
            Log.ServedFromCache(this.logger, path);
            HttpResponse response = context.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = entry.ContentType;
            response.ContentLength = entry.Length;
            headers.Apply(response);
            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await entry.CopyToAsync(response.Body, aborted).ConfigureAwait(false);
            }
        }

        return true;
    }

    private static void EnsureRequestWithinLimits(ImageCommands commands, WebSettings settings)
    {
        EasyImageSharpWebOptions options = settings.Options;
        if (commands.Width > options.MaxWidth || commands.Height > options.MaxHeight
            || (commands.Width is int width && commands.Height is int height && (long)width * height > options.MaxOutputPixels))
        {
            throw new ImageRequestException(StatusCodes.Status422UnprocessableEntity, "The requested size exceeds the configured output limits.");
        }

        if (commands.HasResize && !settings.IsSizeAllowed(commands.Width, commands.Height))
        {
            throw new ImageRequestException(StatusCodes.Status422UnprocessableEntity, "The requested size is not in the list of allowed sizes.");
        }

        if (commands.Format is IImageOutputFormat format && !settings.IsAllowed(format))
        {
            throw new ImageRequestException(StatusCodes.Status422UnprocessableEntity, "The requested output format is not allowed.");
        }
    }

    private string CacheControl(EasyImageSharpWebOptions options, DateTimeOffset? expiresAt)
    {
        TimeSpan maxAge = options.BrowserCacheMaxAge;
        if (expiresAt is DateTimeOffset expiry)
        {
            TimeSpan remaining = expiry - this.timeProvider.GetUtcNow();
            if (remaining < maxAge)
            {
                maxAge = remaining;
            }
        }

        long seconds = (long)Math.Floor(maxAge.TotalSeconds);
        return seconds <= 0 ? "no-cache" : string.Create(CultureInfo.InvariantCulture, $"public, max-age={seconds}");
    }

    private SignatureStatus VerifySignature(HttpRequest request, WebSettings settings, out DateTimeOffset? expiresAt)
    {
        expiresAt = null;
        Span<byte> tag = stackalloc byte[UrlSignature.TagLength];
        StringValues signature = request.Query[ImageCommandNames.Signature];
        if (signature.Count != 1 || !UrlSignature.TryDecode(signature[0], tag))
        {
            return SignatureStatus.Invalid;
        }

        var parameters = new List<KeyValuePair<string, string>>();
        foreach (KeyValuePair<string, StringValues> parameter in request.Query)
        {
            foreach (string? value in parameter.Value)
            {
                parameters.Add(new KeyValuePair<string, string>(parameter.Key, value ?? string.Empty));
            }
        }

        string fullPath = request.PathBase.Add(request.Path).Value ?? "/";
        if (!UrlSignature.Verify(settings.SigningKeys, UrlSignature.Canonicalize(fullPath, parameters), tag))
        {
            return SignatureStatus.Invalid;
        }

        StringValues expires = request.Query[ImageCommandNames.Expires];
        if (expires.Count == 0)
        {
            return SignatureStatus.Valid;
        }

        if (expires.Count != 1
            || !long.TryParse(expires[0], NumberStyles.None, CultureInfo.InvariantCulture, out long unixSeconds)
            || unixSeconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return SignatureStatus.MalformedExpiry;
        }

        expiresAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        return this.timeProvider.GetUtcNow() >= expiresAt ? SignatureStatus.Expired : SignatureStatus.Valid;
    }

    private enum SignatureStatus
    {
        Invalid,
        Valid,
        Expired,
        MalformedExpiry,
    }

    /// <summary>The validators and caching headers of a successful (200 or 304) response.</summary>
    private readonly record struct SuccessHeaders(string ETag, DateTimeOffset? LastModified, string CacheControl, bool VaryByAccept)
    {
        public void Apply(HttpResponse response)
        {
            IHeaderDictionary headers = response.Headers;
            headers[HeaderNames.ETag] = this.ETag;
            headers[HeaderNames.CacheControl] = this.CacheControl;
            headers[HeaderNames.XContentTypeOptions] = "nosniff";
            if (this.LastModified is DateTimeOffset modified)
            {
                response.GetTypedHeaders().LastModified = TruncateToSeconds(modified);
            }

            if (this.VaryByAccept)
            {
                headers.Append(HeaderNames.Vary, HeaderNames.Accept);
            }
        }
    }
}
