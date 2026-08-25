using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace EasyImageSharp.AI;

/// <summary>Where a resolved model file came from.</summary>
public enum ModelSource
{
    /// <summary>A local file supplied through <see cref="ImageAiOptions.ModelPathOverrides"/>.</summary>
    Override,

    /// <summary>A previously downloaded (and verified) file in the cache directory.</summary>
    Cache,

    /// <summary>Downloaded and verified during this call.</summary>
    Download,
}

/// <summary>The outcome of <see cref="ModelHub.ResolveAsync"/>.</summary>
/// <param name="Path">Absolute local path of the ONNX file.</param>
/// <param name="FileName">The registry file name that was resolved (fp32 or int8).</param>
/// <param name="IsQuantized">True when the int8 variant was chosen.</param>
/// <param name="Source">Where the file came from.</param>
public sealed record ResolvedModel(string Path, string FileName, bool IsQuantized, ModelSource Source);

/// <summary>
/// Resolves <see cref="ModelDescriptor"/>s to local ONNX files: honours <see cref="ImageAiOptions.ModelPathOverrides"/>,
/// serves from the cache directory, and otherwise downloads over HTTPS (atomic <c>.part</c> + rename, resumable
/// through HTTP <c>Range</c>, retried with exponential back-off) and verifies the pinned SHA-256 before the file
/// becomes visible in the cache. Fail-closed: a file without a pinned checksum is refused unless
/// <see cref="ImageAiOptions.AllowUnverifiedModels"/> is set. Thread-safe; concurrent callers for the same
/// file share one download.
/// </summary>
public sealed class ModelHub : IDisposable
{
    private const int BufferSize = 81920;

    private static readonly HttpClient SharedHttp = CreateDefaultHttpClient();

    // One gate per final path, shared across every hub in the process, so two sessions pointing at the same
    // cache directory never race on the same file.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private bool disposed;

    /// <summary>Creates a hub with default options (Hugging Face sources, default cache, CPU is irrelevant here).</summary>
    public ModelHub()
        : this(new ImageAiOptions())
    {
    }

    /// <summary>Creates a hub over the given options. The options object is read, never mutated.</summary>
    public ModelHub(ImageAiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.Options = options;
        this.CacheDirectory = ResolveCacheDirectory(options.CachePath);

        if (options.HttpClient is not null)
        {
            this.http = options.HttpClient;
        }
        else if (options.HttpMessageHandler is not null)
        {
            this.http = new HttpClient(options.HttpMessageHandler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(30) };
            this.ownsHttp = true;
        }
        else
        {
            this.http = SharedHttp;
        }
    }

    /// <summary>The options this hub was created with.</summary>
    public ImageAiOptions Options { get; }

    /// <summary>The absolute cache directory in effect (created lazily on first download).</summary>
    public string CacheDirectory { get; }

    /// <summary>
    /// The cache directory used when <see cref="ImageAiOptions.CachePath"/> is <c>null</c>: the
    /// <c>EASYIMAGESHARP_CACHE</c> environment variable, else <c>{LocalApplicationData}/EasyImageSharp/models</c>.
    /// </summary>
    public static string DefaultCacheDirectory => ResolveCacheDirectory(null);

    /// <summary>Resolves the effective cache directory for an optional explicit path.</summary>
    public static string ResolveCacheDirectory(string? cachePath)
    {
        if (!string.IsNullOrWhiteSpace(cachePath))
        {
            return Path.GetFullPath(cachePath);
        }

        string? env = Environment.GetEnvironmentVariable(ImageAiOptions.CacheEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyImageSharp",
            "models");
    }

    /// <summary>Computes the upper-case hex SHA-256 of a file; use it to pin checksums for your own descriptors.</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>The URL a file would be downloaded from, after applying the base-URL override / environment variable.</summary>
    public string ResolveUrl(ModelDescriptor descriptor, bool int8 = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        string fileName = int8
            ? descriptor.Int8FileName ?? throw new ArgumentException($"Model '{descriptor.Name}' has no int8 variant.", nameof(int8))
            : descriptor.FileName;
        return this.ResolveUrlCore(descriptor, fileName);
    }

    /// <summary>The path a file has (or would have) inside the cache directory.</summary>
    public string GetCachePath(ModelDescriptor descriptor, bool int8 = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        string fileName = int8
            ? descriptor.Int8FileName ?? throw new ArgumentException($"Model '{descriptor.Name}' has no int8 variant.", nameof(int8))
            : descriptor.FileName;
        ValidateFileName(fileName);
        return Path.Combine(this.CacheDirectory, fileName);
    }

    /// <summary>
    /// True when the model can be resolved without touching the network: a path override exists, or the
    /// (fp32, or preferred int8) file is already in the cache.
    /// </summary>
    public bool IsAvailableLocally(ModelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (this.TryGetOverride(descriptor, out _))
        {
            return true;
        }

        if (this.Options.Quantize && descriptor.Int8FileName is not null && File.Exists(this.GetCachePath(descriptor, int8: true)))
        {
            return true;
        }

        return File.Exists(this.GetCachePath(descriptor));
    }

    /// <summary>
    /// True when the model can be obtained at all with the current options: locally available, or published
    /// (pinned checksum) / unverified downloads allowed and not in offline mode.
    /// </summary>
    public bool CanResolve(ModelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (this.IsAvailableLocally(descriptor))
        {
            return true;
        }

        if (this.Options.Offline)
        {
            return false;
        }

        return descriptor.IsPublished || this.Options.AllowUnverifiedModels;
    }

    /// <summary>Returns the local path of a model, downloading and verifying it first if needed.</summary>
    public async Task<string> EnsureModelAsync(ModelDescriptor descriptor, CancellationToken cancellationToken = default)
        => (await this.ResolveAsync(descriptor, cancellationToken).ConfigureAwait(false)).Path;

    /// <summary>Synchronous <see cref="EnsureModelAsync"/>.</summary>
    public string EnsureModel(ModelDescriptor descriptor, CancellationToken cancellationToken = default)
        => this.EnsureModelAsync(descriptor, cancellationToken).GetAwaiter().GetResult();

    /// <summary>
    /// Resolves a model to a local file, reporting which variant and source were used. Order: path override,
    /// cache hit, download (unless offline). With <see cref="ImageAiOptions.Quantize"/> the int8 variant is
    /// preferred when it is overridden, cached, or published; otherwise the fp32 file is used.
    /// </summary>
    public async Task<ResolvedModel> ResolveAsync(ModelDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ValidateFileName(descriptor.FileName);
        if (descriptor.Int8FileName is not null)
        {
            ValidateFileName(descriptor.Int8FileName);
        }

        if (this.TryGetOverride(descriptor, out string overridePath))
        {
            if (!File.Exists(overridePath))
            {
                throw new FileNotFoundException(
                    $"ModelPathOverrides maps model '{descriptor.Name}' to '{overridePath}' but that file does not exist.", overridePath);
            }

            bool int8Override = string.Equals(Path.GetFileName(overridePath), descriptor.Int8FileName, StringComparison.OrdinalIgnoreCase);
            return new ResolvedModel(Path.GetFullPath(overridePath), Path.GetFileName(overridePath), int8Override, ModelSource.Override);
        }

        (string fileName, string? sha256, bool isInt8) = this.SelectVariant(descriptor);
        string finalPath = Path.Combine(this.CacheDirectory, fileName);
        if (File.Exists(finalPath))
        {
            return new ResolvedModel(finalPath, fileName, isInt8, ModelSource.Cache);
        }

        if (this.Options.Offline)
        {
            throw new OfflineModelMissingException(
                $"Model '{fileName}' is not present in the cache ('{this.CacheDirectory}') and offline mode is enabled. " +
                "Pre-seed the cache, add an ImageAiOptions.ModelPathOverrides entry, or disable ImageAiOptions.Offline.");
        }

        string url = this.ResolveUrlCore(descriptor, fileName);

        SemaphoreSlim gate = FileGates.GetOrAdd(finalPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(finalPath))
            {
                return new ResolvedModel(finalPath, fileName, isInt8, ModelSource.Cache);
            }

            Directory.CreateDirectory(this.CacheDirectory);
            await this.DownloadWithRetryAsync(url, fileName, sha256, finalPath, cancellationToken).ConfigureAwait(false);
            this.Options.Log?.Invoke($"Model {fileName} cached at {finalPath}");
            return new ResolvedModel(finalPath, fileName, isInt8, ModelSource.Download);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Releases the download client if this hub created one.</summary>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        if (this.ownsHttp)
        {
            this.http.Dispose();
        }
    }

    // ----- Resolution helpers -----

    private bool TryGetOverride(ModelDescriptor descriptor, out string path)
    {
        IDictionary<string, string> overrides = this.Options.ModelPathOverrides;
        if (overrides.Count == 0)
        {
            path = string.Empty;
            return false;
        }

        if (overrides.TryGetValue(descriptor.Name, out string? p) && !string.IsNullOrWhiteSpace(p))
        {
            path = p;
            return true;
        }

        if (this.Options.Quantize && descriptor.Int8FileName is not null
            && overrides.TryGetValue(descriptor.Int8FileName, out p) && !string.IsNullOrWhiteSpace(p))
        {
            path = p;
            return true;
        }

        if (overrides.TryGetValue(descriptor.FileName, out p) && !string.IsNullOrWhiteSpace(p))
        {
            path = p;
            return true;
        }

        path = string.Empty;
        return false;
    }

    private (string FileName, string? Sha256, bool IsInt8) SelectVariant(ModelDescriptor descriptor)
    {
        if (this.Options.Quantize && descriptor.Int8FileName is not null)
        {
            bool cached = File.Exists(Path.Combine(this.CacheDirectory, descriptor.Int8FileName));
            if (cached || descriptor.Int8Sha256 is not null || this.Options.AllowUnverifiedModels)
            {
                return (descriptor.Int8FileName, descriptor.Int8Sha256, true);
            }

            this.Options.Log?.Invoke($"Model {descriptor.Name}: no published int8 variant; using fp32.");
        }

        return (descriptor.FileName, descriptor.Sha256, false);
    }

    private string ResolveUrlCore(ModelDescriptor descriptor, string fileName)
    {
        ValidateFileName(fileName);
        string? baseUrl = this.Options.BaseUrlOverride;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = Environment.GetEnvironmentVariable(ImageAiOptions.BaseUrlEnvironmentVariable);
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = descriptor.BaseUrl;
        }

        string url = ModelDescriptor.Combine(baseUrl, fileName);
        this.EnsureSecureSource(url);
        return url;
    }

    private void EnsureSecureSource(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            throw new ModelDownloadException($"Model source '{url}' is not a valid absolute URL.");
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (this.Options.AllowInsecureModelSource && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new ModelDownloadException(
            $"Refusing to download a model from the non-HTTPS source '{url}'. Use an https:// mirror, or set " +
            "ImageAiOptions.AllowInsecureModelSource = true for a trusted on-host mirror.");
    }

    internal static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName == "." || fileName == ".."
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ModelDownloadException(
                $"Refusing to cache a model with the file name '{fileName}': it must be a single path segment.");
        }
    }

    // ----- Download -----

    private async Task DownloadWithRetryAsync(string url, string fileName, string? sha256, string finalPath, CancellationToken cancellationToken)
    {
        string tempPath = finalPath + ".part";
        int maxAttempts = Math.Max(1, this.Options.MaxRetries + 1);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await this.DownloadOnceAsync(url, fileName, tempPath, cancellationToken).ConfigureAwait(false);
                await this.VerifyChecksumAsync(fileName, tempPath, sha256, cancellationToken).ConfigureAwait(false);
                MoveIntoPlace(tempPath, finalPath);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex) && !cancellationToken.IsCancellationRequested)
            {
                TimeSpan delay = TimeSpan.FromMilliseconds(this.Options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                this.Options.Log?.Invoke($"Download of {fileName} failed (attempt {attempt}/{maxAttempts}): {ex.Message}. Retrying in {delay.TotalSeconds:0.0}s.");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void MoveIntoPlace(string tempPath, string finalPath)
    {
        try
        {
            File.Move(tempPath, finalPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            // Another process completed the same download first; ours is a verified duplicate.
            File.Delete(tempPath);
        }
    }

    private async Task DownloadOnceAsync(string url, string fileName, string tempPath, CancellationToken cancellationToken)
    {
        long existing = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
        HttpResponseMessage response = await this.SendAsync(url, existing, cancellationToken).ConfigureAwait(false);
        try
        {
            if (existing > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // The partial file is already as long as (or longer than) the resource: start over.
                response.Dispose();
                File.Delete(tempPath);
                existing = 0;
                response = await this.SendAsync(url, 0, cancellationToken).ConfigureAwait(false);
            }

            bool resuming = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (existing > 0 && !resuming)
            {
                // The server ignored the range and sent the whole file: restart cleanly.
                existing = 0;
                File.Delete(tempPath);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Model download of '{fileName}' from '{url}' failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.",
                    null,
                    response.StatusCode);
            }

            long total = response.Content.Headers.ContentLength is { } length ? length + existing : -1L;
            long downloaded = existing;
            this.Options.Log?.Invoke($"Downloading {fileName} from {url}{(resuming ? $" (resuming at {existing:N0} bytes)" : string.Empty)}");

            FileMode mode = resuming ? FileMode.Append : FileMode.Create;
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var file = new FileStream(tempPath, mode, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

            byte[] buffer = new byte[BufferSize];
            long lastReported = -1;
            long lastReportTicks = Environment.TickCount64;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;
                long now = Environment.TickCount64;
                if (now - lastReportTicks >= 200)
                {
                    this.Options.Progress?.Report(new ModelDownloadProgress(fileName, downloaded, total));
                    lastReported = downloaded;
                    lastReportTicks = now;
                }
            }

            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (downloaded != lastReported)
            {
                this.Options.Progress?.Report(new ModelDownloadProgress(fileName, downloaded, total));
            }
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string url, long rangeStart, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (rangeStart > 0)
        {
            request.Headers.Range = new RangeHeaderValue(rangeStart, null);
        }

        return await this.http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyChecksumAsync(string fileName, string tempPath, string? expectedSha256, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(expectedSha256))
        {
            if (this.Options.AllowUnverifiedModels)
            {
                this.Options.Log?.Invoke($"Model {fileName} has no pinned checksum; accepted because AllowUnverifiedModels is set.");
                return;
            }

            File.Delete(tempPath);
            throw new ModelChecksumException(
                $"Downloaded model '{fileName}' has no pinned SHA-256 to verify against (it is not yet published in the registry). " +
                "Supply your own file through ImageAiOptions.ModelPathOverrides, or set ImageAiOptions.AllowUnverifiedModels = true " +
                "to accept unverified files from a mirror you trust.");
        }

        string actual = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tempPath);
            throw new ModelChecksumException(
                $"Downloaded model '{fileName}' failed SHA-256 verification: expected {expectedSha256.ToUpperInvariant()}, got {actual}. " +
                "The file has been deleted.");
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        ImageAiException => false, // checksum / policy failures: retrying cannot help
        HttpRequestException => true,
        IOException => true,
        TaskCanceledException => true, // HttpClient timeout (user cancellation is filtered by the caller)
        _ => false,
    };

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AllowAutoRedirect = true,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EasyImageSharp.AI/0.2");
        return client;
    }
}
